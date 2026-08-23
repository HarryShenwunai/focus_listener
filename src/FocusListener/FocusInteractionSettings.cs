using System.Text.Json;

namespace FocusListener;

public sealed record FocusInteractionSettings
{
    public int WarmupSeconds { get; init; } = 60;
    public int AutoCooldownSeconds { get; init; } = 120;
    public int CandidateLifetimeSeconds { get; init; } = 180;
    public AppLanguage AppLanguage { get; init; } = AppLanguage.System;
    public bool OnboardingCompleted { get; init; }
    public bool UsageNoticeAccepted { get; init; }
    public int? SessionReminderMinutes { get; init; }
    public int RetentionDays { get; init; } = 30;
    public int ManualSafetySeconds { get; init; } = 30;
    public int InitialAnswerSeconds { get; init; } = 8;
    public int ExtendedAnswerSeconds { get; init; } = 20;
    public int PendingLifetimeSeconds { get; init; } = 120;
    public int FeedbackSeconds { get; init; } = 3;
    public bool CandidateReadyAnimation { get; init; } = true;
    public AudioCaptureMode AudioMode { get; init; } = AudioCaptureMode.Automatic;
    public string? MicrophoneDeviceId { get; init; }
    public string? MicrophoneDeviceName { get; init; }
    public string? SystemPlaybackDeviceId { get; init; }
    public string? SystemPlaybackDeviceName { get; init; }
    public bool AudioSetupCompleted { get; init; }
    public bool RealTimeTranscriptionEnabled { get; init; } = true;
    public bool SubtitleWindowEnabled { get; init; } = true;
    public bool SubtitleClickThrough { get; init; } = true;
    public double SubtitleBackgroundOpacity { get; init; } = 0.58;
    public double SubtitleFontSize { get; init; } = 28;
    public double? SubtitleLeft { get; init; }
    public double? SubtitleTop { get; init; }
    public double SubtitleWidth { get; init; } = 820;
    public double SubtitleHeight { get; init; } = 150;
    public string SubtitleToggleKey { get; init; } = "S";
    public string SubtitleLockKey { get; init; } = "L";

    public static FocusInteractionSettings Default { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        InRange(WarmupSeconds, 0, 300, T("首次自动提问等待", "First automatic question delay"), errors);
        InRange(AutoCooldownSeconds, 30, 600, T("自动提问间隔", "Automatic question interval"), errors);
        InRange(CandidateLifetimeSeconds, 30, 900, T("知识点保留时间", "Knowledge point lifetime"), errors);
        InRange(ManualSafetySeconds, 0, 120, T("手动题后缓冲", "Post-manual buffer"), errors);
        InRange(InitialAnswerSeconds, 5, 30, T("初始答题时间", "Initial answer time"), errors);
        InRange(ExtendedAnswerSeconds, 6, 60, T("延长后总答题时间", "Extended total answer time"), errors);
        InRange(PendingLifetimeSeconds, 30, 600, T("待答题保留时间", "Pending question lifetime"), errors);
        InRange(FeedbackSeconds, 1, 10, T("反馈显示时间", "Feedback duration"), errors);

        if (!Enum.IsDefined(AppLanguage))
        {
            errors.Add(T("请选择有效的界面语言。", "Choose a valid interface language."));
        }

        if (!SessionReminderOptions.IsValid(SessionReminderMinutes))
        {
            errors.Add(T("课堂提醒只能选择关闭、15、30、45 或 60 分钟。", "Lesson reminder must be Off, 15, 30, 45, or 60 minutes."));
        }

        if (RetentionDays is < 1 or > 365)
        {
            errors.Add(T("本地分析数据保留时间应在 1–365 天之间。", "Local analytics retention must be between 1 and 365 days."));
        }

        if (!Enum.IsDefined(AudioMode))
        {
            errors.Add(T("请选择有效的音频工作模式。", "Choose a valid audio mode."));
        }

        if (SubtitleBackgroundOpacity is < 0.25 or > 0.9)
        {
            errors.Add(T("字幕背景透明度应在 25%–90% 之间。", "Subtitle background opacity must be between 25% and 90%."));
        }

        if (SubtitleFontSize is < 18 or > 54)
        {
            errors.Add(T("字幕字号应在 18–54 之间。", "Subtitle font size must be between 18 and 54."));
        }

        if (SubtitleWidth is < 360 or > 2400 || SubtitleHeight is < 90 or > 600)
        {
            errors.Add(T("字幕窗口大小超出可用范围。", "Subtitle window size is outside the supported range."));
        }

        ValidateShortcut(SubtitleToggleKey, T("显示/隐藏字幕快捷键", "Subtitle visibility shortcut"), errors);
        ValidateShortcut(SubtitleLockKey, T("锁定/解锁字幕快捷键", "Subtitle lock shortcut"), errors);
        var subtitleToggleKey = SubtitleToggleKey?.Trim();
        var subtitleLockKey = SubtitleLockKey?.Trim();
        if (string.Equals(subtitleToggleKey, subtitleLockKey, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(T("两个字幕快捷键不能使用同一个字母。", "Subtitle shortcuts must use different letters."));
        }
        if (string.Equals(subtitleToggleKey, "Q", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subtitleLockKey, "Q", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(T("字幕快捷键不能使用 Q，因为 Ctrl + Shift + Q 已用于立即提问。", "Subtitle shortcuts cannot use Q because Ctrl + Shift + Q asks a question."));
        }

        if (CandidateLifetimeSeconds < AutoCooldownSeconds + 30)
        {
            errors.Add(T("知识点保留时间至少要比自动提问间隔多 30 秒。", "Knowledge point lifetime must exceed the automatic interval by at least 30 seconds."));
        }

        if (ExtendedAnswerSeconds <= InitialAnswerSeconds)
        {
            errors.Add(T("延长后的总答题时间必须大于初始答题时间。", "Extended total answer time must exceed the initial answer time."));
        }

        return errors;
    }

    internal SessionTiming ToSessionTiming()
    {
        var errors = Validate();
        if (errors.Count != 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, errors));
        }

        return new SessionTiming(
            TimeSpan.FromSeconds(InitialAnswerSeconds),
            TimeSpan.FromSeconds(ExtendedAnswerSeconds),
            TimeSpan.FromSeconds(PendingLifetimeSeconds),
            TimeSpan.FromSeconds(CandidateLifetimeSeconds),
            TimeSpan.FromSeconds(FeedbackSeconds),
            TimeSpan.FromMilliseconds(100))
        {
            Warmup = TimeSpan.FromSeconds(WarmupSeconds),
            AutoCooldown = TimeSpan.FromSeconds(AutoCooldownSeconds),
            CandidateLifetime = TimeSpan.FromSeconds(CandidateLifetimeSeconds),
            ManualSafetyGap = TimeSpan.FromSeconds(ManualSafetySeconds),
            CandidateCapacity = 3
        };
    }

    private static string T(string zh, string en) => ProductText.Choose(zh, en);

    private static void InRange(int value, int minimum, int maximum, string name, ICollection<string> errors)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add(T($"{name}应在 {minimum}–{maximum} 秒之间。", $"{name} must be between {minimum} and {maximum} seconds."));
        }
    }

    private static void ValidateShortcut(string value, string name, ICollection<string> errors)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != 1 ||
            (normalized[0] is < 'A' or > 'Z') && (normalized[0] is < 'a' or > 'z'))
        {
            errors.Add(T($"{name}应填写一个英文字母。", $"{name} must be one English letter."));
        }
    }
}

public sealed class FocusInteractionSettingsStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public string SettingsPath { get; } = Path.GetFullPath(settingsPath);

    public FocusInteractionSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return FocusInteractionSettings.Default;
            }

            var settings = JsonSerializer.Deserialize<FocusInteractionSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            return settings is not null && settings.Validate().Count == 0
                ? settings
                : FocusInteractionSettings.Default;
        }
        catch (IOException)
        {
            return FocusInteractionSettings.Default;
        }
        catch (JsonException)
        {
            return FocusInteractionSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return FocusInteractionSettings.Default;
        }
    }

    public async Task SaveAsync(FocusInteractionSettings settings, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count != 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(settings));
        }

        await _saveGate.WaitAsync(cancellation);
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = SettingsPath + ".new";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions),
                cancellation);
            File.Move(temporaryPath, SettingsPath, true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

public static class FocusDataMaintenance
{
    public static Task ClearLocalDataAsync(
        string databasePath,
        string diagnosticsDirectory,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var applicationRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusListener"));
        var database = RequireInside(applicationRoot, databasePath);
        var diagnostics = RequireInside(applicationRoot, diagnosticsDirectory);
        var logs = RequireInside(applicationRoot, Path.Combine(applicationRoot, "logs"));
        var crash = RequireInside(applicationRoot, Path.Combine(applicationRoot, "last-crash.json"));

        foreach (var path in new[] { database, database + "-wal", database + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var databaseDirectory = Path.GetDirectoryName(database);
        if (databaseDirectory is not null && Directory.Exists(databaseDirectory))
        {
            foreach (var backup in Directory.GetFiles(
                         databaseDirectory,
                         Path.GetFileName(database) + ".backup-*.db"))
            {
                File.Delete(backup);
            }
        }

        if (Directory.Exists(diagnostics))
        {
            Directory.Delete(diagnostics, true);
        }
        if (Directory.Exists(logs))
        {
            Directory.Delete(logs, true);
        }
        if (File.Exists(crash))
        {
            File.Delete(crash);
        }

        return Task.CompletedTask;
    }

    private static string RequireInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(ProductText.Choose("只能清除 Focus Listener 自己的本地数据。", "Only Focus Listener local data can be cleared."));
        }

        return fullPath;
    }
}
