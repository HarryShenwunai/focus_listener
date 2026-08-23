using System.Windows;
using System.Windows.Controls;

namespace FocusListener.App;

public partial class MainWindow
{
    private bool _diagnosticsEntryAttached;
    private bool _onboardingChecked;
    private SystemDiagnosticsWindow? _diagnosticsWindow;
    private AboutWindow? _aboutWindow;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_diagnosticsEntryAttached)
        {
            return;
        }

        _diagnosticsEntryAttached = true;
        var button = new Button
        {
            Content = ProductText.Choose("一键系统检测", "One-click system check"),
            Margin = new Thickness(0, 10, 0, 0),
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            FontWeight = FontWeights.SemiBold,
            ToolTip = ProductText.Choose(
                "逐项检测音频、Gemini、转写、题目和本地数据链路",
                "Check audio, Gemini, transcription, questions, and local data")
        };
        button.Click += OpenDiagnostics_Click;
        StartPanel.Children.Add(button);
        var about = new Button
        {
            Content = ProductText.Choose("帮助与关于", "Help & About"),
            Margin = new Thickness(0, 8, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush")
        };
        about.Click += OpenAbout_Click;
        StartPanel.Children.Add(about);
        ShowOnboardingIfNeeded();
    }

    private void ShowOnboardingIfNeeded()
    {
        if (_onboardingChecked)
        {
            return;
        }

        _onboardingChecked = true;
        _settingsStoreV2 ??= new FocusInteractionSettingsStore(SettingsPathV2);
        if (!_settingsStoreV2.Load().OnboardingCompleted)
        {
            var onboarding = new OnboardingWindow(_settingsStoreV2) { Owner = this, Topmost = true };
            onboarding.ShowDialog();
            _activeSettingsV2 = _settingsStoreV2.Load();
            ProductText.Use(_activeSettingsV2.AppLanguage);
            _apiKey = ReadConfiguredApiKey();
            UpdateModeLabelV2();
        }

        if (ProductRuntime.HasPendingCrash)
        {
            StatusText.Text = ProductText.Choose(
                "检测到上次异常退出；可在“帮助与关于”导出诊断包。",
                "The previous run ended unexpectedly. Export a diagnostic bundle from Help & About.");
        }
    }

    private void OpenAbout_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is { IsVisible: true })
        {
            _aboutWindow.Activate();
            return;
        }

        _settingsStoreV2 ??= new FocusInteractionSettingsStore(SettingsPathV2);
        _aboutWindow = new AboutWindow(_settingsStoreV2) { Owner = this, Topmost = true };
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is { IsVisible: true })
        {
            _diagnosticsWindow.WindowState = WindowState.Normal;
            _diagnosticsWindow.Activate();
            return;
        }

        _settingsStoreV2 ??= new FocusInteractionSettingsStore(SettingsPathV2);
        _diagnosticsWindow = new SystemDiagnosticsWindow(
            _apiKey,
            ProductRuntime.DiagnosticsDirectory,
            _settingsStoreV2)
        {
            Owner = this,
            Topmost = true
        };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
        _diagnosticsWindow.Activate();
    }
}
