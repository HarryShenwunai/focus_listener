using System.Windows;
using System.Windows.Interop;

namespace FocusListener.App;

public partial class MainWindow
{
    private const int SubtitleToggleHotKeyIdV3 = 0x464D;
    private const int SubtitleLockHotKeyIdV3 = 0x464E;

    private ClassroomExperienceControl? _experienceV3;
    private SubtitleWindow? _subtitleWindowV3;
    private LiveTranscriptPreview? _latestTranscriptV3;
    private SessionSurfaceKind? _lastExperienceSurfaceV3;
    private bool _registeredSubtitleToggleV3;
    private bool _registeredSubtitleLockV3;

    private void InitializeAudioExperienceV3()
    {
        UpdateExperienceButtonsV3();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            RegisterExperienceHotKeysV3(handle);
        }
    }

    private bool EnsureAudioSetupV3(bool live)
    {
        if (!live || _activeSettingsV2.AudioSetupCompleted)
        {
            return true;
        }

        MessageBox.Show(
            this,
            "首次使用真实课堂前，请选择你实际使用的麦克风和系统播放设备，并可播放一次轻柔测试音。",
            "先确认音频设备",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        if (!ShowSettingsV3())
        {
            return false;
        }

        if (_activeSettingsV2.AudioSetupCompleted)
        {
            return true;
        }

        MessageBox.Show(this, "当前模式所需的音频设备不可用，课堂尚未开始。请连接设备后重试。",
            "音频设备尚未就绪", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private ClassroomExperienceControl? PrepareAudioExperienceV3(bool live)
    {
        _experienceV3?.Dispose();
        _experienceV3 = null;
        _latestTranscriptV3 = null;
        _lastExperienceSurfaceV3 = null;
        AudioLevelMeter.Value = 0;
        if (!live)
        {
            AudioSourceText.Text = "模拟课堂不采集音频";
            TranscriptionToggleButton.IsEnabled = false;
            SubtitleToggleButton.IsEnabled = false;
            SubtitleLockButton.IsEnabled = false;
            return null;
        }

        TranscriptionToggleButton.IsEnabled = true;
        SubtitleToggleButton.IsEnabled = true;
        SubtitleLockButton.IsEnabled = true;
        _experienceV3 = new ClassroomExperienceControl(
            _activeSettingsV2,
            new Progress<ClassroomAudioActivity>(RenderAudioActivityV3),
            new Progress<LiveTranscriptPreview>(RenderTranscriptV3));
        EnsureSubtitleWindowV3();
        _subtitleWindowV3!.ApplySettings(_activeSettingsV2);
        _subtitleWindowV3.Clear();
        SetSubtitleVisibilityV3(_activeSettingsV2.SubtitleWindowEnabled);
        UpdateExperienceButtonsV3();
        AudioSourceText.Text = $"音频：{AudioCaptureModeDisplay.Chinese(_activeSettingsV2.AudioMode)} · 等待声音";
        return _experienceV3;
    }

    private void RenderAudioActivityV3(ClassroomAudioActivity activity)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RenderAudioActivityV3(activity));
            return;
        }

        AudioSourceText.Text = $"音频：{activity.Source}";
        AudioLevelMeter.Value = activity.Level * 100;
    }

    private void RenderTranscriptV3(LiveTranscriptPreview preview)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RenderTranscriptV3(preview));
            return;
        }

        _latestTranscriptV3 = preview;
        RetryTranscriptionButton.Visibility = preview.State == LiveTranscriptState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_activeSettingsV2.SubtitleWindowEnabled)
        {
            EnsureSubtitleWindowV3();
            _subtitleWindowV3!.Render(preview);
        }
    }

    private void RenderAudioExperienceV3(SessionView view)
    {
        if (_experienceV3 is null)
        {
            return;
        }

        if (view.Surface == SessionSurfaceKind.Question &&
            _lastExperienceSurfaceV3 != SessionSurfaceKind.Question &&
            view.Question?.Evidence is { Excerpt.Length: > 0 } evidence)
        {
            _subtitleWindowV3?.HighlightEvidence(evidence.Excerpt);
        }
        else if (view.Surface == SessionSurfaceKind.Listening &&
                 _lastExperienceSurfaceV3 is SessionSurfaceKind.Question or SessionSurfaceKind.Feedback)
        {
            _subtitleWindowV3?.ResumeLatest();
        }

        if (view.Surface is SessionSurfaceKind.AttentionRating or
            SessionSurfaceKind.Completed or SessionSurfaceKind.Failed)
        {
            _subtitleWindowV3?.Hide();
        }
        else if (_activeSettingsV2.SubtitleWindowEnabled && _subtitleWindowV3?.IsVisible != true)
        {
            _subtitleWindowV3?.Show();
        }

        _lastExperienceSurfaceV3 = view.Surface;
    }

    private void FinishAudioExperienceV3()
    {
        if (_subtitleWindowV3 is not null)
        {
            _activeSettingsV2 = _subtitleWindowV3.CaptureSettings(_activeSettingsV2);
            _subtitleWindowV3.Clear();
            _subtitleWindowV3.Hide();
        }

        _experienceV3?.Dispose();
        _experienceV3 = null;
        _latestTranscriptV3 = null;
        _ = PersistSettingsV3();
    }

    private bool ShowSettingsV3()
    {
        _settingsStoreV2 ??= new FocusInteractionSettingsStore(SettingsPathV2);
        if (_subtitleWindowV3 is not null)
        {
            _activeSettingsV2 = _subtitleWindowV3.CaptureSettings(_activeSettingsV2);
        }

        var window = new SettingsWindow(
            _settingsStoreV2,
            _databasePath,
            DiagnosticsDirectoryV2,
            _sessionTask is null)
        {
            Owner = this,
            Topmost = true
        };
        if (window.ShowDialog() != true)
        {
            return false;
        }

        _activeSettingsV2 = _settingsStoreV2.Load();
        _experienceV3?.Apply(_activeSettingsV2);
        if (_subtitleWindowV3 is not null)
        {
            _subtitleWindowV3.ApplySettings(_activeSettingsV2);
            SetSubtitleVisibilityV3(_activeSettingsV2.SubtitleWindowEnabled);
        }
        UpdateExperienceButtonsV3();
        RegisterExperienceHotKeysV3(new WindowInteropHelper(this).Handle);
        StatusText.Text = _sessionTask is null
            ? "设置已保存。"
            : "音频与字幕设置已更新；提问时间从下一次课堂生效。";
        return true;
    }

    private void AudioSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsV3();

    private void TranscriptionToggle_Click(object sender, RoutedEventArgs e)
    {
        _activeSettingsV2 = _activeSettingsV2 with
        {
            RealTimeTranscriptionEnabled = !_activeSettingsV2.RealTimeTranscriptionEnabled
        };
        _experienceV3?.Apply(_activeSettingsV2);
        if (!_activeSettingsV2.RealTimeTranscriptionEnabled)
        {
            _subtitleWindowV3?.Render(new LiveTranscriptPreview(
                _latestTranscriptV3?.CommittedText ?? string.Empty,
                string.Empty,
                LiveTranscriptState.Paused,
                "实时转写已关闭",
                DateTimeOffset.UtcNow));
        }
        UpdateExperienceButtonsV3();
        _ = PersistSettingsV3();
    }

    private void SubtitleToggle_Click(object sender, RoutedEventArgs e) => ToggleSubtitleV3();

    private void ToggleSubtitleV3()
    {
        _activeSettingsV2 = _activeSettingsV2 with
        {
            SubtitleWindowEnabled = !_activeSettingsV2.SubtitleWindowEnabled
        };
        _experienceV3?.SetSubtitleVisible(_activeSettingsV2.SubtitleWindowEnabled);
        SetSubtitleVisibilityV3(_activeSettingsV2.SubtitleWindowEnabled);
        UpdateExperienceButtonsV3();
        _ = PersistSettingsV3();
    }

    private void SubtitleLock_Click(object sender, RoutedEventArgs e) => ToggleSubtitleLockV3();

    private void ToggleSubtitleLockV3()
    {
        EnsureSubtitleWindowV3();
        var locked = !_subtitleWindowV3!.IsLocked;
        _subtitleWindowV3.SetLocked(locked);
        _activeSettingsV2 = _activeSettingsV2 with { SubtitleClickThrough = locked };
        UpdateExperienceButtonsV3();
        _ = PersistSettingsV3();
    }

    private void RetryTranscription_Click(object sender, RoutedEventArgs e)
    {
        RetryTranscriptionButton.Visibility = Visibility.Collapsed;
        _experienceV3?.RetryTranscription();
        StatusText.Text = "正在重新连接音频与 Gemini Live…";
    }

    private void SetSubtitleVisibilityV3(bool visible)
    {
        if (_subtitleWindowV3 is null)
        {
            return;
        }

        if (visible && _experienceV3 is not null)
        {
            if (!_subtitleWindowV3.IsVisible)
            {
                _subtitleWindowV3.Show();
            }
            if (_latestTranscriptV3 is not null)
            {
                _subtitleWindowV3.Render(_latestTranscriptV3);
            }
        }
        else
        {
            _subtitleWindowV3.Hide();
        }
    }

    private void EnsureSubtitleWindowV3()
    {
        if (_subtitleWindowV3 is not null)
        {
            return;
        }

        _subtitleWindowV3 = new SubtitleWindow { Owner = this };
        _subtitleWindowV3.ApplySettings(_activeSettingsV2);
    }

    private void UpdateExperienceButtonsV3()
    {
        ApplyLanguageToExperienceButtonsV3();
    }

    private async Task PersistSettingsV3()
    {
        try
        {
            _settingsStoreV2 ??= new FocusInteractionSettingsStore(SettingsPathV2);
            if (_subtitleWindowV3 is not null)
            {
                _activeSettingsV2 = _subtitleWindowV3.CaptureSettings(_activeSettingsV2);
            }
            await _settingsStoreV2.SaveAsync(_activeSettingsV2);
        }
        catch (Exception exception)
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                StatusText.Text = $"设置未保存：{exception.Message}";
            }
        }
    }

    private void RegisterExperienceHotKeysV3(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (_registeredSubtitleToggleV3)
        {
            UnregisterHotKey(handle, SubtitleToggleHotKeyIdV3);
        }
        if (_registeredSubtitleLockV3)
        {
            UnregisterHotKey(handle, SubtitleLockHotKeyIdV3);
        }

        _registeredSubtitleToggleV3 = RegisterHotKey(
            handle,
            SubtitleToggleHotKeyIdV3,
            ModifierControl | ModifierShift,
            VirtualKey(_activeSettingsV2.SubtitleToggleKey, 0x53));
        _registeredSubtitleLockV3 = RegisterHotKey(
            handle,
            SubtitleLockHotKeyIdV3,
            ModifierControl | ModifierShift,
            VirtualKey(_activeSettingsV2.SubtitleLockKey, 0x4C));
    }

    private bool HandleExperienceHotKeyV3(int id)
    {
        if (id == SubtitleToggleHotKeyIdV3)
        {
            ToggleSubtitleV3();
            return true;
        }

        if (id == SubtitleLockHotKeyIdV3)
        {
            ToggleSubtitleLockV3();
            return true;
        }

        return false;
    }

    private void CloseAudioExperienceV3(IntPtr handle)
    {
        if (_registeredSubtitleToggleV3)
        {
            UnregisterHotKey(handle, SubtitleToggleHotKeyIdV3);
        }
        if (_registeredSubtitleLockV3)
        {
            UnregisterHotKey(handle, SubtitleLockHotKeyIdV3);
        }
        if (_subtitleWindowV3 is not null)
        {
            _activeSettingsV2 = _subtitleWindowV3.CaptureSettings(_activeSettingsV2);
            _subtitleWindowV3.Close();
            _subtitleWindowV3 = null;
        }
        _experienceV3?.Dispose();
        _experienceV3 = null;
        try
        {
            _settingsStoreV2 ??= new FocusInteractionSettingsStore(SettingsPathV2);
            var settings = _activeSettingsV2;
            Task.Run(() => _settingsStoreV2.SaveAsync(settings)).GetAwaiter().GetResult();
        }
        catch
        {
            // The app is already closing; settings remain recoverable from the previous atomic file.
        }
    }

    private static uint VirtualKey(string value, uint fallback)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z'
            ? normalized[0]
            : fallback;
    }
}
