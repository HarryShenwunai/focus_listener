using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusListener.App;

internal sealed class SettingsWindow : Window
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x16, 0x24, 0x1B));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x62, 0x70, 0x67));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x83, 0x5A));
    private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0xDC, 0xE5, 0xDE));
    private readonly FocusInteractionSettingsStore _store;
    private readonly string _databasePath;
    private readonly string _diagnosticsDirectory;
    private readonly bool _canClearData;
    private readonly Dictionary<string, TextBox> _boxes = [];
    private readonly AudioDevicePicker _audioPicker = new();
    private FocusInteractionSettings _loadedSettings = FocusInteractionSettings.Default;
    private readonly ComboBox _language = new();
    private readonly ComboBox _reminder = new();
    private readonly CheckBox _animation = new() { Content = "题目已准备时显示轻柔呼吸动画" };
    private readonly CheckBox _transcription = new() { Content = "开启实时转写（关闭后自动出题也会暂停）" };
    private readonly CheckBox _subtitle = new() { Content = "显示独立半透明字幕窗" };
    private readonly CheckBox _clickThrough = new() { Content = "字幕窗默认锁定并允许鼠标穿透" };
    private readonly Slider _opacity = new() { Minimum = 0.25, Maximum = 0.9, TickFrequency = 0.05 };
    private readonly Slider _fontSize = new() { Minimum = 18, Maximum = 54, TickFrequency = 1 };
    private readonly TextBox _subtitleKey = new() { MaxLength = 1, Width = 48, TextAlignment = TextAlignment.Center };
    private readonly TextBox _lockKey = new() { MaxLength = 1, Width = 48, TextAlignment = TextAlignment.Center };
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
        if (ProductText.Language == AppLanguage.English)
        {
            _animation.Content = "Show a gentle breathing cue when a question is ready";
            _transcription.Content = "Enable realtime transcription (automatic questions pause when off)";
            _subtitle.Content = "Show the separate translucent subtitle window";
            _clickThrough.Content = "Lock subtitles and allow pointer input to pass through";
        }
        _language.ItemsSource = new[]
        {
            new LanguageChoice(AppLanguage.System, "跟随 Windows / Follow Windows"),
            new LanguageChoice(AppLanguage.ZhHans, "简体中文"),
            new LanguageChoice(AppLanguage.English, "English")
        };
        _language.DisplayMemberPath = nameof(LanguageChoice.Label);
        _language.SelectedValuePath = nameof(LanguageChoice.Value);
        _reminder.ItemsSource = new[] { new ReminderChoice(null, "不提醒 / Off") }
            .Concat(SessionReminderOptions.Minutes.Select(minutes => new ReminderChoice(minutes, $"{minutes} 分钟 / min")))
            .ToArray();
        _reminder.DisplayMemberPath = nameof(ReminderChoice.Label);
        _reminder.SelectedValuePath = nameof(ReminderChoice.Minutes);
        var availableHeight = Math.Max(520, SystemParameters.WorkArea.Height - 32);
        Title = "Focus Listener · " + T("音频、字幕与提问设置", "Audio, subtitles & questions");
        Width = 620;
        Height = Math.Min(780, availableHeight);
        MaxHeight = availableHeight;
        MinWidth = 540;
        MinHeight = Math.Min(580, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xF7));
        Content = BuildContent();
        Load(_store.Load());
        Closed += (_, _) => _audioPicker.Dispose();
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(26) };
        root.Children.Add(new TextBlock
        {
            Text = T("音频、字幕与提问设置", "Audio, subtitles & questions"),
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        root.Children.Add(new TextBlock
        {
            Text = T("音频与字幕设置会立即用于当前课堂；提问时间从下一次课堂开始生效。", "Audio and subtitle changes apply to the current session; question timing applies from the next session."),
            Margin = new Thickness(0, 7, 0, 20),
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(SectionTitle("通用 / General"));
        root.Children.Add(BuildProductPanel());
        root.Children.Add(SectionTitle(T("音频设备", "Audio devices")));
        root.Children.Add(_audioPicker.Build());
        root.Children.Add(SectionTitle(T("实时转写与字幕", "Realtime transcription & subtitles")));
        root.Children.Add(BuildSubtitlePanel());

        AddSection(root, T("提问节奏", "Question cadence"), [
            ("WarmupSeconds", T("首次自动提问等待", "First automatic question delay"), T("0–300 秒", "0–300 sec"), T("开始听课后，先留一段安静时间。", "Leave a quiet period after listening begins.")),
            ("AutoCooldownSeconds", T("自动提问间隔", "Automatic question interval"), T("30–600 秒", "30–600 sec"), T("上一道自动题关闭后重新计时。", "Restarts after the previous automatic question closes.")),
            ("CandidateLifetimeSeconds", T("知识点保留时间", "Knowledge point lifetime"), T("最多 900 秒", "Up to 900 sec"), T("至少比自动提问间隔多 30 秒。", "At least 30 seconds longer than the automatic interval.")),
            ("ManualSafetySeconds", T("手动题后缓冲", "Post-manual buffer"), T("0–120 秒", "0–120 sec"), T("避免手动题刚结束就出现自动题。", "Prevents an automatic question immediately after a manual one."))
        ]);
        AddSection(root, T("答题时间", "Answer timing"), [
            ("InitialAnswerSeconds", T("初始答题时间", "Initial answer time"), T("5–30 秒", "5–30 sec"), T("到时会折叠成待答题，不会直接丢失。", "Collapses into a pending question instead of being lost.")),
            ("ExtendedAnswerSeconds", T("延长后总时间", "Total time after extension"), T("最多 60 秒", "Up to 60 sec"), T("必须大于初始答题时间。", "Must exceed the initial answer time.")),
            ("PendingLifetimeSeconds", T("待答题保留时间", "Pending question lifetime"), T("30–600 秒", "30–600 sec"), T("折叠后仍可点开继续作答。", "A collapsed question can still be reopened.")),
            ("FeedbackSeconds", T("课堂证据显示时间", "Lesson evidence duration"), T("1–10 秒", "1–10 sec"), T("作答后展示课堂原话的时长。", "How long lesson evidence remains after an answer."))
        ]);

        root.Children.Add(Card(_animation));
        root.Children.Add(_message);
        _message.Margin = new Thickness(0, 14, 0, 0);

        var actions = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var restore = Button(T("恢复默认", "Restore defaults"), Restore_Click, Brushes.Transparent, Accent);
        actions.Children.Add(restore);
        var clear = Button(T("清除本地记录", "Clear local records"), ClearData_Click, Brushes.Transparent,
            new SolidColorBrush(Color.FromRgb(0x8B, 0x4A, 0x45)));
        clear.IsEnabled = _canClearData;
        clear.ToolTip = _canClearData ? "删除应用数据库和诊断文件" : "请先结束当前课堂";
        Grid.SetColumn(clear, 1);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        actions.Children.Add(clear);
        var cancel = Button(T("取消", "Cancel"), (_, _) => Close(), Brushes.Transparent, Muted);
        Grid.SetColumn(cancel, 2);
        actions.Children.Add(cancel);
        var save = Button(T("保存", "Save"), Save_Click, Accent, Brushes.White);
        Grid.SetColumn(save, 3);
        actions.Children.Add(save);
        root.Children.Add(actions);

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
    }

    private UIElement BuildProductPanel()
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = "界面语言 / Language", Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
        _language.Padding = new Thickness(8, 5, 8, 5);
        Grid.SetColumn(_language, 1);
        panel.Children.Add(_language);
        var reminderLabel = new TextBlock
        {
            Text = "课堂提醒 / Reminder",
            Foreground = Ink,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(reminderLabel, 1);
        panel.Children.Add(reminderLabel);
        _reminder.Padding = new Thickness(8, 5, 8, 5);
        _reminder.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(_reminder, 1);
        Grid.SetColumn(_reminder, 1);
        panel.Children.Add(_reminder);
        var retention = new TextBlock
        {
            Text = "题目、短证据和答题分析默认保留 30 天；原始音频和完整转写不会保存。\nLanguage changes take effect after restarting Focus Listener.",
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(retention, 2);
        Grid.SetColumnSpan(retention, 2);
        panel.Children.Add(retention);
        return Card(panel);
    }

    private UIElement BuildSubtitlePanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(_transcription);
        _subtitle.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_subtitle);
        _clickThrough.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_clickThrough);
        panel.Children.Add(SliderRow(T("字幕背景浓度", "Subtitle background"), _opacity, "25%", "90%"));
        panel.Children.Add(SliderRow(T("字幕字号", "Subtitle font size"), _fontSize, "18", "54"));

        var shortcuts = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcuts.Children.Add(new TextBlock
        {
            Text = T("显示/隐藏 · Ctrl+Shift+", "Show/hide · Ctrl+Shift+"),
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_subtitleKey, 1);
        shortcuts.Children.Add(_subtitleKey);
        var lockLabel = new TextBlock
        {
            Text = T("锁定/解锁 · Ctrl+Shift+", "Lock/unlock · Ctrl+Shift+"),
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lockLabel, 3);
        shortcuts.Children.Add(lockLabel);
        Grid.SetColumn(_lockKey, 4);
        shortcuts.Children.Add(_lockKey);
        panel.Children.Add(shortcuts);
        panel.Children.Add(new TextBlock
        {
            Text = T("锁定后鼠标会穿过字幕窗；解锁后才能拖动和调整大小。", "When locked, pointer input passes through the subtitle window. Unlock it to move or resize."),
            Margin = new Thickness(0, 7, 0, 0),
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        return Card(panel);
    }

    private void AddSection(
        Panel root,
        string title,
        IReadOnlyList<(string Key, string Label, string Range, string Help)> fields)
    {
        root.Children.Add(SectionTitle(title));
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

    private static string T(string zh, string en) => ProductText.Choose(zh, en);

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 15, 0, 8),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = Ink
    };

    private static Border Card(UIElement content) => new()
    {
        Background = Brushes.White,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14),
        Margin = new Thickness(0, 3, 0, 0),
        Child = content
    };

    private static UIElement SliderRow(string label, Slider slider, string minimum, string maximum)
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
        var min = new TextBlock { Text = minimum, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(min, 1);
        grid.Children.Add(min);
        slider.IsSnapToTickEnabled = true;
        slider.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(slider, 2);
        grid.Children.Add(slider);
        var max = new TextBlock { Text = maximum, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(max, 3);
        grid.Children.Add(max);
        return grid;
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
        if (MessageBox.Show(this, "恢复音频、字幕、时间和动画的默认设置？", "恢复默认",
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

        settings = _loadedSettings with
        {
            WarmupSeconds = values["WarmupSeconds"],
            AutoCooldownSeconds = values["AutoCooldownSeconds"],
            CandidateLifetimeSeconds = values["CandidateLifetimeSeconds"],
            ManualSafetySeconds = values["ManualSafetySeconds"],
            InitialAnswerSeconds = values["InitialAnswerSeconds"],
            ExtendedAnswerSeconds = values["ExtendedAnswerSeconds"],
            PendingLifetimeSeconds = values["PendingLifetimeSeconds"],
            FeedbackSeconds = values["FeedbackSeconds"],
            CandidateReadyAnimation = _animation.IsChecked != false,
            AppLanguage = _language.SelectedValue is AppLanguage language ? language : AppLanguage.System,
            SessionReminderMinutes = _reminder.SelectedValue as int?,
            RetentionDays = 30,
            RealTimeTranscriptionEnabled = _transcription.IsChecked != false,
            SubtitleWindowEnabled = _subtitle.IsChecked != false,
            SubtitleClickThrough = _clickThrough.IsChecked != false,
            SubtitleBackgroundOpacity = _opacity.Value,
            SubtitleFontSize = _fontSize.Value,
            SubtitleToggleKey = _subtitleKey.Text.Trim().ToUpperInvariant(),
            SubtitleLockKey = _lockKey.Text.Trim().ToUpperInvariant()
        };
        settings = _audioPicker.ApplyTo(settings);
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
        _loadedSettings = settings;
        _language.SelectedValue = settings.AppLanguage;
        _reminder.SelectedValue = settings.SessionReminderMinutes;
        _boxes["WarmupSeconds"].Text = settings.WarmupSeconds.ToString();
        _boxes["AutoCooldownSeconds"].Text = settings.AutoCooldownSeconds.ToString();
        _boxes["CandidateLifetimeSeconds"].Text = settings.CandidateLifetimeSeconds.ToString();
        _boxes["ManualSafetySeconds"].Text = settings.ManualSafetySeconds.ToString();
        _boxes["InitialAnswerSeconds"].Text = settings.InitialAnswerSeconds.ToString();
        _boxes["ExtendedAnswerSeconds"].Text = settings.ExtendedAnswerSeconds.ToString();
        _boxes["PendingLifetimeSeconds"].Text = settings.PendingLifetimeSeconds.ToString();
        _boxes["FeedbackSeconds"].Text = settings.FeedbackSeconds.ToString();
        _animation.IsChecked = settings.CandidateReadyAnimation;
        _transcription.IsChecked = settings.RealTimeTranscriptionEnabled;
        _subtitle.IsChecked = settings.SubtitleWindowEnabled;
        _clickThrough.IsChecked = settings.SubtitleClickThrough;
        _opacity.Value = settings.SubtitleBackgroundOpacity;
        _fontSize.Value = settings.SubtitleFontSize;
        _subtitleKey.Text = settings.SubtitleToggleKey;
        _lockKey.Text = settings.SubtitleLockKey;
        _audioPicker.Load(settings);
    }

    private sealed record LanguageChoice(AppLanguage Value, string Label);
    private sealed record ReminderChoice(int? Minutes, string Label);
}
