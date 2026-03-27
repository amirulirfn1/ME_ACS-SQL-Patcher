using System;
using System.Windows;
using Velopack;

namespace MagDbPatcher;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
