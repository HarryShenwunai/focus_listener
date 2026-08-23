using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FocusListener;

public enum AudioCaptureMode
{
    Automatic,
    Microphone,
    SystemPlayback,
    SmartMix
}

public static class AudioCaptureModeDisplay
{
    public static string Chinese(AudioCaptureMode mode) => mode switch
    {
        AudioCaptureMode.Microphone => ProductText.Choose("仅麦克风", "Microphone only"),
        AudioCaptureMode.SystemPlayback => ProductText.Choose("仅系统声音", "System sound only"),
        AudioCaptureMode.SmartMix => ProductText.Choose("智能混合", "Smart mix"),
        _ => ProductText.Choose("自动选择", "Automatic")
    };
}

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    bool IsAvailable = true)
{
    public string DisplayName => IsAvailable
        ? IsDefault ? ProductText.Choose($"{Name}（Windows 默认）", $"{Name} (Windows default)") : Name
        : ProductText.Choose($"{Name}（当前不可用）", $"{Name} (unavailable)");
}

public sealed record AudioDeviceSnapshot(
    IReadOnlyList<AudioDeviceInfo> Microphones,
    IReadOnlyList<AudioDeviceInfo> SystemOutputs,
    string? Error = null);

public sealed record AudioCaptureConfiguration(
    AudioCaptureMode Mode,
    string? MicrophoneDeviceId,
    string? MicrophoneDeviceName,
    string? SystemPlaybackDeviceId,
    string? SystemPlaybackDeviceName)
{
    public static AudioCaptureConfiguration Default { get; } = new(
        AudioCaptureMode.Automatic,
        null,
        ProductText.Choose("Windows 默认麦克风", "Windows default microphone"),
        null,
        ProductText.Choose("Windows 默认系统输出", "Windows default system output"));

    public static AudioCaptureConfiguration From(FocusInteractionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AudioCaptureConfiguration(
            settings.AudioMode,
            EmptyToNull(settings.MicrophoneDeviceId),
            EmptyToNull(settings.MicrophoneDeviceName) ?? ProductText.Choose("Windows 默认麦克风", "Windows default microphone"),
            EmptyToNull(settings.SystemPlaybackDeviceId),
            EmptyToNull(settings.SystemPlaybackDeviceName) ?? ProductText.Choose("Windows 默认系统输出", "Windows default system output"));
    }

    public string DisplayName => AudioCaptureModeDisplay.Chinese(Mode);

    internal bool Captures(ClassroomAudioRoute route) => Mode switch
    {
        AudioCaptureMode.Microphone => route == ClassroomAudioRoute.Microphone,
        AudioCaptureMode.SystemPlayback => route == ClassroomAudioRoute.SystemPlayback,
        _ => true
    };

    internal bool Requires(ClassroomAudioRoute route) => Mode switch
    {
        AudioCaptureMode.Microphone => route == ClassroomAudioRoute.Microphone,
        AudioCaptureMode.SystemPlayback => route == ClassroomAudioRoute.SystemPlayback,
        _ => false
    };

    internal string? DeviceId(ClassroomAudioRoute route) => route == ClassroomAudioRoute.Microphone
        ? MicrophoneDeviceId
        : SystemPlaybackDeviceId;

    internal string DeviceName(ClassroomAudioRoute route) => route == ClassroomAudioRoute.Microphone
        ? MicrophoneDeviceName ?? ProductText.Choose("Windows 默认麦克风", "Windows default microphone")
        : SystemPlaybackDeviceName ?? ProductText.Choose("Windows 默认系统输出", "Windows default system output");

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ClassroomAudioActivity(
    string Source,
    double Level,
    AudioCaptureMode Mode,
    DateTimeOffset UpdatedAt);

public enum LiveTranscriptState
{
    Connecting,
    Listening,
    Reconnecting,
    Paused,
    Failed,
    Stopped
}

public sealed record LiveTranscriptPreview(
    string CommittedText,
    string InterimText,
    LiveTranscriptState State,
    string Status,
    DateTimeOffset UpdatedAt)
{
    public string Text
    {
        get
        {
            var committed = CommittedText.Trim();
            var interim = InterimText.Trim();
            if (committed.Length == 0)
            {
                return interim;
            }

            if (interim.Length == 0 || committed.EndsWith(interim, StringComparison.Ordinal))
            {
                return committed;
            }

            return char.IsPunctuation(committed[^1]) ? committed + interim : committed + " " + interim;
        }
    }
}

public sealed class ClassroomExperienceControl : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _changed = new();
    private readonly List<CancellationTokenSource> _retiredChangeTokens = [];
    private AudioCaptureConfiguration _configuration;
    private bool _transcriptionEnabled;
    private bool _subtitleVisible;
    private bool _disposed;

    public ClassroomExperienceControl(
        FocusInteractionSettings settings,
        IProgress<ClassroomAudioActivity>? audioProgress = null,
        IProgress<LiveTranscriptPreview>? transcriptProgress = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _configuration = AudioCaptureConfiguration.From(settings);
        _transcriptionEnabled = settings.RealTimeTranscriptionEnabled;
        _subtitleVisible = settings.SubtitleWindowEnabled;
        AudioProgress = audioProgress;
        TranscriptProgress = transcriptProgress;
    }

    public bool TranscriptionEnabled
    {
        get
        {
            lock (_gate)
            {
                return _transcriptionEnabled;
            }
        }
    }

    public bool SubtitleVisible
    {
        get
        {
            lock (_gate)
            {
                return _subtitleVisible;
            }
        }
    }

    public ClassroomAudioActivity? LastAudioActivity { get; private set; }

    internal IProgress<ClassroomAudioActivity>? AudioProgress { get; }
    internal IProgress<LiveTranscriptPreview>? TranscriptProgress { get; }

    public void Apply(FocusInteractionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configuration = AudioCaptureConfiguration.From(settings);
        var restart = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            restart = configuration != _configuration ||
                      settings.RealTimeTranscriptionEnabled != _transcriptionEnabled;
            _configuration = configuration;
            _transcriptionEnabled = settings.RealTimeTranscriptionEnabled;
            _subtitleVisible = settings.SubtitleWindowEnabled;
            if (restart)
            {
                RotateChangeToken();
            }
        }
    }

    public void SetSubtitleVisible(bool visible)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _subtitleVisible = visible;
        }
    }

    public void RetryTranscription()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RotateChangeToken();
        }
    }

    internal ClassroomExperienceSnapshot Snapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new ClassroomExperienceSnapshot(
                _configuration,
                _transcriptionEnabled,
                _subtitleVisible,
                _changed.Token);
        }
    }

    internal void ReportAudio(ClassroomAudioActivity activity)
    {
        LastAudioActivity = activity;
        AudioProgress?.Report(activity);
    }

    internal void ReportTranscript(LiveTranscriptPreview preview) => TranscriptProgress?.Report(preview);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _changed.Cancel();
            _changed.Dispose();
            foreach (var retired in _retiredChangeTokens)
            {
                retired.Dispose();
            }
            _retiredChangeTokens.Clear();
        }
    }

    private void RotateChangeToken()
    {
        var previous = _changed;
        _changed = new CancellationTokenSource();
        _retiredChangeTokens.Add(previous);
        previous.Cancel();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed record ClassroomExperienceSnapshot(
    AudioCaptureConfiguration Audio,
    bool TranscriptionEnabled,
    bool SubtitleVisible,
    CancellationToken ConfigurationChanged);

public static class WindowsAudioDevices
{
    public static AudioDeviceSnapshot Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AudioDeviceSnapshot([], [], "音频设备选择仅支持 Windows。");
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var microphoneDefault = DefaultId(enumerator, DataFlow.Capture, Role.Communications);
            var playbackDefault = DefaultId(enumerator, DataFlow.Render, Role.Multimedia);
            return new AudioDeviceSnapshot(
                ReadDevices(enumerator, DataFlow.Capture, microphoneDefault),
                ReadDevices(enumerator, DataFlow.Render, playbackDefault));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AudioDeviceSnapshot([], [], $"无法读取 Windows 音频设备（{exception.GetType().Name}）。");
        }
    }

    public static async Task PlayTestToneAsync(
        string? playbackDeviceId,
        CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("测试音仅支持 Windows。");
        }

        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrWhiteSpace(playbackDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(playbackDeviceId);
        using var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .Build();
        var signal = new SignalGenerator(44_100, 1)
        {
            Frequency = 660,
            Gain = 0.08
        };
        var source = signal.Take(TimeSpan.FromMilliseconds(700)).ToWaveProvider16();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is { } exception)
            {
                stopped.TrySetException(exception);
            }
            else
            {
                stopped.TrySetResult();
            }
        };
        player.Init(source);
        player.Volume = 0.35f;
        player.Play();
        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
        }
        finally
        {
            player.Stop();
        }
    }

    private static IReadOnlyList<AudioDeviceInfo> ReadDevices(
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        string? defaultId)
    {
        using var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var devices = new List<AudioDeviceInfo>(collection.Count);
        for (var index = 0; index < collection.Count; index++)
        {
            using var device = collection[index];
            devices.Add(new AudioDeviceInfo(
                device.ID,
                string.IsNullOrWhiteSpace(device.FriendlyName) ? "未命名音频设备" : device.FriendlyName,
                string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
        }

        return devices
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}

internal sealed class WindowsAudioRecorderLease(
    WasapiRecorder recorder,
    MMDevice? device) : IAsyncDisposable
{
    public WasapiRecorder Recorder { get; } = recorder;

    public async ValueTask DisposeAsync()
    {
        await Recorder.DisposeAsync();
        device?.Dispose();
    }
}

internal static class WindowsAudioRecorderFactory
{
    private const int SampleRate = 16_000;
    private const int BufferMilliseconds = 100;

    public static WindowsAudioRecorderLease? TryCreate(
        ClassroomAudioRoute route,
        string? deviceId,
        ConcurrentQueue<Exception> faults)
    {
        MMDevice? device = null;
        try
        {
            var builder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(BufferMilliseconds)
                .WithFormat(new WaveFormat(SampleRate, 16, 1))
                .WithMmcssThreadPriority("Capture");
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(deviceId);
                builder.WithDevice(device);
            }

            if (route == ClassroomAudioRoute.SystemPlayback)
            {
                builder.WithLoopbackCapture();
            }
            else
            {
                builder.WithCommunicationsMode();
            }

            return new WindowsAudioRecorderLease(builder.Build(), device);
        }
        catch (Exception exception)
        {
            device?.Dispose();
            faults.Enqueue(exception);
            return null;
        }
    }
}
