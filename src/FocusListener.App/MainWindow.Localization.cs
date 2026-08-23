namespace FocusListener.App;

public partial class MainWindow
{
    private void ApplyLanguageV3()
    {
        var english = ProductText.Language == AppLanguage.English;
        StartTitleText.Text = english ? "Gently bring your attention back." : "在听课途中，轻轻拉回注意力。";
        StartDescriptionText.Text = english
            ? "Maths, science, history, language, and other complete knowledge points can trigger a brief three-choice restatement question. No calculation is required. Ctrl + Shift + Q asks manually."
            : "数学、科学、历史、语言和其他知识点都能触发。只问三选一复述题，不要求计算；Ctrl + Shift + Q 可手动触发。";
        SettingsButton.Content = english ? "Lesson settings" : "课堂提问设置";
        ListeningTitleText.Text = english ? "Listening" : "正在听课";
        AudioSettingsButton.Content = english ? "Change devices" : "更换设备";
        CandidateReadyText.Text = english ? "Question ready" : "题目已准备";
        ManualTriggerButton.Content = english ? "Ask me now" : "现在问我一道";
        ReportIssueButton.Content = english ? "Report question" : "题目有误";
        PendingHintText.Text = english ? "Choose the badge to continue" : "点击徽标继续作答";
        EvidenceLabelText.Text = english ? "Lesson evidence" : "课堂原话";
        RatingTitleText.Text = english
            ? "How much did these questions help bring your attention back?"
            : "这些问题有多大程度帮你拉回注意力？";
        RatingLowText.Text = english ? "Not at all" : "完全没帮助";
        RatingHighText.Text = english ? "Very much" : "非常有帮助";
        SkipRatingButton.Content = english ? "Skip" : "跳过";
        CompletedTitleText.Text = english ? "Lesson completed" : "本次课堂已完成";
        ExportCsvButton.Content = english ? "Export analysis CSV" : "导出分析 CSV";
        EndButton.Content = english ? "End session" : "结束课堂";
        RetryTranscriptionButton.Content = english ? "Retry transcription" : "重试转写";
        MinimizeButton.ToolTip = english ? "Minimize" : "最小化";
        CloseButton.ToolTip = english ? "Close" : "关闭";
        ApplyLanguageToExperienceButtonsV3();
        UpdateModeLabelV2();
    }

    private void ApplyLanguageToExperienceButtonsV3()
    {
        TranscriptionToggleButton.Content = _activeSettingsV2.RealTimeTranscriptionEnabled
            ? ProductText.Choose("转写：开", "Transcription: On")
            : ProductText.Choose("转写：关", "Transcription: Off");
        SubtitleToggleButton.Content = _activeSettingsV2.SubtitleWindowEnabled
            ? ProductText.Choose("字幕：显示", "Subtitles: Shown")
            : ProductText.Choose("字幕：隐藏", "Subtitles: Hidden");
        var locked = _subtitleWindowV3?.IsLocked ?? _activeSettingsV2.SubtitleClickThrough;
        SubtitleLockButton.Content = locked
            ? ProductText.Choose("字幕：已锁定", "Subtitles: Locked")
            : ProductText.Choose("字幕：可移动", "Subtitles: Movable");
    }
}
