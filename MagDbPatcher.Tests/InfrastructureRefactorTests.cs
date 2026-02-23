using MagDbPatcher.Infrastructure;
using MagDbPatcher.Models;
using Xunit;

namespace MagDbPatcher.Tests;

public class InfrastructureRefactorTests
{
    [Fact]
    public void RunUiStateController_TogglesExpectedFlags()
    {
        var controller = new RunUiStateController();

        var running = controller.GetState(isRunning: true);
        Assert.False(running.PatchEnabled);
        Assert.True(running.CancelEnabled);
        Assert.False(running.ImportPatchPackEnabled);

        var idle = controller.GetState(isRunning: false);
        Assert.True(idle.PatchEnabled);
        Assert.False(idle.CancelEnabled);
        Assert.True(idle.ImportPatchPackEnabled);
    }

    [Fact]
    public void SettingsBinder_BuildPersistedSettings_PreservesDefaults()
    {
        var binder = new SettingsBinder();
        var result = binder.BuildPersistedSettings(new SettingsPersistInput
        {
            Existing = new AppSettings
            {
                WarningThreshold = 0,
                PatchErrorMode = PatchErrorMode.WarnAndContinue,
                ShowAdminTools = true
            },
            PatchesFolder = @"C:\patches",
            LastSqlServer = @".\MAGSQL",
            RecentBackupFiles = new List<string> { @"C:\a.bak" },
            LastImportedPatchPack = "20260213",
            SqlAuthMode = SqlAuthMode.Windows
        });

        Assert.Equal(10, result.WarningThreshold);
        Assert.True(result.ShowAdminTools);
        Assert.Equal(@".\MAGSQL", result.LastSqlServer);
        Assert.Null(result.SqlUsername);
    }
}
