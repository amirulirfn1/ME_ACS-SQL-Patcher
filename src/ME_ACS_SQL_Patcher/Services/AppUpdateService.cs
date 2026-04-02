using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using MagDbPatcher.Infrastructure;

namespace MagDbPatcher.Services;

public interface IAppUpdateService
{
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(string? feedPath, CancellationToken cancellationToken = default);
    Task ApplyPendingUpdateAndRestartAsync(string installerUrl, string? installerSha256 = null, CancellationToken cancellationToken = default);
}

public enum AppUpdateStatus
{
    NotConfigured,
    NoUpdateAvailable,
    UpdateAvailable
}

public sealed record AppUpdateCheckResult(
    AppUpdateStatus Status,
    string Message,
    string? InstallerUrl = null,
    string? InstallerSha256 = null,
    string? ReleaseNotes = null,
    string? ServerPatchCatalogLabel = null)
{
    public bool CanApplyNow => InstallerUrl != null;
    public bool IsUpdateAvailable => Status == AppUpdateStatus.UpdateAvailable;
}

public sealed class BuildDateUpdateService : IAppUpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(string? feedPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedPath))
            return new AppUpdateCheckResult(AppUpdateStatus.NotConfigured,
                "Set an app update feed path or URL first.");

        string json;
        string installerLocation;
        string? installerSha256 = null;
        string? serverPatchCatalogHash = null;
        string? serverPatchCatalogLabel = null;
        string? releaseNotes = null;
        try
        {
            if (AppUpdateFeedResolver.TryGetLocalDirectory(feedPath, out var localDirectory))
            {
                var latestPath = Path.Combine(localDirectory, "latest.json");
                if (!File.Exists(latestPath))
                    throw new InvalidOperationException($"Could not find latest.json in {localDirectory}.");

                json = await File.ReadAllTextAsync(latestPath, cancellationToken);
                installerLocation = localDirectory;
            }
            else
            {
                var latestUrl = AppUpdateFeedResolver.BuildLatestJsonUrl(feedPath);
                json = await Http.GetStringAsync(latestUrl, cancellationToken);
                installerLocation = feedPath.TrimEnd('/');
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not load update feed '{feedPath}': {ex.Message}", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("installerSha256", out var installerSha256El))
            installerSha256 = installerSha256El.GetString();
        if (root.TryGetProperty("patchCatalogHash", out var patchCatalogHashEl))
            serverPatchCatalogHash = patchCatalogHashEl.GetString();
        if (root.TryGetProperty("patchCatalogLabel", out var patchCatalogLabelEl))
            serverPatchCatalogLabel = patchCatalogLabelEl.GetString();
        if (root.TryGetProperty("releaseNotes", out var releaseNotesEl))
            releaseNotes = NormalizeReleaseNotes(releaseNotesEl.GetString());

        if (!root.TryGetProperty("buildDate", out var buildDateEl) ||
            !DateTime.TryParse(buildDateEl.GetString(), null, DateTimeStyles.RoundtripKind, out var serverDate))
        {
            throw new InvalidOperationException("latest.json is missing or has an invalid 'buildDate' field.");
        }

        var appDate = AppMetadata.BuildDate;
        var buildIsNewer = serverDate > appDate;
        var patchCatalogIsNewer =
            !string.IsNullOrWhiteSpace(serverPatchCatalogHash) &&
            !string.IsNullOrWhiteSpace(AppMetadata.InstalledPatchCatalogHash) &&
            !string.Equals(serverPatchCatalogHash.Trim(), AppMetadata.InstalledPatchCatalogHash, StringComparison.OrdinalIgnoreCase);

        if (!buildIsNewer && !patchCatalogIsNewer)
        {
            return new AppUpdateCheckResult(AppUpdateStatus.NoUpdateAvailable,
                BuildUpToDateMessage(appDate, AppMetadata.InstalledPatchCatalogLabel));
        }

        var installerName = root.TryGetProperty("installerName", out var nameEl)
            ? nameEl.GetString() ?? "ME_ACS_SQL_Patcher-win-Setup.exe"
            : "ME_ACS_SQL_Patcher-win-Setup.exe";

        string installerPath;
        if (AppUpdateFeedResolver.TryGetLocalDirectory(feedPath, out var installerDirectory))
        {
            installerPath = AppUpdateFeedResolver.BuildLocalInstallerPath(installerDirectory, installerName);
            if (!File.Exists(installerPath))
                throw new InvalidOperationException($"Update installer was not found: {installerPath}");

            VerifyInstallerHashIfPresent(installerPath, installerSha256);
        }
        else
        {
            installerPath = $"{installerLocation}/{installerName}";
        }

        return new AppUpdateCheckResult(
            AppUpdateStatus.UpdateAvailable,
            BuildUpdatePrompt(
                buildIsNewer,
                patchCatalogIsNewer,
                appDate,
                serverDate,
                AppMetadata.InstalledPatchCatalogLabel,
                serverPatchCatalogLabel,
                releaseNotes),
            installerPath,
            installerSha256,
            releaseNotes,
            serverPatchCatalogLabel);
    }

    public async Task ApplyPendingUpdateAndRestartAsync(string installerUrl, string? installerSha256 = null, CancellationToken cancellationToken = default)
    {
        if (AppUpdateFeedResolver.TryGetLocalFile(installerUrl, out var localInstaller))
        {
            VerifyInstallerHashIfPresent(localInstaller, installerSha256);
            LaunchInstaller(localInstaller);
            System.Windows.Application.Current.Shutdown();
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "ME_ACS_SQL_Patcher-update");
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, $"Setup-{Guid.NewGuid():N}.exe");

        using var response = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var fs = File.Create(localPath))
        {
            await response.Content.CopyToAsync(fs, cancellationToken);
        }

        VerifyInstallerHashIfPresent(localPath, installerSha256);
        LaunchInstaller(localPath);

        System.Windows.Application.Current.Shutdown();
    }

    private static void LaunchInstaller(string installerPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });
    }

    private static void VerifyInstallerHashIfPresent(string installerPath, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return;

        using var stream = File.OpenRead(installerPath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Downloaded installer failed integrity verification. Expected SHA-256 {expectedSha256}, got {hash}.");
        }
    }

    private static string BuildUpToDateMessage(DateTime appDate, string? installedPatchCatalogLabel)
    {
        var builder = new StringBuilder();
        builder.Append($"App is up to date (built {appDate.AddHours(8):yyyy-MM-dd HH:mm} MYT).");
        if (!string.IsNullOrWhiteSpace(installedPatchCatalogLabel))
            builder.Append($" Bundled patch catalog: {installedPatchCatalogLabel}.");

        return builder.ToString();
    }

    private static string BuildUpdatePrompt(
        bool buildIsNewer,
        bool patchCatalogIsNewer,
        DateTime currentBuildDate,
        DateTime serverBuildDate,
        string? installedPatchCatalogLabel,
        string? serverPatchCatalogLabel,
        string? releaseNotes)
    {
        var lines = new List<string>
        {
            buildIsNewer && patchCatalogIsNewer
                ? "A newer app build and bundled patch catalog are available."
                : buildIsNewer
                    ? "A newer app build is available."
                    : "A newer bundled patch catalog is available.",
            string.Empty,
            $"Current build: {currentBuildDate.AddHours(8):dd MMM yyyy HH:mm} MYT",
            $"Incoming build: {serverBuildDate.AddHours(8):dd MMM yyyy HH:mm} MYT"
        };

        if (!string.IsNullOrWhiteSpace(installedPatchCatalogLabel))
            lines.Add($"Current bundled patch catalog: {installedPatchCatalogLabel}");
        if (!string.IsNullOrWhiteSpace(serverPatchCatalogLabel))
            lines.Add($"Incoming bundled patch catalog: {serverPatchCatalogLabel}");
        if (!string.IsNullOrWhiteSpace(releaseNotes))
        {
            lines.Add(string.Empty);
            lines.Add("Notes:");
            lines.Add(releaseNotes);
        }

        lines.Add(string.Empty);
        lines.Add("Download and install now?");
        return string.Join(Environment.NewLine, lines);
    }

    private static string? NormalizeReleaseNotes(string? releaseNotes)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes))
            return null;

        return releaseNotes
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}

internal static class AppUpdateFeedResolver
{
    public static bool TryGetLocalDirectory(string? feedPath, out string directoryPath)
    {
        directoryPath = string.Empty;
        if (string.IsNullOrWhiteSpace(feedPath))
            return false;

        var trimmed = feedPath.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                directoryPath = Path.GetFullPath(uri.LocalPath);
                return true;
            }

            return false;
        }

        if (!Path.IsPathRooted(trimmed))
            return false;

        directoryPath = Path.GetFullPath(trimmed);
        return true;
    }

    public static bool TryGetLocalFile(string? pathOrUrl, out string filePath)
    {
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            return false;

        var trimmed = pathOrUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
                return false;

            filePath = Path.GetFullPath(uri.LocalPath);
            return true;
        }

        if (!Path.IsPathRooted(trimmed))
            return false;

        filePath = Path.GetFullPath(trimmed);
        return true;
    }

    public static string BuildLatestJsonUrl(string feedPath)
        => $"{feedPath.TrimEnd('/')}/latest.json";

    public static string BuildLocalInstallerPath(string directoryPath, string installerName)
    {
        var root = EnsureTrailingSeparator(Path.GetFullPath(directoryPath));
        var combined = Path.GetFullPath(Path.Combine(directoryPath, installerName));
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Installer path escapes update feed folder: {installerName}");

        return combined;
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
