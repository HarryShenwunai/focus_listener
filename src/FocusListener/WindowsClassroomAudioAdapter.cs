using System.Collections.Concurrent;
using System.Diagnostics;
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

internal sealed class WindowsClassroomAudioAdapter(
    AudioCaptureConfiguration configuration,
    ClassroomExperienceControl? experience = null) : IClassroomAudioSource
{
    private const int BufferMilliseconds = 100;
    private const double SystemActivityThreshold = 0.006;
    private static readonly long BucketStopwatchTicks = Math.Max(
        1,
        Stopwatch.Frequency * BufferMilliseconds / 1_000);

    public async IAsyncEnumerable<PcmAudioFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var faults = new ConcurrentQueue<Exception>();
        var microphone = configuration.Captures(ClassroomAudioRoute.Microphone)
            ? WindowsAudioRecorderFactory.TryCreate(
                ClassroomAudioRoute.Microphone,
                configuration.MicrophoneDeviceId,
                faults)
            : null;
        var playback = configuration.Captures(ClassroomAudioRoute.SystemPlayback)
            ? WindowsAudioRecorderFactory.TryCreate(
                ClassroomAudioRoute.SystemPlayback,
                configuration.SystemPlaybackDeviceId,
                faults)
            : null;
        if (microphone is null && playback is null)
        {
            throw new AggregateException(
                $"无法打开所选音频设备：{configuration.MicrophoneDeviceName} / {configuration.SystemPlaybackDeviceName}",
                faults);
        }

        using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var frames = Channel.CreateBounded<PcmAudioFrame>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var pumps = new List<Task>(2);
        if (microphone is not null)
        {
            pumps.Add(PumpAsync(
                microphone.Recorder,
                ClassroomAudioRoute.Microphone,
                frames.Writer,
                faults,
                captureLifetime.Token));
        }

        if (playback is not null)
        {
            pumps.Add(PumpAsync(
                playback.Recorder,
                ClassroomAudioRoute.SystemPlayback,
                frames.Writer,
                faults,
                captureLifetime.Token));
        }

        var completion = CompleteChannelAsync(pumps, frames.Writer, faults, captureLifetime.Token);
        var buckets = new SortedDictionary<long, AudioArbitrationBucket>();
        long latestBucket = long.MinValue;

        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellation))
            {
                latestBucket = Math.Max(latestBucket, frame.TimeBucket);
                if (!buckets.TryGetValue(frame.TimeBucket, out var bucket))
                {
                    bucket = new AudioArbitrationBucket(SystemActivityThreshold);
                    buckets.Add(frame.TimeBucket, bucket);
                }

                bucket.Add(frame);
                foreach (var ready in DrainReady(buckets, latestBucket - 2, configuration.Mode))
                {
                    ReportActivity(ready);
                    yield return ready;
                }
            }

            foreach (var ready in DrainReady(buckets, long.MaxValue, configuration.Mode))
            {
                ReportActivity(ready);
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

    private void ReportActivity(PcmAudioFrame frame)
    {
        if (experience is null)
        {
            return;
        }

        var source = frame.Route == ClassroomAudioRoute.SystemPlayback
            ? $"系统声音 · {configuration.SystemPlaybackDeviceName}"
            : $"麦克风 · {configuration.MicrophoneDeviceName}";
        experience.ReportAudio(new ClassroomAudioActivity(
            source,
            ToMeter(frame.RootMeanSquare),
            configuration.Mode,
            DateTimeOffset.UtcNow));
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

                output.TryWrite(new PcmAudioFrame(
                    Stopwatch.GetTimestamp() / BucketStopwatchTicks,
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
        SortedDictionary<long, AudioArbitrationBucket> buckets,
        long inclusiveMaximum,
        AudioCaptureMode mode)
    {
        var keys = buckets.Keys.TakeWhile(key => key <= inclusiveMaximum).ToArray();
        foreach (var key in keys)
        {
            var bucket = buckets[key];
            buckets.Remove(key);
            var selected = bucket.Select(mode);
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

    private static double ToMeter(double rootMeanSquare)
    {
        var decibels = 20 * Math.Log10(Math.Max(rootMeanSquare, 0.000001));
        return Math.Clamp((decibels + 60) / 60, 0, 1);
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
}
