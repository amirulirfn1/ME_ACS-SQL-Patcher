using System.Windows;
using MagDbPatcher.Models;

namespace MagDbPatcher;

public partial class VersionEditDialog : Window
{
    public string VersionId => txtVersionId.Text.Trim();
    public string VersionName => txtVersionName.Text.Trim();
    public string? UpgradesTo => cmbUpgradesTo.SelectedValue as string;

    private record UpgradeTarget(string? Id, string Display);

    public VersionEditDialog(List<VersionInfo> versions)
    {
        InitializeComponent();
        Title = "Add Version";
        LoadUpgradeTargets(versions, null, null);
    }

    public VersionEditDialog(List<VersionInfo> versions, string id, string name, string? upgradesTo)
    {
        InitializeComponent();
        Title = "Edit Version";
        
        txtVersionId.Text = id;
        txtVersionId.IsEnabled = false; // Can't change ID when editing
        txtVersionName.Text = name;
        LoadUpgradeTargets(versions, id, upgradesTo);
    }

    private void LoadUpgradeTargets(List<VersionInfo> versions, string? currentId, string? currentUpgradesTo)
    {
        var ordered = versions
            .OrderBy(v => v.Order)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = string.IsNullOrWhiteSpace(currentId)
            ? null
            : ordered.FirstOrDefault(v => string.Equals(v.Id, currentId, StringComparison.OrdinalIgnoreCase));

        var choices = ordered
            .Where(v => !string.Equals(v.Id, currentId, StringComparison.OrdinalIgnoreCase));

        if (current != null)
        {
            choices = choices.Where(v => v.Order > current.Order);
        }

        var targets = new List<UpgradeTarget>
        {
            new UpgradeTarget(null, "(Latest / none)")
        };
        targets.AddRange(choices.Select(v => new UpgradeTarget(v.Id, v.Name)));

        cmbUpgradesTo.ItemsSource = targets;
        cmbUpgradesTo.SelectedValue = currentUpgradesTo;
        if (cmbUpgradesTo.SelectedIndex < 0)
            cmbUpgradesTo.SelectedIndex = 0;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VersionId))
        {
            MessageBox.Show("Version ID is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(VersionName))
        {
            MessageBox.Show("Display Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
