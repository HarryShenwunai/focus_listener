using System.Windows;
using System.Windows.Controls;

namespace FocusListener.App;

public partial class MainWindow
{
    private bool _diagnosticsEntryAttached;
    private SystemDiagnosticsWindow? _diagnosticsWindow;

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
            Content = "一键系统检测",
            Margin = new Thickness(0, 10, 0, 0),
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            FontWeight = FontWeights.SemiBold,
            ToolTip = "逐项检测音频、Gemini、转写、题目和本地数据链路"
        };
        button.Click += OpenDiagnostics_Click;
        StartPanel.Children.Add(button);
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
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusListener",
            "diagnostics");
        _diagnosticsWindow = new SystemDiagnosticsWindow(_apiKey, outputDirectory, _settingsStoreV2)
        {
            Owner = this,
            Topmost = true
        };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
        _diagnosticsWindow.Activate();
    }
}
