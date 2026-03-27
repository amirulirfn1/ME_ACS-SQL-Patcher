using System.Reflection;

namespace MagDbPatcher.Infrastructure;

public static class AppMetadata
{
    private static readonly Assembly EntryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

    public static string Title =>
        EntryAssembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
        ?? EntryAssembly.GetName().Name
        ?? "ME_ACS SQL Patcher";

    public static string DisplayVersion
    {
        get
        {
            var informational = EntryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+', 2)[0];

            var fileVersion = EntryAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            if (!string.IsNullOrWhiteSpace(fileVersion))
                return fileVersion;

            return EntryAssembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static string BuildLabel => $"Build {DisplayVersion}";
}
