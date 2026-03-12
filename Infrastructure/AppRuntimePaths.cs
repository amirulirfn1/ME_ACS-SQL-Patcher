using System.IO;

namespace MagDbPatcher.Infrastructure;

public sealed class AppRuntimePaths
{
    private const string MachineDataFolderName = "ME_ACS_SQL_Patcher";

    public AppRuntimePaths(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Base directory is required.", nameof(baseDirectory));

        RootDirectory = Path.GetFullPath(baseDirectory);
        MachineDataDirectory = ResolveMachineDataDirectory();
    }

    public string RootDirectory { get; }
    public string MachineDataDirectory { get; }
    public string PatchesFolder => Path.Combine(RootDirectory, "patches");
    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public string DiagnosticsLogPath => Path.Combine(LogsDirectory, "diagnostics.log");
    public string StartupErrorLogPath => Path.Combine(LogsDirectory, "startup-errors.log");
    public string LegacyPortableTempFolder => Path.Combine(RootDirectory, "temp");
    public string TempFolder => Path.Combine(MachineDataDirectory, "temp");
    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public static AppRuntimePaths CreateDefault() => new(AppContext.BaseDirectory);

    public string ResolveTempFolder(string? configuredTempFolder)
    {
        if (string.IsNullOrWhiteSpace(configuredTempFolder))
            return TempFolder;

        var fullPath = Path.GetFullPath(configuredTempFolder);
        if (string.Equals(fullPath, Path.GetFullPath(LegacyPortableTempFolder), StringComparison.OrdinalIgnoreCase))
            return TempFolder;

        return fullPath;
    }

    private string ResolveMachineDataDirectory()
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonAppData))
            return RootDirectory;

        return Path.GetFullPath(Path.Combine(commonAppData, MachineDataFolderName));
    }
}
