using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace FocusListener.App;

internal sealed class AboutWindow : Window
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x17, 0x21, 0x1B));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5B, 0x69, 0x60));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x23, 0x7A, 0x57));
    private readonly FocusInteractionSettingsStore _settingsStore;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _checkUpdate = new();
    private readonly Button _exportBundle = new();

    public AboutWindow(FocusInteractionSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        Title = T("帮助与关于", "Help & About");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xF7));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent();
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(28) };
        root.Children.Add(new TextBlock
        {
            Text = "Focus Listener",
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        root.Children.Add(new TextBlock
        {
            Text = $"Portable Beta · v{ProductRuntime.Version}",
            Margin = new Thickness(0, 4, 0, 14),
            Foreground = Accent,
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = T(
                "成人与高校学习者使用的课堂注意力复位工具。API Key 只保存在 Windows 凭据管理器；不会保存原始音频或完整转写。",
                "A classroom attention-reset tool for adult and higher-education learners. The API key stays in Windows Credential Manager; raw audio and full transcripts are never stored."),
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        });

        if (ProductRuntime.HasPendingCrash)
        {
            root.Children.Add(new Border
            {
                Margin = new Thickness(0, 16, 0, 0),
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xCD)),
                Child = new TextBlock
                {
                    Text = T(
                        "检测到上次异常退出。可导出不含课堂内容的诊断包，然后在 GitHub 手动提交。",
                        "The previous run ended unexpectedly. You can export a diagnostic bundle without classroom content and submit it manually on GitHub."),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0x52, 0x0E)),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        _checkUpdate.Content = T("检查更新", "Check for updates");
        _checkUpdate.Click += CheckUpdate_Click;
        actions.Children.Add(_checkUpdate);
        _exportBundle.Content = T("导出诊断包", "Export diagnostic bundle");
        _exportBundle.Margin = new Thickness(8, 0, 0, 0);
        _exportBundle.Click += ExportBundle_Click;
        actions.Children.Add(_exportBundle);
        var issue = Button(T("打开反馈页面", "Open feedback page"), (_, _) => OpenUrl(
            "https://github.com/HarryShenwunai/focus_listener/issues/new"));
        issue.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(issue);
        root.Children.Add(actions);

        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        links.Children.Add(Button(T("隐私说明", "Privacy"), (_, _) => OpenUrl(
            "https://github.com/HarryShenwunai/focus_listener/blob/main/PRIVACY.md"), transparent: true));
        links.Children.Add(Button("MIT", (_, _) => OpenUrl(
            "https://github.com/HarryShenwunai/focus_listener/blob/main/LICENSE"), transparent: true));
        links.Children.Add(Button("GitHub", (_, _) => OpenUrl(
            "https://github.com/HarryShenwunai/focus_listener"), transparent: true));
        root.Children.Add(links);

        _status.Margin = new Thickness(0, 15, 0, 0);
        _status.Foreground = Muted;
        root.Children.Add(_status);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        _checkUpdate.IsEnabled = false;
        _status.Text = T("正在查询 GitHub Releases…", "Checking GitHub Releases…");
        try
        {
            var result = await GitHubReleaseChecker.CheckAsync();
            _status.Text = result.IsNewer
                ? T($"发现新版本 {result.Tag}。点击这里打开下载页。", $"Version {result.Tag} is available. Click here to open the download page.")
                : T("当前已是最新公开版本。", "You already have the latest public release.");
            if (result.IsNewer)
            {
                _status.Cursor = System.Windows.Input.Cursors.Hand;
                _status.MouseLeftButtonDown += (_, _) => OpenUrl(result.PageUrl);
            }
        }
        catch (Exception exception)
        {
            ProductRuntime.Log("ReleaseCheckFailed", exception);
            _status.Text = T("未能检查更新；请确认网络后重试。", "Could not check for updates. Check your connection and try again.");
        }
        finally
        {
            _checkUpdate.IsEnabled = true;
        }
    }

    private async void ExportBundle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = T("保存 Focus Listener 诊断包", "Save Focus Listener diagnostic bundle"),
            Filter = "ZIP (*.zip)|*.zip",
            FileName = $"focus-listener-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _exportBundle.IsEnabled = false;
        try
        {
            await ProductRuntime.CreateSupportBundleAsync(dialog.FileName, _settingsStore.Load());
            ProductRuntime.AcknowledgeCrash();
            _status.Text = T(
                $"诊断包已保存：{Path.GetFileName(dialog.FileName)}。请自行检查后再提交。",
                $"Saved {Path.GetFileName(dialog.FileName)}. Review it before submitting.");
        }
        catch (Exception exception)
        {
            ProductRuntime.Log("SupportBundleExportFailed", exception);
            _status.Text = T("诊断包导出失败，请换一个可写目录重试。", "Export failed. Choose another writable folder and try again.");
        }
        finally
        {
            _exportBundle.IsEnabled = true;
        }
    }

    private static Button Button(string text, RoutedEventHandler click, bool transparent = false)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 7, 12, 7),
            Background = transparent ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xEF)),
            Foreground = transparent ? Accent : Ink
        };
        button.Click += click;
        return button;
    }

    private static void OpenUrl(string url) => ProductRuntime.TryOpenUrl(url);
    private static string T(string zh, string en) => ProductText.Choose(zh, en);
}
