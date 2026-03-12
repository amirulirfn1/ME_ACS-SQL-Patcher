using MagDbPatcher.Infrastructure;
using MagDbPatcher.Services;
using Xunit;

namespace MagDbPatcher.Tests;

public class PortableAppBootstrapServiceTests
{
    [Fact]
    public async Task EnsureReadyAsync_ThrowsWhenVersionsJsonMissing()
    {
        var root = CreateTempDir();
        try
        {
            var appPaths = new AppRuntimePaths(root);
            Directory.CreateDirectory(appPaths.PatchesFolder);

            var service = new PortableAppBootstrapService(appPaths);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureReadyAsync());
            Assert.Contains("versions.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task EnsureReadyAsync_CreatesPortableFolders_ForValidPackage()
    {
        var root = CreateTempDir();
        try
        {
            var appPaths = new AppRuntimePaths(root);
            Directory.CreateDirectory(Path.Combine(appPaths.PatchesFolder, "7.0"));
            await File.WriteAllTextAsync(Path.Combine(appPaths.PatchesFolder, "versions.json"), """
            {
              "versions": [
                { "id": "7.0", "name": "7.0", "upgradesTo": null, "order": 1 }
              ],
              "patches": []
            }
            """);

            var service = new PortableAppBootstrapService(appPaths);
            await service.EnsureReadyAsync();

            Assert.True(Directory.Exists(appPaths.LogsDirectory));
            Assert.True(Directory.Exists(appPaths.TempFolder));
            Assert.True(Directory.Exists(appPaths.BackupsDirectory));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task EnsureReadyAsync_ReportsProgress_ForValidPackage()
    {
        var root = CreateTempDir();
        try
        {
            var appPaths = new AppRuntimePaths(root);
            Directory.CreateDirectory(Path.Combine(appPaths.PatchesFolder, "7.0"));
            await File.WriteAllTextAsync(Path.Combine(appPaths.PatchesFolder, "versions.json"), """
            {
              "versions": [
                { "id": "7.0", "name": "7.0", "upgradesTo": null, "order": 1 }
              ],
              "patches": []
            }
            """);

            var updates = new List<string>();
            var service = new PortableAppBootstrapService(appPaths);

            await service.EnsureReadyAsync(new ImmediateProgress<string>(updates.Add));

            Assert.Contains(updates, message => message.Contains("Preparing portable folders", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(updates, message => message.Contains("Loading patch catalog", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MagDbPatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
