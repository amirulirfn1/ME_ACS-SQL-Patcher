using MagDbPatcher.Models;

namespace MagDbPatcher.Infrastructure;

public sealed class SettingsBinder
{
    public SettingsViewSnapshot BuildViewSnapshot(AppSettings settings)
    {
        return new SettingsViewSnapshot
        {
            LastImportedPack = settings.LastImportedPatchPack ?? string.Empty,
            RecentBackups = settings.RecentBackupFiles
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList(),
            LastSqlServer = settings.LastSqlServer ?? string.Empty,
            SqlUsername = settings.SqlUsername ?? string.Empty,
            SqlAuthMode = settings.SqlAuthMode
        };
    }

    public AppSettings BuildPersistedSettings(SettingsPersistInput input)
    {
        return new AppSettings
        {
            PatchTempFolder = input.Existing.PatchTempFolder,
            WarningThreshold = input.Existing.WarningThreshold <= 0 ? 10 : input.Existing.WarningThreshold,
            PatchErrorMode = input.Existing.PatchErrorMode,
            PatchesFolder = input.PatchesFolder,
            LastSqlServer = input.LastSqlServer,
            LastOutputFolder = input.LastOutputFolder,
            RecentBackupFiles = input.RecentBackupFiles.ToList(),
            LastImportedPatchPack = input.LastImportedPatchPack,
            SqlAuthMode = input.SqlAuthMode,
            SqlUsername = input.SqlAuthMode == SqlAuthMode.SqlLogin ? input.SqlUsername : null
        };
    }
}

public sealed class SettingsViewSnapshot
{
    public string LastImportedPack { get; init; } = string.Empty;
    public IReadOnlyList<string> RecentBackups { get; init; } = Array.Empty<string>();
    public string LastSqlServer { get; init; } = string.Empty;
    public string SqlUsername { get; init; } = string.Empty;
    public SqlAuthMode SqlAuthMode { get; init; } = SqlAuthMode.Windows;
}

public sealed class SettingsPersistInput
{
    public AppSettings Existing { get; init; } = new();
    public string PatchesFolder { get; init; } = string.Empty;
    public string LastSqlServer { get; init; } = string.Empty;
    public string? LastOutputFolder { get; init; }
    public IReadOnlyList<string> RecentBackupFiles { get; init; } = Array.Empty<string>();
    public string? LastImportedPatchPack { get; init; }
    public SqlAuthMode SqlAuthMode { get; init; } = SqlAuthMode.Windows;
    public string? SqlUsername { get; init; }
}
