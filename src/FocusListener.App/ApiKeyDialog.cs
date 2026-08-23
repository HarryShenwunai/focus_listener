using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusListener.App;

internal sealed class ApiKeyDialog : Window
{
    private readonly PasswordBox _apiKey = new();
    private readonly TextBlock _validation = new();

    public ApiKeyDialog(string? currentApiKey)
    {
        Title = T("配置 Gemini API Key", "Configure Gemini API key");
        Width = 430;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(251, 252, 250));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        _apiKey.Password = currentApiKey ?? string.Empty;

        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = T("连接 Gemini 免费层", "Connect Gemini"),
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 33, 27))
        };
        layout.Children.Add(title);

        var help = new TextBlock
        {
            Text = T("密钥验证成功后只保存到当前 Windows 用户的凭据管理器，不写入项目或 SQLite。", "After validation, the key is stored only in Windows Credential Manager, never in the project or SQLite."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(102, 115, 107)),
            Margin = new Thickness(0, 8, 0, 14)
        };
        Grid.SetRow(help, 1);
        layout.Children.Add(help);

        _apiKey.Padding = new Thickness(10, 8, 10, 8);
        _apiKey.FontSize = 14;
        Grid.SetRow(_apiKey, 2);
        layout.Children.Add(_apiKey);

        _validation.Foreground = Brushes.Firebrick;
        _validation.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_validation, 3);
        layout.Children.Add(_validation);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var clear = new Button { Content = T("清除密钥", "Clear key"), Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) =>
        {
            ClearRequested = true;
            DialogResult = true;
        };
        var cancel = new Button { Content = T("取消", "Cancel"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button
        {
            Content = T("保存并使用", "Validate and use"),
            Background = new SolidColorBrush(Color.FromRgb(35, 122, 87)),
            Foreground = Brushes.White
        };
        save.Click += (_, _) => Save();
        actions.Children.Add(clear);
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        Grid.SetRow(actions, 4);
        layout.Children.Add(actions);

        Content = layout;
        Loaded += (_, _) =>
        {
            _apiKey.Focus();
            _apiKey.SelectAll();
        };
    }

    public string? ApiKey { get; private set; }
    public bool ClearRequested { get; private set; }

    private static string T(string zh, string en) => ProductText.Choose(zh, en);

    private void Save()
    {
        var value = _apiKey.Password.Trim();
        if (value.Length < 10)
        {
            _validation.Text = T("请输入有效的 Gemini API Key。", "Enter a valid Gemini API key.");
            return;
        }

        ApiKey = value;
        DialogResult = true;
    }
}
