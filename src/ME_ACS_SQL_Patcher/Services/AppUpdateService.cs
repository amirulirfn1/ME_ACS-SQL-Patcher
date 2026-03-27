using Velopack;

namespace MagDbPatcher.Services;

public interface IAppUpdateService
{
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(string? feedPath, CancellationToken cancellationToken = default);
    Task ApplyPendingUpdateAndRestartAsync(string feedPath, VelopackAsset? asset, CancellationToken cancellationToken = default);
}

public enum AppUpdateStatus
{
    NotConfigured,
    NotInstalled,
    NoUpdateAvailable,
    UpdateReadyToApply
}

public sealed record AppUpdateCheckResult(AppUpdateStatus Status, string Message, VelopackAsset? Asset = null)
{
    public bool CanApplyNow => Asset != null;
    public bool IsUpdateAvailable => Status == AppUpdateStatus.UpdateReadyToApply;
}

public sealed class VelopackAppUpdateService : IAppUpdateService
{
    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(string? feedPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedPath))
        {
            return new AppUpdateCheckResult(
                AppUpdateStatus.NotConfigured,
                "Set an app update feed path first. For a PC-hosted prototype, point this to a shared folder or URL that contains the Velopack release files.");
        }

        var updateManager = new UpdateManager(feedPath.Trim(), new UpdateOptions());
        if (!updateManager.IsInstalled)
        {
            return new AppUpdateCheckResult(
                AppUpdateStatus.NotInstalled,
                "Auto-update checks only work from the installed app. Install the setup build first, then try again.");
        }

        if (updateManager.UpdatePendingRestart != null)
        {
            return new AppUpdateCheckResult(
                AppUpdateStatus.UpdateReadyToApply,
                $"Update {updateManager.UpdatePendingRestart.Version} has already been downloaded and is ready to apply.",
                updateManager.UpdatePendingRestart);
        }

        var updates = await updateManager.CheckForUpdatesAsync();
        if (updates == null)
        {
            return new AppUpdateCheckResult(
                AppUpdateStatus.NoUpdateAvailable,
                "No newer app version is available from the configured update feed.");
        }

        await updateManager.DownloadUpdatesAsync(updates, progress: null, cancellationToken);
        return new AppUpdateCheckResult(
            AppUpdateStatus.UpdateReadyToApply,
            $"Update {updates.TargetFullRelease.Version} was downloaded from the configured feed. Apply it now and restart the app?",
            updates.TargetFullRelease);
    }

    public Task ApplyPendingUpdateAndRestartAsync(string feedPath, VelopackAsset? asset, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedPath))
            throw new ArgumentException("Update feed path is required.", nameof(feedPath));

        var updateManager = new UpdateManager(feedPath.Trim(), new UpdateOptions());
        updateManager.ApplyUpdatesAndRestart(asset, Array.Empty<string>());
        return Task.CompletedTask;
    }
}
