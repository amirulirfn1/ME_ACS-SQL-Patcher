using MagDbPatcher.Models;
using MagDbPatcher.Infrastructure;
using MagDbPatcher.Services;
using Xunit;

namespace MagDbPatcher.Tests;

public class PatchStorageServiceTests
{
    [Fact]
    public async Task EnsureSeededAsync_CopiesBundledPatches_WhenTargetEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "MagDbPatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var bundled = Path.Combine(root, "bundled");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(bundled, "7.0"));
        await File.WriteAllTextAsync(Path.Combine(bundled, "versions.json"), """{ "versions": [], "patches": [] }""");
        await File.WriteAllTextAsync(Path.Combine(bundled, "7.0", "script.sql"), "SELECT 1;");

        var service = new PatchStorageService();
        await service.EnsureSeededAsync(target, bundled);

        Assert.True(File.Exists(Path.Combine(target, "versions.json")));
        Assert.True(File.Exists(Path.Combine(target, "7.0", "script.sql")));
    }

    [Fact]
    public async Task ResolvePatchesFolderAsync_UsesConfiguredFolder_WithoutOverriding()
    {
        var root = Path.Combine(Path.GetTempPath(), "MagDbPatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var configured = Path.Combine(root, "configured");
        Directory.CreateDirectory(configured);
        await File.WriteAllTextAsync(Path.Combine(configured, "keep.txt"), "1");

        var bundled = Path.Combine(root, "bundled");
        Directory.CreateDirectory(bundled);
        await File.WriteAllTextAsync(Path.Combine(bundled, "versions.json"), """{ "versions": [], "patches": [] }""");

        var settings = new AppSettings { PatchesFolder = configured };
        var service = new PatchStorageService();
        var resolved = await service.ResolvePatchesFolderAsync(settings, bundled);

        Assert.Equal(Path.GetFullPath(configured), resolved);
        Assert.True(File.Exists(Path.Combine(configured, "keep.txt")));
    }

    [Fact]
    public async Task ResolvePatchesFolderAsync_UsesPortableAppFolderByDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "MagDbPatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var appPaths = new AppRuntimePaths(root);
        Directory.CreateDirectory(appPaths.PatchesFolder);
        await File.WriteAllTextAsync(Path.Combine(appPaths.PatchesFolder, "versions.json"), """{ "versions": [], "patches": [] }""");

        var settings = new AppSettings();
        var service = new PatchStorageService(appPaths);
        var resolved = await service.ResolvePatchesFolderAsync(settings, appPaths.PatchesFolder);

        Assert.Equal(appPaths.PatchesFolder, resolved);
        Assert.Equal(appPaths.PatchesFolder, settings.PatchesFolder);
    }
}
