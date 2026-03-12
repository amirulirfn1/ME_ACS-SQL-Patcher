using System.IO;
using System.Text;
using MagDbPatcher.Infrastructure;

namespace MagDbPatcher.Services;

public sealed class PortableAppBootstrapService
{
    private readonly AppRuntimePaths _appPaths;

    public PortableAppBootstrapService(AppRuntimePaths appPaths)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
    }

    public async Task EnsureReadyAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Preparing portable folders...");
        Directory.CreateDirectory(_appPaths.RootDirectory);
        Directory.CreateDirectory(_appPaths.LogsDirectory);
        Directory.CreateDirectory(_appPaths.BackupsDirectory);
        Directory.CreateDirectory(_appPaths.TempFolder);

        progress?.Report("Checking folder write access...");
        EnsureWritable(_appPaths.SettingsFilePath);
        EnsureWritable(Path.Combine(_appPaths.LogsDirectory, ".write-test"));

        progress?.Report("Verifying bundled patch library...");
        if (!Directory.Exists(_appPaths.PatchesFolder))
        {
            throw new InvalidOperationException(
                $"Portable package is missing the required patches folder:{Environment.NewLine}{_appPaths.PatchesFolder}");
        }

        var versionsPath = Path.Combine(_appPaths.PatchesFolder, "versions.json");
        if (!File.Exists(versionsPath))
        {
            throw new InvalidOperationException(
                $"Portable package is missing patches\\versions.json:{Environment.NewLine}{versionsPath}");
        }

        progress?.Report("Loading patch catalog...");
        var versionService = new VersionService(_appPaths.PatchesFolder);
        await versionService.LoadVersionsAsync();

        if (!versionService.LastValidationResult.HasErrors)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("Portable patch library validation failed.");
        builder.AppendLine("Fix the package contents and try again.");

        foreach (var error in versionService.LastValidationResult.Errors.Take(5))
            builder.AppendLine($"- {error.Message}");

        if (versionService.LastValidationResult.Errors.Count > 5)
            builder.AppendLine($"- {versionService.LastValidationResult.Errors.Count - 5} more error(s)");

        throw new InvalidOperationException(builder.ToString().Trim());
    }

    private static void EnsureWritable(string probePath)
    {
        var directory = Path.GetDirectoryName(probePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(probePath, "ok");
        File.Delete(probePath);
    }
}
