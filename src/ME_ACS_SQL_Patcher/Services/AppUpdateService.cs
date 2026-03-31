using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using MagDbPatcher.Infrastructure;

namespace MagDbPatcher.Services;

public interface IAppUpdateService
{
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(string? feedPath, CancellationToken cancellationToken = default);
    Task ApplyPendingUpdateAndRestartAsync(string installerUrl, CancellationToken cancellationToken = default);
}

public enum AppUpdateStatus
{
    NotConfigured,
    NoUpdateAvailable,
    UpdateAvailable
}

public sealed record AppUpdateCheckResult(AppUpdateStatus Status, string Message, string? InstallerUrl = null)
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
                "Set an app update feed URL first.");

        var baseUrl = feedPath.TrimEnd('/');
        var latestUrl = $"{baseUrl}/latest.json";

        string json;
        try
        {
            json = await Http.GetStringAsync(latestUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not reach {latestUrl}: {ex.Message}", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("buildDate", out var buildDateEl) ||
            !DateTime.TryParse(buildDateEl.GetString(), null, DateTimeStyles.RoundtripKind, out var serverDate))
        {
            throw new InvalidOperationException("latest.json is missing or has an invalid 'buildDate' field.");
        }

        var appDate = AppMetadata.BuildDate;
        if (serverDate <= appDate)
        {
            return new AppUpdateCheckResult(AppUpdateStatus.NoUpdateAvailable,
                $"App is up to date (built {appDate:yyyy-MM-dd HH:mm} UTC).");
        }

        var installerName = root.TryGetProperty("installerName", out var nameEl)
            ? nameEl.GetString() ?? "ME_ACS_SQL_Patcher-win-Setup.exe"
            : "ME_ACS_SQL_Patcher-win-Setup.exe";

        return new AppUpdateCheckResult(
            AppUpdateStatus.UpdateAvailable,
            $"A newer build is available (server: {serverDate:yyyy-MM-dd HH:mm} UTC, you have: {appDate:yyyy-MM-dd HH:mm} UTC). Download and install now?",
            $"{baseUrl}/{installerName}");
    }

    public async Task ApplyPendingUpdateAndRestartAsync(string installerUrl, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ME_ACS_SQL_Patcher-update");
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, "Setup.exe");

        using var response = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(localPath);
        await response.Content.CopyToAsync(fs, cancellationToken);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = localPath,
            UseShellExecute = true
        });

        System.Windows.Application.Current.Shutdown();
    }
}
