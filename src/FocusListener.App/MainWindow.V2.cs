using System.Windows;
using System.Windows.Media.Animation;

namespace FocusListener.App;

public partial class MainWindow
{
    private FocusInteractionSettingsStore? _settingsStoreV2;
    private FocusInteractionSettings _activeSettingsV2 = FocusInteractionSettings.Default;
    private bool _candidateAnimatingV2;

    private string SettingsPathV2 => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FocusListener",
        "settings.json");

    private string DiagnosticsDirectoryV2 => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FocusListener",
        "diagnostics");

    private void Window_LoadedV2(object sender, RoutedEventArgs e)
    {
        Window_Loaded(sender, e);
        _settingsStoreV2 = new FocusInteractionSettingsStore(SettingsPathV2);
        _activeSettingsV2 = _settingsStoreV2.Load();
        UpdateModeLabelV2();
        InitializeAudioExperienceV3();
    }

    private async void StartSessionV2_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionTask is not null)
        {
            return;
        }

        _settingsStoreV2 ??= new FocusInteractionSettingsStore(SettingsPathV2);
        _activeSettingsV2 = _settingsStoreV2.Load();
        var live = !string.IsNullOrWhiteSpace(_apiKey);
        if (!EnsureAudioSetupV3(live))
        {
            return;
        }

        var experience = PrepareAudioExperienceV3(live);
        StartPanel.Visibility = Visibility.Collapsed;
        SessionFooter.Visibility = Visibility.Visible;
        StatusText.Text = _registeredHotKey ? "快捷键：Ctrl + Shift + Q" : "全局快捷键被占用，可点击手动触发";
        _session = live
            ? UniversalFocusSessionFactory.CreateProduction(
                new GeminiFocusOptions(_apiKey!),
                _databasePath,
                _activeSettingsV2,
                experience!)
            : UniversalFocusSessionFactory.CreateSimulation(_databasePath, _activeSettingsV2);
        ModeLabel.Text = live
            ? "真实课堂 · 全学科知识点"
            : "模拟课堂 · 通用知识点演示";
        ExtendButton.Content = $"再想 {_activeSettingsV2.ExtendedAnswerSeconds - _activeSettingsV2.InitialAnswerSeconds} 秒";
        FeedbackDurationText.Text = $"{_activeSettingsV2.FeedbackSeconds} 秒后继续监听";
        var progress = new Progress<SessionView>(RenderV2);
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
        finally
        {
            FinishAudioExperienceV3();
        }
    }

    private void RenderV2(SessionView view)
    {
        Render(view);
        RenderAudioExperienceV3(view);
        CandidateReadyButton.Visibility = view.Surface == SessionSurfaceKind.Listening && view.CandidateReady
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetCandidateAnimation(view.CandidateReady && view.Surface == SessionSurfaceKind.Listening);
        if (view.Question is { } question)
        {
            KnowledgeLabel.Text = $"{question.Subject} · {QuestionTypeDisplay.Chinese(question.Type)}";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsV3();
    }

    private async void CandidateReady_Click(object sender, RoutedEventArgs e) =>
        await TriggerManuallyAsync();

    private void SetCandidateAnimation(bool ready)
    {
        var animate = ready && _activeSettingsV2.CandidateReadyAnimation && SystemParameters.ClientAreaAnimation;
        if (animate == _candidateAnimatingV2)
        {
            return;
        }

        _candidateAnimatingV2 = animate;
        if (!animate)
        {
            CandidateReadyButton.BeginAnimation(OpacityProperty, null);
            CandidateReadyButton.Opacity = 1;
            return;
        }

        CandidateReadyButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.58, 1, TimeSpan.FromSeconds(1.15))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void UpdateModeLabelV2()
    {
        var live = !string.IsNullOrWhiteSpace(_apiKey);
        ModeLabel.Text = live
            ? "真实课堂已就绪 · 全学科知识点"
            : "模拟课堂 · 点击标题配置 Gemini 免费层";
        StartSessionButtonV2.Content = live ? "开始 12 分钟真实课堂" : "开始 12 分钟模拟课堂";
    }
}
