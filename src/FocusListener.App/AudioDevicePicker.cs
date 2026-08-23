using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FocusListener.App;

internal sealed class AudioDevicePicker : IDisposable
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x16, 0x24, 0x1B));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x62, 0x70, 0x67));
    private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0xDC, 0xE5, 0xDE));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x83, 0x5A));

    private readonly ComboBox _mode = new();
    private readonly ComboBox _microphone = new();
    private readonly ComboBox _systemOutput = new();
    private readonly TextBlock _message = new();
    private readonly DispatcherTimer _refreshTimer;
    private string? _lastDeviceFingerprint;
    private bool _disposed;

    public AudioDevicePicker()
    {
        _mode.ItemsSource = Enum.GetValues<AudioCaptureMode>()
            .Select(value => new ModeChoice(value, AudioCaptureModeDisplay.Chinese(value)))
            .ToArray();
        _mode.DisplayMemberPath = nameof(ModeChoice.Label);
        _mode.SelectedValuePath = nameof(ModeChoice.Value);
        _mode.SelectedValue = AudioCaptureMode.Automatic;

        ConfigureDeviceCombo(_microphone);
        ConfigureDeviceCombo(_systemOutput);
        _message.Foreground = Muted;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.FontSize = 11;
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            (_, _) => RefreshIfChanged(),
            Dispatcher.CurrentDispatcher);
        _refreshTimer.Start();
    }

    public UIElement Build()
    {
        var root = new StackPanel();
        root.Children.Add(Field(T("音频工作模式", "Audio mode"), _mode, T("自动会采用声音更清晰的一路；智能混合会优先排除扬声器重复声。", "Automatic uses the clearer route; Smart mix avoids duplicated speaker audio.")));
        root.Children.Add(Field(T("麦克风设备", "Microphone"), _microphone, T("请选择你实际说话时使用的麦克风。", "Choose the microphone you actually use.")));
        root.Children.Add(Field(T("系统播放设备", "System output"), _systemOutput, T("请选择正在播放网课、视频或课件声音的输出设备。", "Choose the output device playing the lesson or video.")));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var refresh = Button(T("刷新设备", "Refresh devices"));
        refresh.Click += (_, _) => RefreshDevices(
            _microphone.SelectedValue as string,
            _systemOutput.SelectedValue as string,
            announce: true);
        actions.Children.Add(refresh);
        var tone = Button(T("播放轻柔测试音", "Play gentle test tone"));
        tone.Margin = new Thickness(8, 0, 0, 0);
        tone.Click += PlayTone_Click;
        actions.Children.Add(tone);
        root.Children.Add(actions);
        _message.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_message);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = root
        };
    }

    public void Load(FocusInteractionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _mode.SelectedValue = settings.AudioMode;
        RefreshDevices(settings.MicrophoneDeviceId, settings.SystemPlaybackDeviceId, announce: false,
            settings.MicrophoneDeviceName, settings.SystemPlaybackDeviceName);
    }

    public FocusInteractionSettings ApplyTo(FocusInteractionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var microphone = _microphone.SelectedItem as AudioDeviceInfo;
        var output = _systemOutput.SelectedItem as AudioDeviceInfo;
        var mode = _mode.SelectedValue is AudioCaptureMode selectedMode
            ? selectedMode
            : AudioCaptureMode.Automatic;
        var setupComplete = mode switch
        {
            AudioCaptureMode.Microphone => microphone?.IsAvailable == true,
            AudioCaptureMode.SystemPlayback => output?.IsAvailable == true,
            _ => microphone?.IsAvailable == true || output?.IsAvailable == true
        };
        return settings with
        {
            AudioMode = mode,
            MicrophoneDeviceId = microphone?.Id,
            MicrophoneDeviceName = microphone?.Name,
            SystemPlaybackDeviceId = output?.Id,
            SystemPlaybackDeviceName = output?.Name,
            AudioSetupCompleted = setupComplete
        };
    }

    public AudioCaptureConfiguration CurrentConfiguration =>
        AudioCaptureConfiguration.From(ApplyTo(FocusInteractionSettings.Default));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
    }

    private static void ConfigureDeviceCombo(ComboBox combo)
    {
        combo.MinWidth = 280;
        combo.DisplayMemberPath = nameof(AudioDeviceInfo.DisplayName);
        combo.SelectedValuePath = nameof(AudioDeviceInfo.Id);
        combo.Padding = new Thickness(8, 5, 8, 5);
    }

    private static UIElement Field(string label, Control input, string help)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Ink,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        input.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock
        {
            Text = help,
            Foreground = Muted,
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private static Button Button(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 6, 12, 6),
        Background = Brushes.Transparent,
        Foreground = Accent
    };

    private void RefreshIfChanged()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = WindowsAudioDevices.Enumerate();
        var fingerprint = string.Join('|', snapshot.Microphones.Select(item => item.Id)) + "::" +
                          string.Join('|', snapshot.SystemOutputs.Select(item => item.Id));
        if (string.Equals(fingerprint, _lastDeviceFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        RefreshDevices(
            _microphone.SelectedValue as string,
            _systemOutput.SelectedValue as string,
            announce: _lastDeviceFingerprint is not null);
    }

    private void RefreshDevices(
        string? preferredMicrophone,
        string? preferredOutput,
        bool announce,
        string? missingMicrophoneName = null,
        string? missingOutputName = null)
    {
        var snapshot = WindowsAudioDevices.Enumerate();
        var microphones = WithMissing(snapshot.Microphones, preferredMicrophone, missingMicrophoneName);
        var outputs = WithMissing(snapshot.SystemOutputs, preferredOutput, missingOutputName);
        _microphone.ItemsSource = microphones;
        _systemOutput.ItemsSource = outputs;
        Select(_microphone, preferredMicrophone, microphones);
        Select(_systemOutput, preferredOutput, outputs);
        _lastDeviceFingerprint = string.Join('|', snapshot.Microphones.Select(item => item.Id)) + "::" +
                                 string.Join('|', snapshot.SystemOutputs.Select(item => item.Id));
        _message.Text = snapshot.Error ?? (announce
            ? T($"设备列表已刷新 · 麦克风 {snapshot.Microphones.Count} 个 / 系统输出 {snapshot.SystemOutputs.Count} 个", $"Devices refreshed · {snapshot.Microphones.Count} microphone(s) / {snapshot.SystemOutputs.Count} output(s)")
            : T("设备插拔后会自动刷新；不会擅自切换当前选择。", "The list refreshes after device changes without silently switching your selection."));
    }

    private static IReadOnlyList<AudioDeviceInfo> WithMissing(
        IReadOnlyList<AudioDeviceInfo> devices,
        string? selectedId,
        string? selectedName)
    {
        if (string.IsNullOrWhiteSpace(selectedId) || devices.Any(item => item.Id == selectedId))
        {
            return devices;
        }

        return
        [
            new AudioDeviceInfo(selectedId, selectedName ?? T("上次选择的设备", "Previously selected device"), false, false),
            .. devices
        ];
    }

    private static void Select(ComboBox combo, string? preferredId, IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (!string.IsNullOrWhiteSpace(preferredId) && devices.Any(item => item.Id == preferredId))
        {
            combo.SelectedValue = preferredId;
            return;
        }

        combo.SelectedItem = devices.FirstOrDefault(item => item.IsDefault) ?? devices.FirstOrDefault();
    }

    private async void PlayTone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            _message.Text = T("1 秒后播放轻柔测试音…", "A gentle test tone will play in 1 second…");
            await Task.Delay(TimeSpan.FromSeconds(1));
            await WindowsAudioDevices.PlayTestToneAsync(_systemOutput.SelectedValue as string);
            _message.Text = T("测试音播放完成。系统检测会确认软件能否同时捕获它。", "Test tone completed. System check can confirm whether it was captured.");
        }
        catch (Exception exception)
        {
            ProductRuntime.Log("TestTonePlaybackFailed", exception);
            _message.Text = T("测试音未播放，请确认输出设备后重试。", "The test tone did not play. Confirm the output device and retry.");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string T(string zh, string en) => ProductText.Choose(zh, en);

    private sealed record ModeChoice(AudioCaptureMode Value, string Label);
}
