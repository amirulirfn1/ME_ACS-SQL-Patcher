using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MagDbPatcher.Models;
using Microsoft.Win32;

namespace MagDbPatcher;

public partial class PatchEditDialog : Window
{
    public string FromVersion => (cmbFromVersion.SelectedItem as VersionInfo)?.Id ?? "";
    public string ToVersion => (cmbToVersion.SelectedItem as VersionInfo)?.Id ?? "";
    public List<string> Scripts => _scripts.ToList();

    private readonly ObservableCollection<string> _scripts = new();
    private readonly List<string> _availableScripts;
    private readonly string _patchesFolder;

    public PatchEditDialog(List<VersionInfo> versions, List<string> availableScripts, string patchesFolder)
    {
        InitializeComponent();
        
        cmbFromVersion.ItemsSource = versions;
        cmbToVersion.ItemsSource = versions;
        _availableScripts = availableScripts;
        _patchesFolder = patchesFolder;
        
        lstScripts.ItemsSource = _scripts;
        UpdateScriptCount();
    }

    public PatchEditDialog(List<VersionInfo> versions, List<string> availableScripts, string patchesFolder, 
                           string fromVersion, string toVersion, List<string> scripts) 
        : this(versions, availableScripts, patchesFolder)
    {
        // Set selected versions
        cmbFromVersion.SelectedItem = versions.FirstOrDefault(v => v.Id == fromVersion);
        cmbToVersion.SelectedItem = versions.FirstOrDefault(v => v.Id == toVersion);
        
        // Load existing scripts
        foreach (var script in scripts)
        {
            _scripts.Add(script);
        }
        UpdateScriptCount();
    }

    private void UpdateScriptCount()
    {
        txtScriptCount.Text = $"({_scripts.Count} script{(_scripts.Count != 1 ? "s" : "")})";
    }

    private void LstScripts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = lstScripts.SelectedItem != null;
        btnRemoveScript.IsEnabled = hasSelection;
        btnMoveUp.IsEnabled = hasSelection && lstScripts.SelectedIndex > 0;
        btnMoveDown.IsEnabled = hasSelection && lstScripts.SelectedIndex < _scripts.Count - 1;
    }

    private void BtnAddScript_Click(object sender, RoutedEventArgs e)
    {
        // Show available scripts or browse for file
        var dialog = new OpenFileDialog
        {
            Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
            Title = "Select SQL Script",
            Multiselect = true,
            InitialDirectory = _patchesFolder
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                // Get relative path from patches folder
                var relativePath = file;
                if (file.StartsWith(_patchesFolder, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = file.Substring(_patchesFolder.Length).TrimStart('\\', '/');
                }
                
                if (!_scripts.Contains(relativePath))
                {
                    _scripts.Add(relativePath);
                }
            }
            UpdateScriptCount();
        }
    }

    private void BtnRemoveScript_Click(object sender, RoutedEventArgs e)
    {
        if (lstScripts.SelectedItem is string script)
        {
            _scripts.Remove(script);
            UpdateScriptCount();
        }
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        var index = lstScripts.SelectedIndex;
        if (index > 0)
        {
            var item = _scripts[index];
            _scripts.RemoveAt(index);
            _scripts.Insert(index - 1, item);
            lstScripts.SelectedIndex = index - 1;
        }
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        var index = lstScripts.SelectedIndex;
        if (index < _scripts.Count - 1)
        {
            var item = _scripts[index];
            _scripts.RemoveAt(index);
            _scripts.Insert(index + 1, item);
            lstScripts.SelectedIndex = index + 1;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (cmbFromVersion.SelectedItem == null || cmbToVersion.SelectedItem == null)
        {
            MessageBox.Show("Please select both From and To versions.", "Validation", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FromVersion == ToVersion)
        {
            MessageBox.Show("From and To versions cannot be the same.", "Validation", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
