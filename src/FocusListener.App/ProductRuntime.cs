using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FocusListener.App;

internal static class ProductRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly object LogGate = new();

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FocusListener");
    public static string DatabasePath { get; } = Path.Combine(RootDirectory, "focus-listener.db");
    public static string SettingsPath { get; } = Path.Combine(RootDirectory, "settings.json");
    public static string DiagnosticsDirectory { get; } = Path.Combine(RootDirectory, "diagnostics");
    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");
    public static string CrashPath { get; } = Path.Combine(RootDirectory, "last-crash.json");
    public static string Version { get; } = ResolveVersion();

    public static bool HasPendingCrash => File.Exists(CrashPath);

    public static void Initialize()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        PurgeDirectory(LogsDirectory, TimeSpan.FromDays(7));
        PurgeDirectory(DiagnosticsDirectory, TimeSpan.FromDays(7));
        Log("ApplicationStarted");
    }

    public static void Log(string code, Exception? exception = null)
    {
        var entry = new
        {
            At = DateTimeOffset.UtcNow,
            Code = code,
            Version,
            ExceptionType = exception?.GetType().FullName,
            exception?.HResult,
            Stack = SanitizeStack(exception?.StackTrace)
        };
        var path = Path.Combine(LogsDirectory, $"focus-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        lock (LogGate)
        {
            Directory.CreateDirectory(LogsDirectory);
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }

    public static void RecordCrash(string code, Exception exception)
    {
        Log(code, exception);
        Directory.CreateDirectory(RootDirectory);
        File.WriteAllText(CrashPath, JsonSerializer.Serialize(new
        {
            At = DateTimeOffset.UtcNow,
            Code = code,
            Version,
            ExceptionType = exception.GetType().FullName,
            exception.HResult,
            Stack = SanitizeStack(exception.StackTrace)
        }, JsonOptions));
    }

    public static void AcknowledgeCrash()
    {
        if (File.Exists(CrashPath))
        {
            File.Delete(CrashPath);
        }
    }

    public static async Task CreateSupportBundleAsync(
        string destinationPath,
        FocusInteractionSettings settings,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var staging = Path.Combine(Path.GetTempPath(), "focus-listener-support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
            {
                Product = "Focus Listener",
                Version,
                CreatedAt = DateTimeOffset.UtcNow,
                OperatingSystem = Environment.OSVersion.VersionString,
                Framework = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                Privacy = "No API key, device identity, audio, full transcript, question, evidence, or database is included."
            }, JsonOptions), cancellation);
            await File.WriteAllTextAsync(Path.Combine(staging, "settings-sanitized.json"), JsonSerializer.Serialize(new
            {
                settings.AppLanguage,
                settings.AudioMode,
                settings.RealTimeTranscriptionEnabled,
                settings.SubtitleWindowEnabled,
                settings.SessionReminderMinutes,
                settings.RetentionDays,
                settings.WarmupSeconds,
                settings.AutoCooldownSeconds
            }, JsonOptions), cancellation);

            CopyIfPresent(CrashPath, Path.Combine(staging, "last-crash.json"));
            if (Directory.Exists(LogsDirectory))
            {
                foreach (var log in Directory.GetFiles(LogsDirectory, "*.jsonl")
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Take(2))
                {
                    File.Copy(log, Path.Combine(staging, Path.GetFileName(log)), true);
                }
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            ZipFile.CreateFromDirectory(staging, destination, CompressionLevel.SmallestSize, false);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public static bool TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception)
        {
            Log("OpenExternalUrlFailed", exception);
            return false;
        }
    }
    private static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0").Split('+')[0];
    }

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, true);
        }
    }

    private static void PurgeDirectory(string directory, TimeSpan age)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - age;
        foreach (var file in Directory.GetFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? SanitizeStack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack))
        {
            return null;
        }

        return stack.Length <= 12_000 ? stack : stack[..12_000];
    }
}

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\FocusListener.PortableBeta";
    private readonly Mutex _mutex = new(false, MutexName);
    private bool _ownsMutex;

    public bool TryAcquire()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        return _ownsMutex;
    }

    public static void ActivateExistingWindow()
    {
        var handle = FindWindow(null, "Focus Listener");
        if (handle == IntPtr.Zero)
        {
            return;
        }
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
}

internal sealed record ReleaseCheckResult(bool IsNewer, string Tag, string PageUrl);

internal static class GitHubReleaseChecker
{
    public static async Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellation = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FocusListener/" + ProductRuntime.Version);
        using var response = await client.GetAsync(
            "https://api.github.com/repos/HarryShenwunai/focus_listener/releases?per_page=10",
            cancellation);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        var root = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault(release =>
                !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.True)
            : default;
        var tag = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tag_name", out var tagValue)
            ? tagValue.GetString() ?? string.Empty
            : string.Empty;
        var page = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("html_url", out var pageValue)
            ? pageValue.GetString() ?? "https://github.com/HarryShenwunai/focus_listener/releases"
            : "https://github.com/HarryShenwunai/focus_listener/releases";
        var current = "v" + ProductRuntime.Version;
        return new ReleaseCheckResult(tag.Length > 0 && !string.Equals(tag, current, StringComparison.OrdinalIgnoreCase), tag, page);
    }
}
