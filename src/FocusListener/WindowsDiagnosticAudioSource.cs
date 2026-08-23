using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.Wave;

namespace FocusListener;

internal sealed class WindowsDiagnosticAudioSource(AudioCaptureConfiguration configuration)
{
    private const int BufferMilliseconds = 100;
    private const double AudibleSignalThreshold = 0.006;
    private static readonly long BucketStopwatchTicks = Math.Max(
        1,
        Stopwatch.Frequency * BufferMilliseconds / 1_000);

    public async IAsyncEnumerable<PcmAudioFrame> CaptureAsync(
        IProgress<FocusDiagnosticSignal> progress,
        TimeSpan duration,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var faults = new ConcurrentQueue<Exception>();
        var microphone = WindowsAudioRecorderFactory.TryCreate(
            ClassroomAudioRoute.Microphone,
            configuration.MicrophoneDeviceId,
            faults);
        var playback = WindowsAudioRecorderFactory.TryCreate(
            ClassroomAudioRoute.SystemPlayback,
            configuration.SystemPlaybackDeviceId,
            faults);

        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.MicrophoneLevel,
            FocusDiagnosticState.Running,
            microphone is null
                ? $"无法打开：{configuration.MicrophoneDeviceName}"
                : $"已打开：{configuration.MicrophoneDeviceName} · 请朗读测试句",
            0));
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.SystemSoundLevel,
            FocusDiagnosticState.Running,
            playback is null
                ? $"无法打开：{configuration.SystemPlaybackDeviceName}"
                : $"已打开：{configuration.SystemPlaybackDeviceName} · 将播放轻柔测试音",
            0));
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.AudioRoute,
            FocusDiagnosticState.Running,
            $"{configuration.DisplayName} · 等待音频信号"));

        if (microphone is null && playback is null)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.MicrophoneLevel,
                FocusDiagnosticState.Failed,
                "无法打开所选麦克风；请重新选择设备并检查 Windows 麦克风权限"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.SystemSoundLevel,
                FocusDiagnosticState.Failed,
                "无法打开所选系统输出；请重新选择正在播放声音的设备"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.AudioRoute,
                FocusDiagnosticState.Failed,
                "所选音频设备均不可用"));
            yield break;
        }

        using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        captureLifetime.CancelAfter(duration);
        var rawFrames = Channel.CreateBounded<DiagnosticRawFrame>(new BoundedChannelOptions(128)
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
                rawFrames.Writer,
                faults,
                captureLifetime.Token));
        }

        if (playback is not null)
        {
            pumps.Add(PumpAsync(
                playback.Recorder,
                ClassroomAudioRoute.SystemPlayback,
                rawFrames.Writer,
                faults,
                captureLifetime.Token));
        }

        var completion = CompleteChannelAsync(pumps, rawFrames.Writer);
        var buckets = new SortedDictionary<long, AudioArbitrationBucket>();
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
                        microphonePeak,
                        configuration.MicrophoneDeviceName ?? "所选麦克风"));
                }
                else
                {
                    playbackPeak = Math.Max(playbackPeak, raw.RootMeanSquare);
                    progress.Report(LevelSignal(
                        FocusDiagnosticId.SystemSoundLevel,
                        raw.RootMeanSquare,
                        playbackPeak,
                        configuration.SystemPlaybackDeviceName ?? "所选系统输出"));
                }

                latestBucket = Math.Max(latestBucket, raw.TimeBucket);
                if (!buckets.TryGetValue(raw.TimeBucket, out var bucket))
                {
                    bucket = new AudioArbitrationBucket(AudibleSignalThreshold);
                    buckets.Add(raw.TimeBucket, bucket);
                }

                bucket.Add(new PcmAudioFrame(raw.TimeBucket, raw.Route, raw.Pcm16, raw.RootMeanSquare));
                foreach (var selected in DrainReady(buckets, latestBucket - 2))
                {
                    CountSelection(selected.Route, ref microphoneSelections, ref playbackSelections);
                    progress.Report(RouteSignal(selected.Route));
                    yield return selected;
                }
            }

            foreach (var selected in DrainReady(buckets, long.MaxValue))
            {
                CountSelection(selected.Route, ref microphoneSelections, ref playbackSelections);
                progress.Report(RouteSignal(selected.Route));
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
            configuration.Requires(ClassroomAudioRoute.Microphone),
            microphonePeak,
            $"请确认已选择正在使用的麦克风：{configuration.MicrophoneDeviceName}");
        ReportFinalLevel(
            progress,
            FocusDiagnosticId.SystemSoundLevel,
            playback is not null,
            configuration.Requires(ClassroomAudioRoute.SystemPlayback),
            playbackPeak,
            $"请确认已选择正在播放声音的设备：{configuration.SystemPlaybackDeviceName}");

        var totalSelections = microphoneSelections + playbackSelections;
        if (totalSelections == 0)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.AudioRoute,
                FocusDiagnosticState.Failed,
                $"{configuration.DisplayName}没有取得可用音频帧；请重新选择设备"));
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
                $"{configuration.DisplayName} · {RouteName(selectedRoute)}为主 · " +
                $"麦克风 {microphoneSelections} 帧 / 系统声音 {playbackSelections} 帧"));
        }

        if (!faults.IsEmpty && totalSelections > 0)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.AudioRoute,
                FocusDiagnosticState.Warning,
                "音频可用，但有一个所选设备在检测途中中断"));
        }
    }

    private async Task PumpAsync(
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

                output.TryWrite(new DiagnosticRawFrame(
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
        ChannelWriter<DiagnosticRawFrame> writer)
    {
        await Task.WhenAll(pumps);
        writer.TryComplete();
    }

    private IEnumerable<PcmAudioFrame> DrainReady(
        SortedDictionary<long, AudioArbitrationBucket> buckets,
        long inclusiveMaximum)
    {
        var keys = buckets.Keys.TakeWhile(key => key <= inclusiveMaximum).ToArray();
        foreach (var key in keys)
        {
            var bucket = buckets[key];
            buckets.Remove(key);
            var selected = bucket.Select(configuration.Mode);
            if (selected is not null)
            {
                yield return selected;
            }
        }
    }

    private FocusDiagnosticSignal RouteSignal(ClassroomAudioRoute route) => new(
        FocusDiagnosticId.AudioRoute,
        FocusDiagnosticState.Running,
        $"{configuration.DisplayName} · 正在采用{RouteName(route)}");

    private static void CountSelection(
        ClassroomAudioRoute route,
        ref int microphoneSelections,
        ref int playbackSelections)
    {
        if (route == ClassroomAudioRoute.SystemPlayback)
        {
            playbackSelections++;
        }
        else
        {
            microphoneSelections++;
        }
    }

    private static FocusDiagnosticSignal LevelSignal(
        FocusDiagnosticId id,
        double current,
        double peak,
        string deviceName) => new(
            id,
            FocusDiagnosticState.Running,
            $"{deviceName} · 当前 {ToDb(current):0} dBFS · 峰值 {ToDb(peak):0} dBFS",
            ToMeter(current));

    private static void ReportFinalLevel(
        IProgress<FocusDiagnosticSignal> progress,
        FocusDiagnosticId id,
        bool endpointOpened,
        bool required,
        double peak,
        string recovery)
    {
        if (!endpointOpened)
        {
            progress.Report(new FocusDiagnosticSignal(
                id,
                required ? FocusDiagnosticState.Failed : FocusDiagnosticState.Warning,
                recovery,
                0));
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

    private static string RouteName(ClassroomAudioRoute route) => route == ClassroomAudioRoute.SystemPlayback
        ? "系统声音"
        : "麦克风";

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
}
