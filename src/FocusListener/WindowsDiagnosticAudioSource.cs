using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.Wave;

namespace FocusListener;

internal sealed class WindowsDiagnosticAudioSource
{
    private const int SampleRate = 16_000;
    private const int BufferMilliseconds = 100;
    private const double AudibleSignalThreshold = 0.006;
    private static readonly long BucketTicks = TimeSpan.FromMilliseconds(BufferMilliseconds).Ticks;

    public async IAsyncEnumerable<PcmAudioFrame> CaptureAsync(
        IProgress<FocusDiagnosticSignal> progress,
        TimeSpan duration,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var faults = new ConcurrentQueue<Exception>();
        WasapiRecorder? microphone = TryBuildRecorder(loopback: false, faults);
        WasapiRecorder? playback = TryBuildRecorder(loopback: true, faults);

        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.MicrophoneLevel,
            FocusDiagnosticState.Running,
            microphone is null ? "正在确认麦克风端点…" : "已打开，等待你说话",
            0));
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.SystemSoundLevel,
            FocusDiagnosticState.Running,
            playback is null ? "正在确认系统回放端点…" : "已打开，等待电脑播放声音",
            0));
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.AudioRoute,
            FocusDiagnosticState.Running,
            "等待音频信号"));

        if (microphone is null && playback is null)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.MicrophoneLevel,
                FocusDiagnosticState.Failed,
                "无法打开麦克风，请检查 Windows 麦克风权限"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.SystemSoundLevel,
                FocusDiagnosticState.Failed,
                "无法打开默认系统输出设备"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.AudioRoute,
                FocusDiagnosticState.Failed,
                "没有可用的音频输入"));
            yield break;
        }

        using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        captureLifetime.CancelAfter(duration);
        var rawFrames = Channel.CreateBounded<DiagnosticRawFrame>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var pumps = new List<Task>(2);
        if (microphone is not null)
        {
            pumps.Add(PumpAsync(microphone, ClassroomAudioRoute.Microphone, rawFrames.Writer, faults, captureLifetime.Token));
        }

        if (playback is not null)
        {
            pumps.Add(PumpAsync(playback, ClassroomAudioRoute.SystemPlayback, rawFrames.Writer, faults, captureLifetime.Token));
        }

        var completion = CompleteChannelAsync(pumps, rawFrames.Writer);
        var buckets = new SortedDictionary<long, DiagnosticAudioBucket>();
        long latestBucket = long.MinValue;
        double microphonePeak = 0;
        double playbackPeak = 0;
        var microphoneSelections = 0;
        var playbackSelections = 0;

        try
        {
            await foreach (var raw in rawFrames.Reader.ReadAllAsync(cancellation))
            {
                if (raw.Route == ClassroomAudioRoute.Microphone)
                {
                    microphonePeak = Math.Max(microphonePeak, raw.RootMeanSquare);
                    progress.Report(LevelSignal(
                        FocusDiagnosticId.MicrophoneLevel,
                        raw.RootMeanSquare,
                        microphonePeak));
                }
                else
                {
                    playbackPeak = Math.Max(playbackPeak, raw.RootMeanSquare);
                    progress.Report(LevelSignal(
                        FocusDiagnosticId.SystemSoundLevel,
                        raw.RootMeanSquare,
                        playbackPeak));
                }

                latestBucket = Math.Max(latestBucket, raw.TimeBucket);
                if (!buckets.TryGetValue(raw.TimeBucket, out var bucket))
                {
                    bucket = new DiagnosticAudioBucket();
                    buckets.Add(raw.TimeBucket, bucket);
                }

                bucket.Add(raw);
                foreach (var selected in DrainReady(buckets, latestBucket - 2))
                {
                    if (selected.Route == ClassroomAudioRoute.SystemPlayback)
                    {
                        playbackSelections++;
                    }
                    else
                    {
                        microphoneSelections++;
                    }

                    progress.Report(new FocusDiagnosticSignal(
                        FocusDiagnosticId.AudioRoute,
                        FocusDiagnosticState.Running,
                        RouteDetail(selected.Route)));
                    yield return selected;
                }
            }

            foreach (var selected in DrainReady(buckets, long.MaxValue))
            {
                if (selected.Route == ClassroomAudioRoute.SystemPlayback)
                {
                    playbackSelections++;
                }
                else
                {
                    microphoneSelections++;
                }

                progress.Report(new FocusDiagnosticSignal(
                    FocusDiagnosticId.AudioRoute,
                    FocusDiagnosticState.Running,
                    RouteDetail(selected.Route)));
                yield return selected;
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

        ReportFinalLevel(
            progress,
            FocusDiagnosticId.MicrophoneLevel,
            microphone is not null,
            microphonePeak,
            "请检查麦克风权限和输入设备，并在检测时说话");
        ReportFinalLevel(
            progress,
            FocusDiagnosticId.SystemSoundLevel,
            playback is not null,
            playbackPeak,
            "请让电脑播放一段声音，并检查默认输出设备");

        var totalSelections = microphoneSelections + playbackSelections;
        if (totalSelections == 0)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.AudioRoute,
                FocusDiagnosticState.Failed,
                "未取得可仲裁的音频帧"));
        }
        else
        {
            var selectedRoute = playbackSelections > microphoneSelections
                ? ClassroomAudioRoute.SystemPlayback
                : ClassroomAudioRoute.Microphone;
            var activePeak = selectedRoute == ClassroomAudioRoute.SystemPlayback ? playbackPeak : microphonePeak;
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.AudioRoute,
                activePeak >= AudibleSignalThreshold ? FocusDiagnosticState.Passed : FocusDiagnosticState.Warning,
                $"{RouteName(selectedRoute)}为主 · 麦克风 {microphoneSelections} 帧 / 系统声音 {playbackSelections} 帧"));
        }

        if (!faults.IsEmpty && totalSelections > 0)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.AudioRoute,
                FocusDiagnosticState.Warning,
                "音频可用，但有一个采集端点在检测途中中断"));
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
        ChannelWriter<DiagnosticRawFrame> output,
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

                var bucket = DateTimeOffset.UtcNow.Ticks / BucketTicks;
                output.TryWrite(new DiagnosticRawFrame(
                    bucket,
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
        ChannelWriter<DiagnosticRawFrame> writer)
    {
        await Task.WhenAll(pumps);
        writer.TryComplete();
    }

    private static IEnumerable<PcmAudioFrame> DrainReady(
        SortedDictionary<long, DiagnosticAudioBucket> buckets,
        long inclusiveMaximum)
    {
        var keys = buckets.Keys.TakeWhile(key => key <= inclusiveMaximum).ToArray();
        foreach (var key in keys)
        {
            var bucket = buckets[key];
            buckets.Remove(key);
            var selected = bucket.SystemPlayback is { } system && system.RootMeanSquare >= AudibleSignalThreshold
                ? system
                : bucket.Microphone ?? bucket.SystemPlayback;
            if (selected is not null)
            {
                yield return new PcmAudioFrame(
                    selected.TimeBucket,
                    selected.Route,
                    selected.Pcm16,
                    selected.RootMeanSquare);
            }
        }
    }

    private static FocusDiagnosticSignal LevelSignal(
        FocusDiagnosticId id,
        double current,
        double peak) => new(
            id,
            FocusDiagnosticState.Running,
            $"当前 {ToDb(current):0} dBFS · 峰值 {ToDb(peak):0} dBFS",
            ToMeter(current));

    private static void ReportFinalLevel(
        IProgress<FocusDiagnosticSignal> progress,
        FocusDiagnosticId id,
        bool endpointOpened,
        double peak,
        string recovery)
    {
        if (!endpointOpened)
        {
            progress.Report(new FocusDiagnosticSignal(id, FocusDiagnosticState.Failed, recovery, 0));
            return;
        }

        if (peak >= AudibleSignalThreshold)
        {
            progress.Report(new FocusDiagnosticSignal(
                id,
                FocusDiagnosticState.Passed,
                $"检测到声音 · 峰值 {ToDb(peak):0} dBFS",
                ToMeter(peak)));
        }
        else
        {
            progress.Report(new FocusDiagnosticSignal(
                id,
                FocusDiagnosticState.Warning,
                $"设备已打开但接近静音 · {recovery}",
                ToMeter(peak)));
        }
    }

    private static string RouteDetail(ClassroomAudioRoute route) => route switch
    {
        ClassroomAudioRoute.SystemPlayback => "系统声音（检测到电脑播放，优先采用）",
        _ => "麦克风（系统声音静音时采用）"
    };

    private static string RouteName(ClassroomAudioRoute route) => route switch
    {
        ClassroomAudioRoute.SystemPlayback => "系统声音",
        _ => "麦克风"
    };

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

    private static double ToDb(double rootMeanSquare) =>
        20 * Math.Log10(Math.Max(rootMeanSquare, 0.000001));

    private static double ToMeter(double rootMeanSquare) =>
        Math.Clamp((ToDb(rootMeanSquare) + 60) / 60, 0, 1);

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

    private sealed record DiagnosticRawFrame(
        long TimeBucket,
        ClassroomAudioRoute Route,
        byte[] Pcm16,
        double RootMeanSquare);

    private sealed class DiagnosticAudioBucket
    {
        public DiagnosticRawFrame? Microphone { get; private set; }
        public DiagnosticRawFrame? SystemPlayback { get; private set; }

        public void Add(DiagnosticRawFrame frame)
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
