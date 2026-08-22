namespace FocusListener.Tests;

public sealed class AudioExperienceTests
{
    [Fact]
    public void Configuration_preserves_the_selected_endpoint_ids_and_names()
    {
        var settings = FocusInteractionSettings.Default with
        {
            AudioMode = AudioCaptureMode.SmartMix,
            MicrophoneDeviceId = "microphone-2",
            MicrophoneDeviceName = "教室领夹麦克风",
            SystemPlaybackDeviceId = "speaker-4",
            SystemPlaybackDeviceName = "HDMI 扬声器"
        };

        var configuration = AudioCaptureConfiguration.From(settings);

        Assert.Equal(AudioCaptureMode.SmartMix, configuration.Mode);
        Assert.Equal("microphone-2", configuration.MicrophoneDeviceId);
        Assert.Equal("教室领夹麦克风", configuration.MicrophoneDeviceName);
        Assert.Equal("speaker-4", configuration.SystemPlaybackDeviceId);
        Assert.Equal("HDMI 扬声器", configuration.SystemPlaybackDeviceName);
    }

    [Fact]
    public void Applying_a_device_change_interrupts_the_active_capture_snapshot()
    {
        using var control = new ClassroomExperienceControl(FocusInteractionSettings.Default);
        var before = control.Snapshot();

        control.Apply(FocusInteractionSettings.Default with
        {
            AudioMode = AudioCaptureMode.Microphone,
            MicrophoneDeviceId = "selected-microphone",
            MicrophoneDeviceName = "USB 麦克风"
        });

        var after = control.Snapshot();
        Assert.True(before.ConfigurationChanged.IsCancellationRequested);
        Assert.False(after.ConfigurationChanged.IsCancellationRequested);
        Assert.Equal(AudioCaptureMode.Microphone, after.Audio.Mode);
        Assert.Equal("selected-microphone", after.Audio.MicrophoneDeviceId);
    }

    [Fact]
    public void Device_change_keeps_the_retired_token_safe_for_late_linking()
    {
        using var control = new ClassroomExperienceControl(FocusInteractionSettings.Default);
        var old = control.Snapshot().ConfigurationChanged;

        control.Apply(FocusInteractionSettings.Default with
        {
            AudioMode = AudioCaptureMode.Microphone
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(old);
        Assert.True(old.IsCancellationRequested);
        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public void Disabling_transcription_interrupts_capture_but_subtitle_visibility_does_not()
    {
        using var control = new ClassroomExperienceControl(FocusInteractionSettings.Default);
        var beforeVisibility = control.Snapshot();
        control.SetSubtitleVisible(false);
        var afterVisibility = control.Snapshot();

        Assert.False(beforeVisibility.ConfigurationChanged.IsCancellationRequested);
        Assert.Equal(beforeVisibility.ConfigurationChanged, afterVisibility.ConfigurationChanged);

        control.Apply(FocusInteractionSettings.Default with { RealTimeTranscriptionEnabled = false });
        var disabled = control.Snapshot();
        Assert.True(afterVisibility.ConfigurationChanged.IsCancellationRequested);
        Assert.False(disabled.TranscriptionEnabled);
    }

    [Fact]
    public void Audio_modes_choose_distinct_routes()
    {
        var automatic = BuildBucket().Select(AudioCaptureMode.Automatic);
        var microphone = BuildBucket().Select(AudioCaptureMode.Microphone);
        var system = BuildBucket().Select(AudioCaptureMode.SystemPlayback);
        var smartMix = BuildBucket().Select(AudioCaptureMode.SmartMix);

        Assert.Equal(ClassroomAudioRoute.Microphone, automatic?.Route);
        Assert.Equal(ClassroomAudioRoute.Microphone, microphone?.Route);
        Assert.Equal(ClassroomAudioRoute.SystemPlayback, system?.Route);
        Assert.Equal(ClassroomAudioRoute.SystemPlayback, smartMix?.Route);
    }

    [Fact]
    public void Transcript_preview_keeps_confirmed_and_temporary_text_separate()
    {
        var preview = new LiveTranscriptPreview(
            "速度表示单位时间内通过的路程。",
            "下一步我们比较",
            LiveTranscriptState.Listening,
            "正在形成字幕…",
            DateTimeOffset.UtcNow);

        Assert.Equal("速度表示单位时间内通过的路程。", preview.CommittedText);
        Assert.Equal("下一步我们比较", preview.InterimText);
        Assert.Equal("速度表示单位时间内通过的路程。下一步我们比较", preview.Text);
    }

    private static AudioArbitrationBucket BuildBucket()
    {
        var bucket = new AudioArbitrationBucket(0.006);
        bucket.Add(Frame(ClassroomAudioRoute.Microphone, 4_000));
        bucket.Add(Frame(ClassroomAudioRoute.SystemPlayback, 1_000));
        return bucket;
    }

    private static PcmAudioFrame Frame(ClassroomAudioRoute route, short amplitude)
    {
        var pcm = new byte[320];
        for (var index = 0; index < pcm.Length; index += 2)
        {
            pcm[index] = (byte)(amplitude & 0xFF);
            pcm[index + 1] = (byte)(amplitude >> 8 & 0xFF);
        }

        return new PcmAudioFrame(42, route, pcm, amplitude / 32768d);
    }
}
