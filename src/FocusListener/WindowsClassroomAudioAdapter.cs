using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.Wave;

namespace FocusListener;

internal enum ClassroomAudioRoute
{
    Microphone,
    SystemPlayback
}

internal sealed record PcmAudioFrame(
    long TimeBucket,
    ClassroomAudioRoute Route,
    byte[] Pcm16,
    double RootMeanSquare);

internal interface IClassroomAudioSource
{
    IAsyncEnumerable<PcmAudioFrame> CaptureAsync(CancellationToken cancellation);
}

internal sealed class WindowsClassroomAudioAdapter : IClassroomAudioSource
{
    private const int SampleRate = 16_000;
    private const int BufferMilliseconds = 100;
    private const double SystemActivityThreshold = 0.006;
    private static readonly long BucketTicks = TimeSpan.FromMilliseconds(BufferMilliseconds).Ticks;

    public async IAsyncEnumerable<PcmAudioFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var faults = new ConcurrentQueue<Exception>();
        WasapiRecorder? microphone = TryBuildRecorder(loopback: false, faults);
        WasapiRecorder? playback = TryBuildRecorder(loopback: true, faults);
        if (microphone is null && playback is null)
        {
            throw new AggregateException("No Windows audio capture endpoint could be opened.", faults);
        }

        using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var frames = Channel.CreateBounded<PcmAudioFrame>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var pumps = new List<Task>(2);
        if (microphone is not null)
        {
            pumps.Add(PumpAsync(microphone, ClassroomAudioRoute.Microphone, frames.Writer, faults, captureLifetime.Token));
        }

        if (playback is not null)
        {
            pumps.Add(PumpAsync(playback, ClassroomAudioRoute.SystemPlayback, frames.Writer, faults, captureLifetime.Token));
        }

        var completion = CompleteChannelAsync(pumps, frames.Writer, faults, captureLifetime.Token);
        var buckets = new SortedDictionary<long, AudioBucket>();
        long latestBucket = long.MinValue;

        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellation))
            {
                latestBucket = Math.Max(latestBucket, frame.TimeBucket);
                if (!buckets.TryGetValue(frame.TimeBucket, out var bucket))
                {
                    bucket = new AudioBucket();
                    buckets.Add(frame.TimeBucket, bucket);
                }

                bucket.Add(frame);
                foreach (var ready in DrainReady(buckets, latestBucket - 2))
                {
                    yield return ready;
                }
            }

            foreach (var ready in DrainReady(buckets, long.MaxValue))
            {
                yield return ready;
            }

            await completion;
        }
        finally
        {
            captureLifetime.Cancel();
            await IgnoreCaptureCancellationAsync(completion);
            if (microphone is not null)
            {
                await microphone.DisposeAsync();
            }

            if (playback is not null)
            {
                await playback.DisposeAsync();
            }
        }
    }

    private static WasapiRecorder? TryBuildRecorder(bool loopback, ConcurrentQueue<Exception> faults)
    {
        try
        {
            var builder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(BufferMilliseconds)
                .WithFormat(new WaveFormat(SampleRate, 16, 1))
                .WithMmcssThreadPriority("Capture");
            if (loopback)
            {
                builder.WithLoopbackCapture();
            }
            else
            {
                builder.WithCommunicationsMode();
            }

            return builder.Build();
        }
        catch (Exception exception)
        {
            faults.Enqueue(exception);
            return null;
        }
    }

    private static async Task PumpAsync(
        WasapiRecorder recorder,
        ClassroomAudioRoute route,
        ChannelWriter<PcmAudioFrame> output,
        ConcurrentQueue<Exception> faults,
        CancellationToken cancellation)
    {
        try
        {
            await foreach (var buffer in recorder.CaptureAsync(cancellation))
            {
                var pcm = buffer.Data.ToArray();
                if (pcm.Length == 0)
                {
                    continue;
                }

                var qpcTicks = buffer.QPCPosition > 0
                    ? buffer.QPCPosition
                    : DateTimeOffset.UtcNow.Ticks;
                output.TryWrite(new PcmAudioFrame(
                    qpcTicks / BucketTicks,
                    route,
                    pcm,
                    CalculateRootMeanSquare(pcm)));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            faults.Enqueue(exception);
        }
    }

    private static async Task CompleteChannelAsync(
        IReadOnlyCollection<Task> pumps,
        ChannelWriter<PcmAudioFrame> writer,
        ConcurrentQueue<Exception> faults,
        CancellationToken cancellation)
    {
        await Task.WhenAll(pumps);
        if (!cancellation.IsCancellationRequested && faults.Count > 0)
        {
            writer.TryComplete(new AggregateException(faults));
        }
        else
        {
            writer.TryComplete();
        }
    }

    private static IEnumerable<PcmAudioFrame> DrainReady(
        SortedDictionary<long, AudioBucket> buckets,
        long inclusiveMaximum)
    {
        var keys = buckets.Keys.TakeWhile(key => key <= inclusiveMaximum).ToArray();
        foreach (var key in keys)
        {
            var bucket = buckets[key];
            buckets.Remove(key);
            var selected = bucket.SystemPlayback is { } system && system.RootMeanSquare >= SystemActivityThreshold
                ? system
                : bucket.Microphone ?? bucket.SystemPlayback;
            if (selected is not null)
            {
                yield return selected;
            }
        }
    }

    private static double CalculateRootMeanSquare(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2)
        {
            return 0;
        }

        double sum = 0;
        var samples = pcm.Length / 2;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8);
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples);
    }

    private static async Task IgnoreCaptureCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class AudioBucket
    {
        public PcmAudioFrame? Microphone { get; private set; }
        public PcmAudioFrame? SystemPlayback { get; private set; }

        public void Add(PcmAudioFrame frame)
        {
            if (frame.Route == ClassroomAudioRoute.SystemPlayback)
            {
                if (SystemPlayback is null || frame.RootMeanSquare > SystemPlayback.RootMeanSquare)
                {
                    SystemPlayback = frame;
                }
            }
            else if (Microphone is null || frame.RootMeanSquare > Microphone.RootMeanSquare)
            {
                Microphone = frame;
            }
        }
    }
}
