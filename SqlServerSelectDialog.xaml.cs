using System.Windows;
using MagDbPatcher.Services;

namespace MagDbPatcher;

public partial class SqlServerSelectDialog : Window
{
    public string ServerName { get; private set; } = "";

    public SqlServerSelectDialog()
    {
        InitializeComponent();

        cmbServer.ItemsSource = new[]
        {
            ".",
            "localhost",
            "(local)",
            ".\\SQLEXPRESS",
            "localhost\\SQLEXPRESS",
            "(localdb)\\MSSQLLocalDB",
            ".\\MAGSQL",
            "MAGSQL"
        };

        cmbServer.SelectedIndex = 0;
    }

    private async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        var server = (cmbServer.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(server))
        {
            txtResult.Text = "Enter a server name.";
            btnOk.IsEnabled = false;
            return;
        }

        btnTest.IsEnabled = false;
        txtResult.Text = "Testing...";
        btnOk.IsEnabled = false;

        try
        {
            var testService = new SqlServerService(server);
            var ok = await testService.TestConnectionAsync();
            if (ok)
            {
                txtResult.Text = "Connected";
                btnOk.IsEnabled = true;
            }
            else
            {
                txtResult.Text = "Failed to connect";
                btnOk.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            txtResult.Text = $"Failed: {ex.Message}";
            btnOk.IsEnabled = false;
        }
        finally
        {
            btnTest.IsEnabled = true;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var server = (cmbServer.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(server))
        {
            txtResult.Text = "Enter a server name.";
            return;
        }

        ServerName = server;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}


