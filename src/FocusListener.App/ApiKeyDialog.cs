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
        Title = "配置 Gemini API Key";
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
            Text = "连接 Gemini 免费层",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 33, 27))
        };
        layout.Children.Add(title);

        var help = new TextBlock
        {
            Text = "密钥只保存到当前 Windows 用户的凭据管理器，不写入项目或 SQLite。",
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
        var clear = new Button { Content = "清除密钥", Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) =>
        {
            ClearRequested = true;
            DialogResult = true;
        };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button
        {
            Content = "保存并使用",
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

    private void Save()
    {
        var value = _apiKey.Password.Trim();
        if (value.Length < 10)
        {
            _validation.Text = "请输入有效的 Gemini API Key。";
            return;
        }

        ApiKey = value;
        DialogResult = true;
    }
}
