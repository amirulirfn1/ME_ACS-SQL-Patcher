using System.Text.Json;
using MagDbPatcher.Models;
using Xunit;

namespace MagDbPatcher.Tests;

public class AppSettingsTests
{
    [Fact]
    public void AppSettings_Deserializes_ShowAdminTools_FromLegacyJson()
    {
        var json = """
                   {
                     "showAdminTools": true,
                     "lastSqlServer": ".\\MAGSQL"
                   }
                   """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var settings = JsonSerializer.Deserialize<AppSettings>(json, options);

        Assert.NotNull(settings);
        Assert.True(settings!.ShowAdminTools);
        Assert.Equal(".\\MAGSQL", settings.LastSqlServer);
    }
}
