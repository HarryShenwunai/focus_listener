using System.Text.Json;

namespace FocusListener;

public sealed record FocusInteractionSettings
{
    public int WarmupSeconds { get; init; } = 60;
    public int AutoCooldownSeconds { get; init; } = 120;
    public int CandidateLifetimeSeconds { get; init; } = 180;
    public int ManualSafetySeconds { get; init; } = 30;
    public int InitialAnswerSeconds { get; init; } = 8;
    public int ExtendedAnswerSeconds { get; init; } = 20;
    public int PendingLifetimeSeconds { get; init; } = 120;
    public int FeedbackSeconds { get; init; } = 3;
    public bool CandidateReadyAnimation { get; init; } = true;

    public static FocusInteractionSettings Default { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        InRange(WarmupSeconds, 0, 300, "首次自动提问等待", errors);
        InRange(AutoCooldownSeconds, 30, 600, "自动提问间隔", errors);
        InRange(CandidateLifetimeSeconds, 30, 900, "知识点保留时间", errors);
        InRange(ManualSafetySeconds, 0, 120, "手动题后缓冲", errors);
        InRange(InitialAnswerSeconds, 5, 30, "初始答题时间", errors);
        InRange(ExtendedAnswerSeconds, 6, 60, "延长后总答题时间", errors);
        InRange(PendingLifetimeSeconds, 30, 600, "待答题保留时间", errors);
        InRange(FeedbackSeconds, 1, 10, "反馈显示时间", errors);

        if (CandidateLifetimeSeconds < AutoCooldownSeconds + 30)
        {
            errors.Add("知识点保留时间至少要比自动提问间隔多 30 秒。");
        }

        if (ExtendedAnswerSeconds <= InitialAnswerSeconds)
        {
            errors.Add("延长后的总答题时间必须大于初始答题时间。");
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

    private static void InRange(int value, int minimum, int maximum, string name, ICollection<string> errors)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"{name}应在 {minimum}–{maximum} 秒之间。");
        }
    }
}

public sealed class FocusInteractionSettingsStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

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

        foreach (var path in new[] { database, database + "-wal", database + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(diagnostics))
        {
            Directory.Delete(diagnostics, true);
        }

        return Task.CompletedTask;
    }

    private static string RequireInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能清除 Focus Listener 自己的本地数据。");
        }

        return fullPath;
    }
}
