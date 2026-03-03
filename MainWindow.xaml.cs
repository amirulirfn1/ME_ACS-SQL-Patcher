using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MagDbPatcher.Infrastructure;
using MagDbPatcher.Models;
using MagDbPatcher.Services;
using MagDbPatcher.ViewModels;
using MagDbPatcher.Workflows;
using Microsoft.Win32;

namespace MagDbPatcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly PatchStorageService _patchStorageService = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly RunRequestBuilder _requestBuilder = new();
    private readonly DiagnosticsComposer _diagnosticsComposer = new();
    private readonly SettingsBinder _settingsBinder = new();
    private readonly RunUiStateController _runUiStateController = new();
    private readonly ObservableCollection<string> _runWarnings = new();
    private readonly StringBuilder _logBuilder = new();
    private readonly Queue<string> _pendingLogLines = new();
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private AppSettings _settings = new();
    private VersionService? _versionService;
    private PatchRunCoordinator? _runCoordinator;
    private PatchPackImportCoordinator? _patchImportCoordinator;
    private CancellationTokenSource? _runCancellation;
    private AdminWindow? _adminWindow;
    private string _defaultPatchesFolder = "";
    private string _lastOutputPath = "";
    private string _lastSqlTestSignature = "";
    private bool _sqlConnectionTestPassed;
    private bool _isLoadingUi;
    private bool _isUiInitialized;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = _viewModel;

        cmbSourcePath.ItemsSource = _viewModel.RecentBackupFiles;
        cmbFromVersion.ItemsSource = _viewModel.SourceVersions;
        cmbToVersion.ItemsSource = _viewModel.TargetVersions;
        lstValidationIssues.ItemsSource = _viewModel.ValidationIssues;
        lstRunWarnings.ItemsSource = _runWarnings;

        lstValidationIssues.DisplayMemberPath = nameof(ValidationIssue.Message);
        cmbSourcePath.LostFocus += (_, _) => RefreshRunSummary();
        cmbSqlServer.LostFocus += (_, _) => RefreshRunSummary();
        _logFlushTimer.Tick += (_, _) => FlushPendingLogLines();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        ApplyReadyState();
        _isUiInitialized = true;
    }

    private enum StepUiState
    {
        Pending,
        Ready,
        Done,
        NeedsAttention
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoadingUi = true;
        try
        {
            _defaultPatchesFolder = Path.Combine(AppContext.BaseDirectory, "patches");
            _settings = await _settingsService.LoadAsync();
            var initialPatchesFolder = await _patchStorageService.ResolvePatchesFolderAsync(_settings, _defaultPatchesFolder);

            ApplySettingsToUi();
            await LoadSqlServerSuggestionsAsync();
            await SetPatchesFolderAsync(initialPatchesFolder, closeAdminWindow: false);
            RefreshRunSummary();
            await PersistSettingsAsync();
            FocusPrimaryInput();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"Startup failed: {ex.Message}", "Startup Error");
            SetBanner(NotificationLevel.Error, "Startup failed. See error message.");
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_runCancellation != null)
        {
            var answer = MessageBox.Show(
                "A patch run is still in progress. Cancel it and close the app?",
                "Close Application",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _runCancellation.Cancel();
        }

        if (_adminWindow != null)
        {
            _adminWindow.Close();
            _adminWindow = null;
        }

        await PersistSettingsAsync();
    }

    private void ApplyReadyState()
    {
        txtVersion.Text = "";
        txtStatus.Text = "Ready";
        txtResultSummary.Text = "";
        txtLog.Text = "";
        txtSqlTestResult.Text = "";

        progressBar.Value = 0;
        _viewModel.ClearValidation();
        _runWarnings.Clear();
        UpdateWarningChip(0);
        bdStatusBanner.Visibility = Visibility.Collapsed;

        txtRunSummaryVersions.Text = "Versions: Select source and target versions";
        txtRunSummaryPlan.Text = "Plan: Select versions to preview steps and script count.";
        txtRunSummaryTemp.Text = "Temp folder: C:\\temp\\MagDbPatcher";
        txtRunSummaryOutputAndWarnings.Text = "Output: Not generated yet";

        btnRetryFromValidation.IsEnabled = false;
        btnCopyDiagnostics.IsEnabled = false;
        btnOpenOutputFolder.IsEnabled = false;
        btnCancel.IsEnabled = false;

        txtNextAction.Text = "Next: Select a source backup file.";
        txtPatchActionHint.Text = "Complete Step 1 to continue.";
        SetStepStatusChip(bdStep1Status, txtStep1Status, "Pending", StepUiState.Pending);
        SetStepStatusChip(bdStep2Status, txtStep2Status, "Pending", StepUiState.Pending);
        SetStepStatusChip(bdStep3Status, txtStep3Status, "Pending", StepUiState.Pending);
        SetStepStatusChip(bdStep4Status, txtStep4Status, "Pending", StepUiState.Pending);
    }

    private void ApplySettingsToUi()
    {
        var snapshot = _settingsBinder.BuildViewSnapshot(_settings);
        txtLastImportedPack.Text = snapshot.LastImportedPack;

        _viewModel.RecentBackupFiles.Clear();
        foreach (var item in snapshot.RecentBackups)
            _viewModel.RecentBackupFiles.Add(item);

        if (_viewModel.RecentBackupFiles.Count > 0)
            cmbSourcePath.SelectedIndex = 0;

        cmbSqlServer.Text = snapshot.LastSqlServer;
        txtSqlUsername.Text = snapshot.SqlUsername;

        rbAuthSql.IsChecked = snapshot.SqlAuthMode == SqlAuthMode.SqlLogin;
        rbAuthWindows.IsChecked = snapshot.SqlAuthMode != SqlAuthMode.SqlLogin;
        UpdateAuthModeUi();
    }

    private string GetCurrentPatchesFolder()
    {
        if (!string.IsNullOrWhiteSpace(txtPatchesFolder.Text))
            return txtPatchesFolder.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.PatchesFolder))
            return _settings.PatchesFolder!;
        return _patchStorageService.GetDefaultUserPatchesFolder();
    }

    private async Task LoadSqlServerSuggestionsAsync()
    {
        var serverService = new SqlServerService(".");
        var discovered = await serverService.GetAvailableSqlServersAsync();

        var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".",
            "localhost",
            "(local)",
            ".\\SQLEXPRESS",
            "(localdb)\\MSSQLLocalDB",
            ".\\MAGSQL"
        };

        foreach (var server in discovered)
            suggestions.Add(server);

        if (!string.IsNullOrWhiteSpace(_settings.LastSqlServer))
            suggestions.Add(_settings.LastSqlServer);

        cmbSqlServer.ItemsSource = suggestions.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrWhiteSpace(cmbSqlServer.Text))
            cmbSqlServer.Text = ".\\MAGSQL";
    }

    private async Task SetPatchesFolderAsync(string folder, bool closeAdminWindow = true)
    {
        var normalized = string.IsNullOrWhiteSpace(folder)
            ? _defaultPatchesFolder
            : Path.GetFullPath(folder.Trim());

        if (!Directory.Exists(normalized))
            Directory.CreateDirectory(normalized);

        txtPatchesFolder.Text = normalized;
        _settings.PatchesFolder = normalized;

        _versionService = new VersionService(normalized);
        await _versionService.LoadVersionsAsync();

        _runCoordinator = new PatchRunCoordinator(_versionService);
        _patchImportCoordinator = new PatchPackImportCoordinator(new PatchPackService());

        if (closeAdminWindow && _adminWindow != null)
        {
            _adminWindow.Close();
            _adminWindow = null;
        }

        RefreshVersionSelectors(tryPreserveSelection: true);
        RefreshRunSummary();
    }

    private void RefreshVersionSelectors(bool tryPreserveSelection)
    {
        if (_versionService == null)
            return;

        var previousFrom = tryPreserveSelection ? GetSelectedVersionId(cmbFromVersion) : null;
        var previousTo = tryPreserveSelection ? GetSelectedVersionId(cmbToVersion) : null;

        _viewModel.SourceVersions.Clear();
        foreach (var version in _versionService.GetSourceVersions())
            _viewModel.SourceVersions.Add(version);

        if (!string.IsNullOrWhiteSpace(previousFrom))
        {
            cmbFromVersion.SelectedItem = _viewModel.SourceVersions.FirstOrDefault(v =>
                string.Equals(v.Id, previousFrom, StringComparison.OrdinalIgnoreCase));
        }

        if (cmbFromVersion.SelectedItem == null && _viewModel.SourceVersions.Count > 0)
            cmbFromVersion.SelectedIndex = 0;

        RefreshTargetVersions(previousTo);
        RefreshPatchVersionBadge();
    }

    private void RefreshPatchVersionBadge()
    {
        if (_versionService == null)
        {
            txtVersion.Text = "";
            return;
        }

        var latest = _versionService.GetAllVersions()
            .OrderBy(v => v.Order)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();

        txtVersion.Text = latest == null ? "Patch unknown" : $"Patch {latest.Id}";
    }

    private void RefreshTargetVersions(string? preferredTargetId = null)
    {
        if (_versionService == null)
            return;

        var fromVersionId = GetSelectedVersionId(cmbFromVersion);
        _viewModel.TargetVersions.Clear();

        if (string.IsNullOrWhiteSpace(fromVersionId))
            return;

        foreach (var version in _versionService.GetTargetVersions(fromVersionId))
            _viewModel.TargetVersions.Add(version);

        if (!string.IsNullOrWhiteSpace(preferredTargetId))
        {
            cmbToVersion.SelectedItem = _viewModel.TargetVersions.FirstOrDefault(v =>
                string.Equals(v.Id, preferredTargetId, StringComparison.OrdinalIgnoreCase));
        }

        if (cmbToVersion.SelectedItem == null && _viewModel.TargetVersions.Count > 0)
            cmbToVersion.SelectedIndex = _viewModel.TargetVersions.Count - 1;

        UpdateUpgradePathText();
    }

    private static string? GetSelectedVersionId(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is VersionInfo versionInfo)
            return versionInfo.Id;
        return comboBox.SelectedValue as string;
    }

    private void UpdateUpgradePathText()
    {
        if (_versionService == null)
        {
            bdUpgradePath.Visibility = Visibility.Collapsed;
            return;
        }

        var fromVersion = GetSelectedVersionId(cmbFromVersion);
        var toVersion = GetSelectedVersionId(cmbToVersion);

        if (string.IsNullOrWhiteSpace(fromVersion) || string.IsNullOrWhiteSpace(toVersion))
        {
            bdUpgradePath.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var steps = _versionService.CalculateUpgradePath(fromVersion, toVersion);
            var totalScripts = steps.Sum(s => s.Scripts.Count);
            var segments = new List<string> { fromVersion };
            segments.AddRange(steps.Select(s => s.ToVersion));
            txtUpgradePath.Text = $"{string.Join("  →  ", segments)}    ({steps.Count} step{(steps.Count == 1 ? "" : "s")}, {totalScripts} script{(totalScripts == 1 ? "" : "s")})";
            txtUpgradePath.Foreground = (Brush)FindResource("TextPrimary");
        }
        catch (Exception ex)
        {
            txtUpgradePath.Text = $"No path available: {ex.Message}";
            txtUpgradePath.Foreground = (Brush)FindResource("Error");
        }

        bdUpgradePath.Visibility = Visibility.Visible;
    }

    private void RefreshRunSummary()
    {
        if (!_isUiInitialized ||
            txtRunSummaryVersions == null ||
            txtRunSummaryTemp == null ||
            txtRunSummaryOutputAndWarnings == null ||
            txtRunSummaryPlan == null ||
            cmbSourcePath == null ||
            cmbFromVersion == null ||
            cmbToVersion == null ||
            cmbSqlServer == null)
        {
            return;
        }

        var sourcePath = (cmbSourcePath.Text ?? "").Trim();

        // File hint for Step 1
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            txtSourceFileHint.Text = "";
        }
        else if (!File.Exists(sourcePath))
        {
            txtSourceFileHint.Text = "File not found.";
            txtSourceFileHint.Foreground = (Brush)FindResource("Error");
        }
        else
        {
            var info = new FileInfo(sourcePath);
            var size = info.Length >= 1024L * 1024 * 1024
                ? $"{info.Length / (1024.0 * 1024 * 1024):F1} GB"
                : $"{info.Length / (1024.0 * 1024):F0} MB";
            txtSourceFileHint.Text = $"{info.Name}  ({size})";
            txtSourceFileHint.Foreground = (Brush)FindResource("Success");
        }

        var fromVersion = GetSelectedVersionId(cmbFromVersion) ?? "(not selected)";
        var toVersion = GetSelectedVersionId(cmbToVersion) ?? "(not selected)";
        var tempFolder = _settings.PatchTempFolder ?? @"C:\temp\MagDbPatcher";

        var sqlServer = string.IsNullOrWhiteSpace(cmbSqlServer.Text) ? ".\\MAGSQL" : cmbSqlServer.Text.Trim();
        txtRunSummaryVersions.Text = $"Versions: {fromVersion} -> {toVersion}\nSQL: {sqlServer}";
        txtRunSummaryTemp.Text = $"Temp folder: {tempFolder}";

        if (string.IsNullOrWhiteSpace(sourcePath))
            txtRunSummaryOutputAndWarnings.Text = "Output: Select a source backup to preview output path";
        else
            txtRunSummaryOutputAndWarnings.Text = $"Output: {BuildOutputBakPath(sourcePath, toVersion)}";

        if (_versionService == null || fromVersion == "(not selected)" || toVersion == "(not selected)")
        {
            txtRunSummaryPlan.Text = "Plan: Select source and target versions.";
            return;
        }

        try
        {
            var steps = _versionService.CalculateUpgradePath(fromVersion, toVersion);
            var totalScripts = steps.Sum(s => s.Scripts.Count);
            txtRunSummaryPlan.Text = $"Plan: {steps.Count} step(s), {totalScripts} script(s)";
        }
        catch (Exception ex)
        {
            txtRunSummaryPlan.Text = $"Plan: {ex.Message}";
        }

        UpdateGuidanceUi();
    }

    private void UpdateGuidanceUi()
    {
        var sourcePath = (cmbSourcePath.Text ?? "").Trim();
        var fromVersion = GetSelectedVersionId(cmbFromVersion);
        var toVersion = GetSelectedVersionId(cmbToVersion);
        var settings = BuildSqlConnectionSettings();
        var sqlSignature = BuildSqlSignature(settings);

        if (!string.Equals(sqlSignature, _lastSqlTestSignature, StringComparison.Ordinal))
            _sqlConnectionTestPassed = false;

        var sourceExists = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath);
        var sourcePathEntered = !string.IsNullOrWhiteSpace(sourcePath);
        var step1State = sourceExists ? StepUiState.Done : (sourcePathEntered ? StepUiState.NeedsAttention : StepUiState.Pending);

        var versionSelectionPresent = !string.IsNullOrWhiteSpace(fromVersion) && !string.IsNullOrWhiteSpace(toVersion);
        var versionPathValid = false;
        if (versionSelectionPresent && _versionService != null)
        {
            try
            {
                _ = _versionService.CalculateUpgradePath(fromVersion!, toVersion!);
                versionPathValid = true;
            }
            catch
            {
                versionPathValid = false;
            }
        }

        var step2State = versionPathValid ? StepUiState.Done : (versionSelectionPresent ? StepUiState.NeedsAttention : StepUiState.Pending);

        var localSql = !string.IsNullOrWhiteSpace(settings.Server) && LocalSqlValidator.IsLocalServer(settings.Server);
        var hasSqlUser = settings.AuthMode != SqlAuthMode.SqlLogin || !string.IsNullOrWhiteSpace(settings.Username);
        var hasSqlPassword = settings.AuthMode != SqlAuthMode.SqlLogin || !string.IsNullOrWhiteSpace(settings.Password);
        var basicConnectionReady = localSql && hasSqlUser && hasSqlPassword;
        var step3State = _sqlConnectionTestPassed && basicConnectionReady
            ? StepUiState.Done
            : (basicConnectionReady ? StepUiState.Ready : StepUiState.NeedsAttention);

        var runReady = sourceExists && versionPathValid && _sqlConnectionTestPassed && basicConnectionReady && _runCoordinator != null;
        if (runReady && _runCoordinator != null)
        {
            runReady = ValidateCurrentRequest(requirePassword: true).IsValid;
        }

        var step4State = runReady
            ? StepUiState.Ready
            : ((sourceExists || versionSelectionPresent || basicConnectionReady) ? StepUiState.Pending : StepUiState.Pending);

        SetStepStatusChip(bdStep1Status, txtStep1Status, GetStepText(step1State), step1State);
        SetStepStatusChip(bdStep2Status, txtStep2Status, GetStepText(step2State), step2State);
        SetStepStatusChip(bdStep3Status, txtStep3Status, GetStepText(step3State), step3State);
        SetStepStatusChip(bdStep4Status, txtStep4Status, runReady ? "Ready" : "Pending", step4State);

        txtNextAction.Text = GetNextActionText(sourceExists, versionPathValid, basicConnectionReady, _sqlConnectionTestPassed);
        txtPatchActionHint.Text = runReady
            ? "Ready to run. Click Start Patch."
            : GetPatchHint(sourceExists, versionPathValid, basicConnectionReady, _sqlConnectionTestPassed);

        if (_runCancellation == null)
            btnPatch.IsEnabled = runReady;
    }

    private static string BuildSqlSignature(SqlConnectionSettings settings)
        => $"{settings.Server}|{settings.AuthMode}|{settings.Username}|{settings.Password}";

    private static string GetStepText(StepUiState state) => state switch
    {
        StepUiState.Done => "Done",
        StepUiState.Ready => "Ready",
        StepUiState.NeedsAttention => "Needs Attention",
        _ => "Pending"
    };

    private static string GetNextActionText(bool sourceDone, bool versionsDone, bool basicConnectionReady, bool sqlTestPassed)
    {
        if (!sourceDone)
            return "Next: Select a source backup file.";
        if (!versionsDone)
            return "Next: Choose the target version.";
        if (!basicConnectionReady)
            return "Next: Complete SQL connection details.";
        if (!sqlTestPassed)
            return "Next: Click Test SQL connection.";
        return "Next: Click Start Patch.";
    }

    private static string GetPatchHint(bool sourceDone, bool versionsDone, bool basicConnectionReady, bool sqlTestPassed)
    {
        if (!sourceDone)
            return "Complete Step 1 to continue.";
        if (!versionsDone)
            return "Complete Step 2 to continue.";
        if (!basicConnectionReady || !sqlTestPassed)
            return "Complete Step 3 (Test SQL) to continue.";
        return "Review details and continue.";
    }

    private void SetStepStatusChip(Border border, TextBlock textBlock, string text, StepUiState state)
    {
        textBlock.Text = text;

        switch (state)
        {
            case StepUiState.Done:
                border.Background = (Brush)FindResource("SuccessSoft");
                border.BorderBrush = (Brush)FindResource("Success");
                textBlock.Foreground = (Brush)FindResource("Success");
                break;
            case StepUiState.Ready:
                border.Background = (Brush)FindResource("InfoSoft");
                border.BorderBrush = (Brush)FindResource("Info");
                textBlock.Foreground = (Brush)FindResource("Info");
                break;
            case StepUiState.NeedsAttention:
                border.Background = (Brush)FindResource("WarningSoft");
                border.BorderBrush = (Brush)FindResource("Warning");
                textBlock.Foreground = (Brush)FindResource("Warning");
                break;
            default:
                border.Background = (Brush)FindResource("SurfaceSubtle");
                border.BorderBrush = (Brush)FindResource("BorderStrong");
                textBlock.Foreground = (Brush)FindResource("TextMuted");
                break;
        }
    }

    private string BuildOutputBakPath(string sourceBakPath, string toVersionId)
        => _requestBuilder.BuildOutputBakPath(sourceBakPath, toVersionId);

    private void UpdateAuthModeUi()
    {
        if (pnlSqlLogin == null || rbAuthSql == null)
            return;

        pnlSqlLogin.Visibility = rbAuthSql.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBanner(NotificationLevel level, string message, bool warningBanner = false)
    {
        txtNotification.Text = message;
        bdStatusBanner.Visibility = Visibility.Visible;

        if (warningBanner)
        {
            bdStatusBanner.Style = (Style)FindResource("StatusBannerWarning");
            return;
        }

        switch (level)
        {
            case NotificationLevel.Success:
                bdStatusBanner.Style = (Style)FindResource("StatusBannerSuccess");
                break;
            case NotificationLevel.Error:
                bdStatusBanner.Style = (Style)FindResource("StatusBannerError");
                break;
            default:
                bdStatusBanner.Style = (Style)FindResource("StatusBannerInfo");
                break;
        }
    }

    private void SetValidationIssues(IEnumerable<ValidationIssue> issues)
    {
        _viewModel.SetValidationIssues(issues);
        btnRetryFromValidation.IsEnabled = _viewModel.ValidationIssues.Count > 0;
        lstValidationIssues.Visibility = _viewModel.ValidationIssues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearValidationIssues()
    {
        _viewModel.ClearValidation();
        btnRetryFromValidation.IsEnabled = false;
        lstValidationIssues.Visibility = Visibility.Collapsed;
    }

    private void UpdateWarningChip(int count)
    {
        txtWarningChip.Text = count.ToString(CultureInfo.InvariantCulture);

        if (count > 0)
        {
            bdWarningChip.Background = (Brush)FindResource("WarningSoft");
            txtWarningChip.Foreground = (Brush)FindResource("Warning");
        }
        else
        {
            bdWarningChip.Background = (Brush)FindResource("InfoSoft");
            txtWarningChip.Foreground = (Brush)FindResource("TextSecondary");
        }
    }

    private SqlConnectionSettings BuildSqlConnectionSettings()
    {
        return new SqlConnectionSettings
        {
            Server = (cmbSqlServer.Text ?? "").Trim(),
            AuthMode = rbAuthSql.IsChecked == true ? SqlAuthMode.SqlLogin : SqlAuthMode.Windows,
            Username = (txtSqlUsername.Text ?? "").Trim(),
            Password = pwdSqlPassword.Password
        };
    }

    private PatchRunRequest BuildRunRequest()
    {
        var sourceBakPath = (cmbSourcePath.Text ?? "").Trim();
        var fromVersion = GetSelectedVersionId(cmbFromVersion) ?? "";
        var toVersion = GetSelectedVersionId(cmbToVersion) ?? "";
        var connectionSettings = BuildSqlConnectionSettings();
        return _requestBuilder.Build(sourceBakPath, fromVersion, toVersion, _settings, connectionSettings);
    }

    private (bool IsValid, PatchRunRequest Request, List<ValidationIssue> Issues) ValidateCurrentRequest(bool requirePassword)
    {
        if (_runCoordinator == null)
        {
            return (false, new PatchRunRequest(), new List<ValidationIssue>
            {
                new() { Field = "Application", Message = "Patching service is not initialized yet." }
            });
        }

        var request = BuildRunRequest();
        var issues = _runCoordinator.Validate(request, requirePassword).ToList();
        return (issues.Count == 0, request, issues);
    }

    private void AppendLogLine(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _pendingLogLines.Enqueue(stamped);
        if (!_logFlushTimer.IsEnabled)
            _logFlushTimer.Start();
    }

    private void FlushPendingLogLines()
    {
        if (_pendingLogLines.Count == 0)
        {
            _logFlushTimer.Stop();
            return;
        }

        while (_pendingLogLines.Count > 0)
        {
            _logBuilder.AppendLine(_pendingLogLines.Dequeue());
        }

        txtLog.Text = _logBuilder.ToString();
    }

    private void ApplyRunUiLock(bool isRunning)
    {
        var state = _runUiStateController.GetState(isRunning);
        btnPatch.IsEnabled = state.PatchEnabled;
        btnCancel.IsEnabled = state.CancelEnabled;
        btnBrowse.IsEnabled = state.BrowseEnabled;
        cmbSourcePath.IsEnabled = state.SourceSelectorEnabled;
        cmbFromVersion.IsEnabled = state.FromSelectorEnabled;
        cmbToVersion.IsEnabled = state.ToSelectorEnabled;
        btnPatchToLatest.IsEnabled = state.PatchToLatestEnabled;
        btnAdminTools.IsEnabled = state.AdminToolsEnabled;
        btnCopyPatchPlan.IsEnabled = state.CopyPatchPlanEnabled;
        btnImportPatchPack.IsEnabled = state.ImportPatchPackEnabled;
        UpdateGuidanceUi();
    }

    private void RememberRecentBackup(string sourceBakPath)
    {
        if (string.IsNullOrWhiteSpace(sourceBakPath))
            return;

        var normalized = sourceBakPath.Trim();
        var existing = _viewModel.RecentBackupFiles
            .FirstOrDefault(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _viewModel.RecentBackupFiles.Remove(existing);

        _viewModel.RecentBackupFiles.Insert(0, normalized);
        while (_viewModel.RecentBackupFiles.Count > 5)
            _viewModel.RecentBackupFiles.RemoveAt(_viewModel.RecentBackupFiles.Count - 1);
    }

    private async Task PersistSettingsAsync()
    {
        _settings = _settingsBinder.BuildPersistedSettings(new SettingsPersistInput
        {
            Existing = _settings,
            PatchesFolder = txtPatchesFolder.Text.Trim(),
            LastSqlServer = (cmbSqlServer.Text ?? "").Trim(),
            LastOutputFolder = string.IsNullOrWhiteSpace(_lastOutputPath) ? _settings.LastOutputFolder : Path.GetDirectoryName(_lastOutputPath),
            RecentBackupFiles = _viewModel.RecentBackupFiles.ToList(),
            LastImportedPatchPack = string.IsNullOrWhiteSpace(txtLastImportedPack.Text) ? _settings.LastImportedPatchPack : txtLastImportedPack.Text,
            SqlAuthMode = rbAuthSql.IsChecked == true ? SqlAuthMode.SqlLogin : SqlAuthMode.Windows,
            SqlUsername = txtSqlUsername.Text.Trim()
        });

        await _settingsService.SaveAsync(_settings);
    }

    private async Task RefreshMainAfterAdminMutationAsync()
    {
        if (_versionService == null)
            return;

        await _versionService.LoadVersionsAsync();
        RefreshVersionSelectors(tryPreserveSelection: true);
        RefreshRunSummary();
    }

    private string BuildPatchPlanText()
    {
        var request = BuildRunRequest();
        return _diagnosticsComposer.BuildPatchPlan(request, _versionService);
    }

    private string BuildDiagnosticsText()
    {
        FlushPendingLogLines();
        return _diagnosticsComposer.BuildDiagnostics(
            txtStatus.Text,
            txtResultSummary.Text,
            _runWarnings,
            _logBuilder.ToString(),
            BuildPatchPlanText(),
            _versionService?.NonFatalDiagnostics);
    }

    private void BtnAdminTools_Click(object sender, RoutedEventArgs e)
    {
        if (_versionService == null)
        {
            _dialogs.ShowWarning("Patches are still loading. Try again in a moment.", "Admin Tools");
            return;
        }

        if (_adminWindow is { IsLoaded: true })
        {
            _adminWindow.Activate();
            _adminWindow.Focus();
            return;
        }

        _adminWindow = new AdminWindow(
            _versionService,
            RefreshMainAfterAdminMutationAsync,
            getCurrentPatchesFolder: GetCurrentPatchesFolder,
            setPatchesFolderAsync: SetPatchesFolderFromAdminAsync,
            resetPatchesFolderAsync: ResetPatchesFolderFromAdminAsync,
            persistSettingsAsync: PersistSettingsAsync,
            getVersionService: () => _versionService)
        {
            Owner = this
        };
        _adminWindow.Closed += (_, _) => _adminWindow = null;
        _adminWindow.Show();
    }

    private async Task SetPatchesFolderFromAdminAsync(string folder)
    {
        await SetPatchesFolderAsync(folder, closeAdminWindow: false);
    }

    private async Task<string> ResetPatchesFolderFromAdminAsync()
    {
        var writableFolder = _patchStorageService.GetDefaultUserPatchesFolder();
        await _patchStorageService.EnsureSeededAsync(writableFolder, _defaultPatchesFolder);
        await SetPatchesFolderAsync(writableFolder, closeAdminWindow: false);
        return writableFolder;
    }

    private void CmbSourcePath_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        var selected = (cmbSourcePath.SelectedItem as string) ?? cmbSourcePath.Text;
        if (!string.IsNullOrWhiteSpace(selected))
            RememberRecentBackup(selected);

        RefreshRunSummary();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Backup Files (*.bak)|*.bak|All Files (*.*)|*.*",
            Title = "Select source backup file"
        };

        if (dialog.ShowDialog() != true)
            return;

        cmbSourcePath.Text = dialog.FileName;
        RememberRecentBackup(dialog.FileName);
        RefreshRunSummary();
    }

    private void CmbFromVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        RefreshTargetVersions();
        RefreshRunSummary();
    }

    private void CmbToVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        UpdateUpgradePathText();
        RefreshRunSummary();
    }

    private void BtnPatchToLatest_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TargetVersions.Count == 0)
            return;

        cmbToVersion.SelectedIndex = _viewModel.TargetVersions.Count - 1;
    }

    private void AuthMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
            return;

        _sqlConnectionTestPassed = false;
        UpdateAuthModeUi();
        RefreshRunSummary();
    }

    private void FocusPrimaryInput()
    {
        if (!string.IsNullOrWhiteSpace(cmbSourcePath.Text))
        {
            cmbFromVersion.Focus();
            return;
        }

        cmbSourcePath.Focus();
    }

    private void FocusFirstIncompleteStep()
    {
        var sourcePath = (cmbSourcePath.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            cmbSourcePath.Focus();
            return;
        }

        if (GetSelectedVersionId(cmbFromVersion) == null || GetSelectedVersionId(cmbToVersion) == null)
        {
            if (GetSelectedVersionId(cmbFromVersion) == null)
                cmbFromVersion.Focus();
            else
                cmbToVersion.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(cmbSqlServer.Text))
        {
            cmbSqlServer.Focus();
            return;
        }

        btnTestSql.Focus();
    }

    private async void BtnTestSql_Click(object sender, RoutedEventArgs e)
    {
        if (_runCoordinator == null)
            return;

        var settings = BuildSqlConnectionSettings();
        if (string.IsNullOrWhiteSpace(settings.Server))
        {
            txtSqlTestResult.Text = "Enter a SQL Server value.";
            txtSqlTestResult.Foreground = (Brush)FindResource("Error");
            return;
        }

        if (!LocalSqlValidator.IsLocalServer(settings.Server))
        {
            txtSqlTestResult.Text = "Only local SQL Server instances are allowed.";
            txtSqlTestResult.Foreground = (Brush)FindResource("Error");
            return;
        }

        btnTestSql.IsEnabled = false;
        txtSqlTestResult.Text = "Testing...";
        txtSqlTestResult.Foreground = (Brush)FindResource("TextMuted");

        try
        {
            var ok = await _runCoordinator.TestConnectionAsync(settings);
            if (ok)
            {
                _sqlConnectionTestPassed = true;
                _lastSqlTestSignature = BuildSqlSignature(settings);
                txtSqlTestResult.Text = "Connection successful.";
                txtSqlTestResult.Foreground = (Brush)FindResource("Success");
                SetBanner(NotificationLevel.Success, "SQL connection successful.");
            }
            else
            {
                _sqlConnectionTestPassed = false;
                txtSqlTestResult.Text = "Failed to connect.";
                txtSqlTestResult.Foreground = (Brush)FindResource("Error");
                SetBanner(NotificationLevel.Error, "SQL connection failed. Check server/auth settings.");
            }
        }
        catch (Exception ex)
        {
            _sqlConnectionTestPassed = false;
            txtSqlTestResult.Text = ex.Message;
            txtSqlTestResult.Foreground = (Brush)FindResource("Error");
            SetBanner(NotificationLevel.Error, "SQL connection test failed.");
        }
        finally
        {
            btnTestSql.IsEnabled = true;
            UpdateGuidanceUi();
        }
    }

    private async void BtnChangePatchesFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select patches folder",
            InitialDirectory = GetCurrentPatchesFolder(),
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        await SetPatchesFolderAsync(dialog.FolderName);
        await PersistSettingsAsync();
        SetBanner(NotificationLevel.Success, "Patches folder updated.");
    }

    private void BtnOpenPatchesFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = GetCurrentPatchesFolder();
        if (!Directory.Exists(folder))
        {
            _dialogs.ShowWarning("Patches folder does not exist.", "Open Folder");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private async void BtnResetPatchesFolder_Click(object sender, RoutedEventArgs e)
    {
        var writableFolder = _patchStorageService.GetDefaultUserPatchesFolder();
        await _patchStorageService.EnsureSeededAsync(writableFolder, _defaultPatchesFolder);
        await SetPatchesFolderAsync(writableFolder);
        await PersistSettingsAsync();
        SetBanner(NotificationLevel.Success, "Patches folder reset to local writable default.");
    }

    private async void BtnImportPatchPack_Click(object sender, RoutedEventArgs e)
    {
        if (_patchImportCoordinator == null)
            return;

        var dialog = new OpenFileDialog
        {
            Filter = "Patch pack (*.zip)|*.zip|All files (*.*)|*.*",
            Title = "Select patch pack"
        };

        if (dialog.ShowDialog() != true)
            return;

        btnImportPatchPack.IsEnabled = false;
        try
        {
            var result = await _patchImportCoordinator.ImportAsync(new PatchImportRequest
            {
                ZipPath = dialog.FileName,
                TargetPatchesFolder = GetCurrentPatchesFolder()
            });

            txtLastImportedPack.Text = result.PackLabel;
            _settings.LastImportedPatchPack = result.PackLabel;

            await SetPatchesFolderAsync(GetCurrentPatchesFolder());
            await PersistSettingsAsync();

            SetBanner(NotificationLevel.Success, $"Patch pack imported. Backup: {result.BackupFolder}");
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, "Import Failed");
            SetBanner(NotificationLevel.Error, "Patch pack import failed.");
        }
        finally
        {
            btnImportPatchPack.IsEnabled = true;
            RefreshRunSummary();
        }
    }

    private void BtnRetryFromValidation_Click(object sender, RoutedEventArgs e)
    {
        var validation = ValidateCurrentRequest(requirePassword: true);
        if (validation.IsValid)
        {
            ClearValidationIssues();
            SetBanner(NotificationLevel.Success, "Validation passed. Ready to start patch.");
        }
        else
        {
            SetValidationIssues(validation.Issues);
            SetBanner(NotificationLevel.Error, "Validation failed. Fix highlighted items.");
        }
    }

    private void BtnCopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var text = BuildDiagnosticsText();
        Clipboard.SetText(text);
        SetBanner(NotificationLevel.Info, "Diagnostics copied to clipboard.");
    }

    private void BtnOpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath))
        {
            _dialogs.ShowInfo("No output file available yet.", "Open Output");
            return;
        }

        if (File.Exists(_lastOutputPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_lastOutputPath}\"",
                UseShellExecute = true
            });
            return;
        }

        var folder = Path.GetDirectoryName(_lastOutputPath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
            return;
        }

        _dialogs.ShowWarning("Output folder is not available.", "Open Output");
    }

    private void BtnCopyPatchPlan_Click(object sender, RoutedEventArgs e)
    {
        var text = BuildPatchPlanText();
        Clipboard.SetText(text);
        SetBanner(NotificationLevel.Info, "Patch plan copied to clipboard.");
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation == null)
            return;

        _runCancellation.Cancel();
        SetBanner(NotificationLevel.Info, "Cancelling patch run...");
    }

    private async void BtnPatch_Click(object sender, RoutedEventArgs e)
    {
        if (_runCoordinator == null)
            return;

        var validation = ValidateCurrentRequest(requirePassword: true);
        if (!validation.IsValid)
        {
            SetValidationIssues(validation.Issues);
            SetBanner(NotificationLevel.Error, "Validation failed. Fix highlighted items.");
            FocusFirstIncompleteStep();
            return;
        }

        ClearValidationIssues();

        if (!string.IsNullOrWhiteSpace(validation.Request.SourceBakPath) && !File.Exists(validation.Request.SourceBakPath))
        {
            _dialogs.ShowWarning("Source backup file does not exist.", "Validation");
            SetBanner(NotificationLevel.Error, "Source backup file does not exist.");
            FocusFirstIncompleteStep();
            return;
        }

        var planPreview = BuildPatchPlanText();
        var decision = MessageBox.Show(
            $"{planPreview}\n\nStart patch now?",
            "Confirm Patch Run",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (decision != MessageBoxResult.OK)
            return;

        RememberRecentBackup(validation.Request.SourceBakPath);
        _lastOutputPath = validation.Request.OutputBakPath;

        _logBuilder.Clear();
        _pendingLogLines.Clear();
        _logFlushTimer.Stop();
        txtLog.Text = "";
        _runWarnings.Clear();
        UpdateWarningChip(0);
        expRunWarnings.IsExpanded = false;

        txtStatus.Text = "Starting...";
        txtResultSummary.Text = "";
        progressBar.Value = 0;
        btnOpenOutputFolder.IsEnabled = false;
        btnCopyDiagnostics.IsEnabled = false;

        ApplyRunUiLock(isRunning: true);
        SetBanner(NotificationLevel.Info, "Patch run started.");

        _runCancellation = new CancellationTokenSource();

        try
        {
            var progress = new Progress<PatchRunProgress>(p =>
            {
                progressBar.Value = p.Percent;
                txtStatus.Text = p.Message;
            });

            var logProgress = new Progress<string>(AppendLogLine);
            var result = await _runCoordinator.RunAsync(validation.Request, progress, logProgress, _runCancellation.Token);

            txtResultSummary.Text = result.Summary;
            _lastOutputPath = result.OutputPath;

            _runWarnings.Clear();
            foreach (var warning in result.Warnings)
            {
                _runWarnings.Add(
                    $"SQL {warning.ErrorNumber} in {warning.ScriptName} (batch {warning.BatchIndex}/{warning.BatchCount}): {warning.ErrorMessage}");
            }

            UpdateWarningChip(result.WarningCount);

            if (result.Success)
            {
                txtStatus.Text = "Completed";
                progressBar.Value = 100;
                btnOpenOutputFolder.IsEnabled = !string.IsNullOrWhiteSpace(result.OutputPath);
                btnCopyDiagnostics.IsEnabled = result.WarningCount > 0 || _logBuilder.Length > 0;
                if (result.WarningThresholdExceeded)
                {
                    SetBanner(
                        NotificationLevel.Info,
                        $"Patch completed with warnings above threshold ({result.WarningCount}/{result.WarningThreshold}).",
                        warningBanner: true);
                }
                else
                {
                    SetBanner(result.WarningCount > 0 ? NotificationLevel.Info : NotificationLevel.Success,
                        result.WarningCount > 0 ? "Patch completed with warnings." : "Patch completed successfully.",
                        warningBanner: result.WarningCount > 0);
                }
            }
            else if (result.Cancelled)
            {
                txtStatus.Text = "Cancelled";
                btnCopyDiagnostics.IsEnabled = _logBuilder.Length > 0 || result.WarningCount > 0;
                SetBanner(NotificationLevel.Info, "Patch run cancelled.");
            }
            else
            {
                txtStatus.Text = "Failed";
                btnCopyDiagnostics.IsEnabled = true;
                SetBanner(NotificationLevel.Error, "Patch failed. Review diagnostics.");
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Failed";
            txtResultSummary.Text = ex.Message;
            btnCopyDiagnostics.IsEnabled = true;
            SetBanner(NotificationLevel.Error, "Patch failed unexpectedly.");
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            ApplyRunUiLock(isRunning: false);
            RefreshRunSummary();
            await PersistSettingsAsync();
        }
    }
}
