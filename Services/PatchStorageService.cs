using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MagDbPatcher.Models;

namespace MagDbPatcher.Services;

public class PatchStorageService
{
    private readonly string _appDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagDbPatcher");

    public string GetDefaultUserPatchesFolder()
        => Path.Combine(_appDataRoot, "patches");

    public async Task<string> ResolvePatchesFolderAsync(AppSettings settings, string bundledPatchesFolder)
    {
        if (!string.IsNullOrWhiteSpace(settings.PatchesFolder))
            return Path.GetFullPath(settings.PatchesFolder);

        var writablePatchesFolder = GetDefaultUserPatchesFolder();
        await EnsureSeededAsync(writablePatchesFolder, bundledPatchesFolder);
        settings.PatchesFolder = writablePatchesFolder;
        return writablePatchesFolder;
    }

    public async Task EnsureSeededAsync(string targetPatchesFolder, string bundledPatchesFolder)
    {
        Directory.CreateDirectory(targetPatchesFolder);

        if (Directory.EnumerateFileSystemEntries(targetPatchesFolder).Any())
            return;

        if (!Directory.Exists(bundledPatchesFolder))
            return;

        await Task.Run(() => CopyDirectory(bundledPatchesFolder, targetPatchesFolder));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destinationFile = Path.Combine(destination, relative);
            var destinationDir = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDir))
                Directory.CreateDirectory(destinationDir);

            File.Copy(file, destinationFile, overwrite: true);
        }
    }
}
