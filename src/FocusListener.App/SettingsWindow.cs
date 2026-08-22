using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusListener.App;

internal sealed class SettingsWindow : Window
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x16, 0x24, 0x1B));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x62, 0x70, 0x67));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x83, 0x5A));
    private readonly FocusInteractionSettingsStore _store;
    private readonly string _databasePath;
    private readonly string _diagnosticsDirectory;
    private readonly bool _canClearData;
    private readonly Dictionary<string, TextBox> _boxes = [];
    private readonly CheckBox _animation = new() { Content = "题目已准备时显示轻柔呼吸动画" };
    private readonly TextBlock _message = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(
        FocusInteractionSettingsStore store,
        string databasePath,
        string diagnosticsDirectory,
        bool canClearData)
    {
        _store = store;
        _databasePath = databasePath;
        _diagnosticsDirectory = diagnosticsDirectory;
        _canClearData = canClearData;
        Title = "Focus Listener 设置";
        Width = 520;
        Height = 680;
        MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xF7));
        Content = BuildContent();
        Load(_store.Load());
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(26) };
        root.Children.Add(new TextBlock
        {
            Text = "课堂提问设置",
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        root.Children.Add(new TextBlock
        {
            Text = "只显示会影响使用体验的时间。保存后从下一次课堂开始生效。",
            Margin = new Thickness(0, 7, 0, 20),
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });

        AddSection(root, "提问节奏", [
            ("WarmupSeconds", "首次自动提问等待", "0–300 秒", "开始听课后，先留一段安静时间。"),
            ("AutoCooldownSeconds", "自动提问间隔", "30–600 秒", "上一道自动题关闭后重新计时。"),
            ("CandidateLifetimeSeconds", "知识点保留时间", "最多 900 秒", "至少比自动提问间隔多 30 秒。"),
            ("ManualSafetySeconds", "手动题后缓冲", "0–120 秒", "避免手动题刚结束就出现自动题。")
        ]);
        AddSection(root, "答题时间", [
            ("InitialAnswerSeconds", "初始答题时间", "5–30 秒", "到时会折叠成待答题，不会直接丢失。"),
            ("ExtendedAnswerSeconds", "延长后总时间", "最多 60 秒", "必须大于初始答题时间。"),
            ("PendingLifetimeSeconds", "待答题保留时间", "30–600 秒", "折叠后仍可点开继续作答。"),
            ("FeedbackSeconds", "课堂证据显示时间", "1–10 秒", "作答后展示课堂原话的时长。")
        ]);

        var animationPanel = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xE5, 0xDE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 12, 0, 0),
            Child = _animation
        };
        root.Children.Add(animationPanel);
        root.Children.Add(_message);
        _message.Margin = new Thickness(0, 14, 0, 0);

        var actions = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var restore = Button("恢复默认", Restore_Click, Brushes.Transparent, Accent);
        actions.Children.Add(restore);
        var clear = Button("清除本地记录", ClearData_Click, Brushes.Transparent,
            new SolidColorBrush(Color.FromRgb(0x8B, 0x4A, 0x45)));
        clear.IsEnabled = _canClearData;
        clear.ToolTip = _canClearData ? "删除应用数据库和诊断文件" : "请先结束当前课堂";
        Grid.SetColumn(clear, 1);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        actions.Children.Add(clear);
        var cancel = Button("取消", (_, _) => Close(), Brushes.Transparent, Muted);
        Grid.SetColumn(cancel, 2);
        actions.Children.Add(cancel);
        var save = Button("保存", Save_Click, Accent, Brushes.White);
        Grid.SetColumn(save, 3);
        actions.Children.Add(save);
        root.Children.Add(actions);

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
    }

    private void AddSection(
        Panel root,
        string title,
        IReadOnlyList<(string Key, string Label, string Range, string Help)> fields)
    {
        root.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 12, 0, 8),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        foreach (var field in fields)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = field.Label,
                Foreground = Ink,
                VerticalAlignment = VerticalAlignment.Center
            });
            var box = new TextBox
            {
                MinWidth = 72,
                Padding = new Thickness(8, 5, 8, 5),
                HorizontalContentAlignment = HorizontalAlignment.Right
            };
            _boxes[field.Key] = box;
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            var help = new TextBlock
            {
                Text = $"{field.Range}\n{field.Help}",
                Margin = new Thickness(10, 0, 0, 0),
                FontSize = 11,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(help, 2);
            grid.Children.Add(help);
            root.Children.Add(grid);
        }
    }

    private static Button Button(string text, RoutedEventHandler click, Brush background, Brush foreground)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(13, 7, 13, 7),
            Background = background,
            Foreground = foreground
        };
        button.Click += click;
        return button;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var settings, out var error))
        {
            _message.Text = error;
            _message.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x3D, 0x38));
            return;
        }

        try
        {
            await _store.SaveAsync(settings);
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            _message.Text = $"设置未保存：{exception.Message}";
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "恢复所有默认时间和动画设置？", "恢复默认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            Load(FocusInteractionSettings.Default);
            _message.Text = "默认值已填入，点击“保存”后生效。";
        }
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        if (!_canClearData || MessageBox.Show(
                this,
                "这会删除 Focus Listener 的课堂数据库和系统检测文件。已经导出到其他位置的 CSV、设置和 Gemini Key 不会删除。继续吗？",
                "清除本地记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await FocusDataMaintenance.ClearLocalDataAsync(_databasePath, _diagnosticsDirectory);
            _message.Text = "本地课堂记录和诊断文件已清除；设置与 Gemini Key 已保留。";
            _message.Foreground = Accent;
        }
        catch (Exception exception)
        {
            _message.Text = $"未能清除：{exception.Message}";
        }
    }

    private bool TryRead(out FocusInteractionSettings settings, out string error)
    {
        settings = FocusInteractionSettings.Default;
        error = string.Empty;
        var values = new Dictionary<string, int>();
        foreach (var pair in _boxes)
        {
            if (!int.TryParse(pair.Value.Text.Trim(), out var value))
            {
                error = "所有时间都要填写整数秒。";
                return false;
            }
            values[pair.Key] = value;
        }

        settings = new FocusInteractionSettings
        {
            WarmupSeconds = values["WarmupSeconds"],
            AutoCooldownSeconds = values["AutoCooldownSeconds"],
            CandidateLifetimeSeconds = values["CandidateLifetimeSeconds"],
            ManualSafetySeconds = values["ManualSafetySeconds"],
            InitialAnswerSeconds = values["InitialAnswerSeconds"],
            ExtendedAnswerSeconds = values["ExtendedAnswerSeconds"],
            PendingLifetimeSeconds = values["PendingLifetimeSeconds"],
            FeedbackSeconds = values["FeedbackSeconds"],
            CandidateReadyAnimation = _animation.IsChecked != false
        };
        var errors = settings.Validate();
        if (errors.Count == 0)
        {
            return true;
        }

        error = string.Join(Environment.NewLine, errors);
        return false;
    }

    private void Load(FocusInteractionSettings settings)
    {
        _boxes["WarmupSeconds"].Text = settings.WarmupSeconds.ToString();
        _boxes["AutoCooldownSeconds"].Text = settings.AutoCooldownSeconds.ToString();
        _boxes["CandidateLifetimeSeconds"].Text = settings.CandidateLifetimeSeconds.ToString();
        _boxes["ManualSafetySeconds"].Text = settings.ManualSafetySeconds.ToString();
        _boxes["InitialAnswerSeconds"].Text = settings.InitialAnswerSeconds.ToString();
        _boxes["ExtendedAnswerSeconds"].Text = settings.ExtendedAnswerSeconds.ToString();
        _boxes["PendingLifetimeSeconds"].Text = settings.PendingLifetimeSeconds.ToString();
        _boxes["FeedbackSeconds"].Text = settings.FeedbackSeconds.ToString();
        _animation.IsChecked = settings.CandidateReadyAnimation;
    }
}
