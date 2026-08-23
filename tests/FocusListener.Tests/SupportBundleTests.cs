using System.IO.Compression;
using FocusListener.App;

namespace FocusListener.Tests;

public sealed class SupportBundleTests
{
    [Fact]
    public async Task Support_bundle_excludes_device_identity_and_classroom_content()
    {
        var directory = Path.Combine(Path.GetTempPath(), "focus-listener-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var zip = Path.Combine(directory, "support.zip");
        try
        {
            var settings = FocusInteractionSettings.Default with
            {
                MicrophoneDeviceId = "SECRET-MIC-ID",
                MicrophoneDeviceName = "Secret microphone name",
                SystemPlaybackDeviceId = "SECRET-OUTPUT-ID",
                SystemPlaybackDeviceName = "Secret output name"
            };

            await ProductRuntime.CreateSupportBundleAsync(zip, settings);

            using var archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "settings-sanitized.json");
            var combined = string.Join('\n', archive.Entries.Select(ReadEntry));
            Assert.DoesNotContain("SECRET-MIC-ID", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret microphone name", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-OUTPUT-ID", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret output name", combined, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
