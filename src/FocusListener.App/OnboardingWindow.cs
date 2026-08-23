using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusListener.App;

internal sealed class OnboardingWindow : Window
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x17, 0x21, 0x1B));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5B, 0x69, 0x60));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x23, 0x7A, 0x57));
    private readonly FocusInteractionSettingsStore _settingsStore;
    private readonly AudioDevicePicker _audioPicker = new();
    private readonly ComboBox _language = new();
    private readonly CheckBox _adult = new();
    private readonly CheckBox _permission = new();
    private readonly PasswordBox _key = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _connect = new();
    private readonly Button _simulation = new();

    public OnboardingWindow(FocusInteractionSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        Title = "Focus Listener · Welcome / 欢迎";
        Width = 680;
        Height = Math.Min(820, Math.Max(560, SystemParameters.WorkArea.Height - 36));
        MinWidth = 580;
        MinHeight = Math.Min(560, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xF7));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        _language.ItemsSource = new[]
        {
            new LanguageChoice(AppLanguage.System, "Follow Windows / 跟随 Windows"),
            new LanguageChoice(AppLanguage.ZhHans, "简体中文"),
            new LanguageChoice(AppLanguage.English, "English")
        };
        _language.DisplayMemberPath = nameof(LanguageChoice.Label);
        _language.SelectedValuePath = nameof(LanguageChoice.Value);
        var settings = _settingsStore.Load();
        _language.SelectedValue = settings.AppLanguage;
        _audioPicker.Load(settings);
        Content = BuildContent();
        Closed += (_, _) => _audioPicker.Dispose();
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(30) };
        root.Children.Add(new TextBlock
        {
            Text = "Bring your attention back to the lesson.\n把注意力带回正在讲授的内容。",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 36
        });
        root.Children.Add(new TextBlock
        {
            Text = "Focus Listener asks brief, evidence-grounded questions. It is designed for adults and higher-education learners.\nFocus Listener 通过有课堂原话依据的简短问题帮助注意力复位，首个公开版本仅面向成人与高校用户。",
            Margin = new Thickness(0, 10, 0, 18),
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        });

        root.Children.Add(Heading("1  Language / 语言"));
        _language.Padding = new Thickness(10, 7, 10, 7);
        root.Children.Add(_language);

        root.Children.Add(Heading("2  Responsible use / 使用确认"));
        _adult.Content = "I am 18 or older. / 我已年满 18 岁。";
        _adult.Checked += ConsentChanged;
        _adult.Unchecked += ConsentChanged;
        root.Children.Add(_adult);
        _permission.Content = "I have permission to capture and send this audio to Google, and I will not use sensitive, confidential, or personal content.\n我有权采集并向 Google 提交相关音频，不会使用敏感、机密或个人信息。";
        _permission.Margin = new Thickness(0, 10, 0, 0);
        _permission.Checked += ConsentChanged;
        _permission.Unchecked += ConsentChanged;
        root.Children.Add(_permission);
        var terms = LinkButton("Google Gemini API terms / Google Gemini API 条款", "https://ai.google.dev/gemini-api/terms");
        terms.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(terms);

        root.Children.Add(Heading("3  Gemini key / Gemini 密钥"));
        root.Children.Add(new TextBlock
        {
            Text = "Your key is validated before storage and then kept only in Windows Credential Manager. You can skip this and use simulation first.\n密钥验证成功后才会保存到 Windows 凭据管理器；也可以先跳过并体验模拟课堂。",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });
        _key.Margin = new Thickness(0, 9, 0, 0);
        _key.Padding = new Thickness(10, 8, 10, 8);
        _key.MaxLength = 256;
        root.Children.Add(_key);
        root.Children.Add(LinkButton("Get a Gemini API key / 获取 Gemini API Key", "https://aistudio.google.com/apikey"));

        root.Children.Add(Heading("4  Audio / 音频"));
        root.Children.Add(_audioPicker.Build());
        root.Children.Add(new TextBlock
        {
            Text = "After setup, run One-click system check before your first real lesson.\n完成后请在第一次真实课堂前运行“一键系统检测”。",
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });

        _status.Margin = new Thickness(0, 16, 0, 0);
        _status.Foreground = Muted;
        root.Children.Add(_status);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        _simulation.Content = "Use simulation / 使用模拟模式";
        _simulation.IsEnabled = false;
        _simulation.Click += Simulation_Click;
        actions.Children.Add(_simulation);
        _connect.Content = "Validate and finish / 验证并完成";
        _connect.Margin = new Thickness(10, 0, 0, 0);
        _connect.Background = Accent;
        _connect.Foreground = Brushes.White;
        _connect.IsEnabled = false;
        _connect.Click += Connect_Click;
        actions.Children.Add(_connect);
        root.Children.Add(actions);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }

    private void ConsentChanged(object sender, RoutedEventArgs e)
    {
        var enabled = _adult.IsChecked == true && _permission.IsChecked == true;
        _simulation.IsEnabled = enabled;
        _connect.IsEnabled = enabled;
    }

    private async void Simulation_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await SaveSettingsAsync();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ProductRuntime.Log("OnboardingSimulationSaveFailed", exception);
            _status.Text = "Setup could not be saved. Check Windows permissions and retry. / 设置未能保存，请检查 Windows 权限后重试。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var key = _key.Password.Trim();
        if (key.Length < 10)
        {
            _status.Text = "Enter a Gemini API key, or choose simulation. / 请输入 Gemini API Key，或选择模拟模式。";
            return;
        }

        SetBusy(true);
        _status.Text = "Validating with Gemini… / 正在连接 Gemini 验证…";
        try
        {
            var validation = await GeminiCredentialValidator.ValidateAsync(new GeminiFocusOptions(key));
            if (!validation.IsValid)
            {
                _status.Text = validation.State switch
                {
                    GeminiCredentialState.InvalidOrUnauthorized => "The key is invalid or unauthorized. / Key 无效或没有权限。",
                    GeminiCredentialState.NetworkUnavailable => "Gemini could not be reached. Check the network and retry. / 无法连接 Gemini，请检查网络后重试。",
                    _ => "The model or quota is unavailable. Check AI Studio and retry. / 模型或配额当前不可用，请在 AI Studio 检查后重试。"
                };
                return;
            }

            WindowsCredentialStore.WriteApiKey(key);
            await SaveSettingsAsync();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ProductRuntime.Log("OnboardingGeminiValidationFailed", exception);
            _status.Text = "Setup could not be saved. Check Windows permissions and retry. / 设置未能保存，请检查 Windows 权限后重试。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveSettingsAsync()
    {
        var current = _settingsStore.Load();
        var selectedLanguage = _language.SelectedValue is AppLanguage language ? language : AppLanguage.System;
        var settings = _audioPicker.ApplyTo(current with
        {
            AppLanguage = selectedLanguage,
            OnboardingCompleted = true,
            UsageNoticeAccepted = true,
            RetentionDays = 30
        });
        await _settingsStore.SaveAsync(settings);
        ProductText.Use(selectedLanguage);
    }

    private void SetBusy(bool busy)
    {
        _connect.IsEnabled = !busy && _adult.IsChecked == true && _permission.IsChecked == true;
        _simulation.IsEnabled = !busy && _adult.IsChecked == true && _permission.IsChecked == true;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 20, 0, 8),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = Ink
    };

    private static Button LinkButton(string label, string url)
    {
        var button = new Button { Content = label, Background = Brushes.Transparent, Foreground = Accent };
        button.Click += (_, _) => ProductRuntime.TryOpenUrl(url);
        return button;
    }

    private sealed record LanguageChoice(AppLanguage Value, string Label);
}
