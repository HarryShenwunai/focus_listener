using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FocusListener.App;

public partial class MainWindow : Window
{
    private const int ManualTriggerHotKeyId = 0x464C;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint VirtualKeyQ = 0x51;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _countdownTimer;
    private readonly string _databasePath;
    private IFocusSession? _session;
    private Task<SessionSummary>? _sessionTask;
    private SessionView? _view;
    private SessionSummary? _summary;
    private HwndSource? _windowSource;
    private bool _registeredHotKey;
    private string? _apiKey;

    public MainWindow()
    {
        InitializeComponent();
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusListener",
            "focus-listener.db");
        _countdownTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => UpdateCountdown(), Dispatcher);
        _countdownTimer.Start();
        _apiKey = ReadConfiguredApiKey();
        ModeLabel.Cursor = Cursors.Hand;
        ModeLabel.MouseLeftButtonDown += ConfigureGemini_Click;
        UpdateModeLabel();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProcedure);
        _registeredHotKey = RegisterHotKey(handle, ManualTriggerHotKeyId,
            ModifierControl | ModifierShift, VirtualKeyQ);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - ActualWidth - 22);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - ActualHeight - 22);
        UpdateModeLabel();
    }

    private void ConfigureGemini_Click(object sender, MouseButtonEventArgs e)
    {
        if (_sessionTask is not null)
        {
            StatusText.Text = "请在下一次课堂开始前更改 Gemini 配置。";
            return;
        }

        var dialog = new ApiKeyDialog(_apiKey) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (dialog.ClearRequested)
            {
                WindowsCredentialStore.DeleteApiKey();
                _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            }
            else if (dialog.ApiKey is { } key)
            {
                WindowsCredentialStore.WriteApiKey(key);
                _apiKey = key;
            }

            UpdateModeLabel();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Gemini 配置未保存：{exception.Message}", "Focus Listener",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StartSession_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionTask is not null)
        {
            return;
        }

        StartPanel.Visibility = Visibility.Collapsed;
        SessionFooter.Visibility = Visibility.Visible;
        StatusText.Text = _registeredHotKey ? "快捷键：Ctrl + Shift + Q" : "全局快捷键被占用，可点击手动触发";
        var live = !string.IsNullOrWhiteSpace(_apiKey);
        _session = live
            ? ProductionFocusSessionFactory.Create(new GeminiFocusOptions(_apiKey!), _databasePath)
            : FocusSessionFactory.CreateSimulation(_databasePath);
        ModeLabel.Text = live
            ? "真实课堂 · Gemini Live + Flash-Lite"
            : "模拟课堂 · 小学行程问题";
        var progress = new Progress<SessionView>(Render);
        _sessionTask = _session.RunAsync(
            new SessionStart(live ? ClassroomKind.InPerson : ClassroomKind.ComputerPlayback, TimeSpan.FromMinutes(12)),
            progress,
            _lifetime.Token);

        try
        {
            _summary = await _sessionTask;
            RenderSummary(_summary);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowOnly(CompletedPanel);
            SummaryText.Text = $"会话意外停止：{exception.Message}";
            StatusText.Text = "错误";
        }
    }

    private void Render(SessionView view)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Render(view));
            return;
        }

        _view = view;
        StatusText.Text = view.Notice ?? (view.Health == SessionHealth.Healthy ? "运行正常" : "自动题源暂时不可用");
        EndButton.Visibility = view.Surface is SessionSurfaceKind.AttentionRating or SessionSurfaceKind.Completed
            ? Visibility.Collapsed
            : Visibility.Visible;

        switch (view.Surface)
        {
            case SessionSurfaceKind.Listening:
                ListeningNotice.Text = view.Notice ?? "等待出现适合复述的知识单元…";
                ShowOnly(ListeningPanel);
                break;
            case SessionSurfaceKind.Question:
                RenderQuestion(view);
                ShowOnly(QuestionPanel);
                break;
            case SessionSurfaceKind.PendingBadge:
                PendingText.Text = RemainingPendingText(view.PendingExpiresAt);
                ShowOnly(PendingPanel);
                break;
            case SessionSurfaceKind.Feedback:
                RenderFeedback(view);
                ShowOnly(FeedbackPanel);
                break;
            case SessionSurfaceKind.AttentionRating:
                ShowOnly(RatingPanel);
                break;
            case SessionSurfaceKind.Completed:
                ShowOnly(CompletedPanel);
                break;
            case SessionSurfaceKind.Failed:
                ShowOnly(CompletedPanel);
                SummaryText.Text = view.Notice ?? "会话失败。";
                break;
        }
    }

    private void RenderQuestion(SessionView view)
    {
        if (view.Question is null)
        {
            return;
        }

        TriggerLabel.Text = view.Question.Trigger == TriggerKind.Automatic ? "自动复位题" : "手动复位题";
        QuestionStem.Text = view.Question.Stem;
        var buttons = new[] { ChoiceA, ChoiceB, ChoiceC };
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            if (index < view.Question.Choices.Count)
            {
                var choice = view.Question.Choices[index];
                button.Tag = choice.Id.Value;
                button.Content = $"{choice.Id.Value}  {choice.Text}";
                button.Visibility = Visibility.Visible;
                button.IsEnabled = true;
            }
            else
            {
                button.Visibility = Visibility.Collapsed;
            }
        }

        ExtendButton.Visibility = view.CanExtend ? Visibility.Visible : Visibility.Collapsed;
        CollapseButton.Visibility = view.PendingExpiresAt.HasValue ? Visibility.Visible : Visibility.Collapsed;
        UpdateCountdown();
    }

    private void RenderFeedback(SessionView view)
    {
        if (view.Feedback is null)
        {
            return;
        }

        FeedbackTitle.Text = view.Feedback.IsCorrect ? "答对了，注意力已复位" : "再听一下这个关键句";
        FeedbackTitle.Foreground = view.Feedback.IsCorrect
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(164, 91, 44));
        EvidenceText.Text = view.Feedback.Evidence.Excerpt;
    }

    private void RenderSummary(SessionSummary summary)
    {
        var accuracy = summary.Answers == 0
            ? "—"
            : $"{(double)summary.CorrectAnswers / summary.Answers:P0}";
        SummaryText.Text =
            $"弹出 {summary.QuestionsShown} 题 · 回答 {summary.Answers} 题 · 正确率 {accuracy}\n" +
            $"排队 {summary.QuestionsQueued} 题 · 容量跳过 {summary.CapacityDrops} 题 · 题目有误 {summary.InvalidQuestions} 题";
        StatusText.Text = summary.AttentionRating is null
            ? "未填写注意力评分"
            : $"注意力复位评分：{summary.AttentionRating}/5";
    }

    private async void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string choice || _view?.Question is null)
        {
            return;
        }

        await ApplyAsync(new SelectAnswer(IntentId.New(), _view.Question.Id, new ChoiceId(choice)));
    }

    private async void Extend_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.Question is { } question)
        {
            await ApplyAsync(new ExtendThinking(IntentId.New(), question.Id));
        }
    }

    private async void OpenPending_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.Question is { } question)
        {
            await ApplyAsync(new OpenPending(IntentId.New(), question.Id));
        }
    }

    private async void CollapsePending_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.Question is { } question)
        {
            await ApplyAsync(new CollapsePending(IntentId.New(), question.Id));
        }
    }

    private async void ReportIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.Question is { } question)
        {
            await ApplyAsync(new ReportQuestionIssue(IntentId.New(), question.Id));
        }
    }

    private async void ManualTrigger_Click(object sender, RoutedEventArgs e) =>
        await TriggerManuallyAsync();

    private async Task TriggerManuallyAsync()
    {
        if (_session is null)
        {
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
        await ApplyAsync(new RequestManualTrigger(IntentId.New()));
    }

    private async void EndSession_Click(object sender, RoutedEventArgs e) =>
        await ApplyAsync(new EndSession(IntentId.New()));

    private async void Rating_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string raw } && byte.TryParse(raw, out var rating))
        {
            await ApplyAsync(new RateAttentionReset(IntentId.New(), rating));
        }
    }

    private async void SkipRating_Click(object sender, RoutedEventArgs e) =>
        await ApplyAsync(new SkipAttentionRating(IntentId.New()));

    private async Task ApplyAsync(LearnerIntent intent)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var outcome = await _session.ApplyAsync(intent, _lifetime.Token);
            if (!outcome.Accepted || !string.IsNullOrWhiteSpace(outcome.Message))
            {
                StatusText.Text = outcome.Message;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 Focus Listener 分析记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"focus-listener-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await FocusSessionFactory.ExportCsvAsync(_databasePath, dialog.FileName, _lifetime.Token);
            StatusText.Text = $"已导出：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"导出失败：{exception.Message}";
        }
    }

    private void UpdateCountdown()
    {
        if (_view?.Deadline is { } deadline)
        {
            var remaining = Math.Max(0, Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));
            CountdownText.Text = $"{remaining:0} 秒";
        }
        else if (_view?.PendingExpiresAt is { } pending)
        {
            CountdownText.Text = RemainingPendingText(pending);
            PendingText.Text = RemainingPendingText(pending);
        }
        else
        {
            CountdownText.Text = string.Empty;
        }
    }

    private static string RemainingPendingText(DateTimeOffset? expiry)
    {
        if (expiry is null)
        {
            return "有 1 道待答题";
        }

        var seconds = Math.Max(0, (int)Math.Ceiling((expiry.Value - DateTimeOffset.UtcNow).TotalSeconds));
        return $"待答题 · {seconds / 60}:{seconds % 60:00}";
    }

    private void ShowOnly(UIElement panel)
    {
        foreach (var candidate in new UIElement[]
                 { StartPanel, ListeningPanel, QuestionPanel, PendingPanel, FeedbackPanel, RatingPanel, CompletedPanel })
        {
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateModeLabel()
    {
        var live = !string.IsNullOrWhiteSpace(_apiKey);
        ModeLabel.Text = live
            ? "真实课堂已就绪 · 点击更换 Gemini Key"
            : "模拟课堂 · 点击配置 Gemini 免费层";
        if (StartPanel.Children.OfType<Button>().FirstOrDefault() is { } startButton)
        {
            startButton.Content = live ? "开始 12 分钟真实课堂" : "开始 12 分钟模拟课堂";
        }
    }

    private static string? ReadConfiguredApiKey()
    {
        try
        {
            return WindowsCredentialStore.ReadApiKey() ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        }
        catch
        {
            return Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int hotKeyMessage = 0x0312;
        if (message == hotKeyMessage && wParam.ToInt32() == ManualTriggerHotKeyId)
        {
            _ = TriggerManuallyAsync();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _countdownTimer.Stop();
        _lifetime.Cancel();
        if (_registeredHotKey)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, ManualTriggerHotKeyId);
        }
        _windowSource?.RemoveHook(WindowProcedure);
        _lifetime.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
