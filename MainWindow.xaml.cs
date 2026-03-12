using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
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
    private readonly AppRuntimePaths _appPaths;
    private readonly AppSettingsService _settingsService;
    private readonly PatchStorageService _patchStorageService;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly RunRequestBuilder _requestBuilder;
    private readonly SettingsBinder _settingsBinder;
    private readonly RunSummaryComposer _runSummaryComposer = new();
    private readonly RunUiStateController _runUiStateController = new();
    private readonly RunStateEvaluator _runStateEvaluator = new();
    private readonly SqlConnectionTestCoordinator _sqlConnectionTestCoordinator = new();
    private readonly RunExecutionPresenter _runExecutionPresenter = new();
    private readonly RunDiagnosticsCoordinator _runDiagnosticsCoordinator = new();
    private readonly SessionLog _sessionLog;
    private readonly IMainRunOrchestrator _runOrchestrator;
    private readonly ObservableCollection<string> _runWarnings = new();
    private readonly Queue<string> _pendingLogLines = new();
    private readonly Queue<string> _visibleLogLines = new();
    private readonly Queue<string> _retainedLogLines = new();
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private const int MaxVisibleLogLines = 400;
    private const int MaxRetainedLogLines = 4000;

    private AppSettings _settings = new();
    private VersionService? _versionService;
    private PatchPackImportCoordinator? _patchImportCoordinator;
    private CancellationTokenSource? _runCancellation;
    private AdminWindow? _adminWindow;
    private string _defaultPatchesFolder = "";
    private string _lastOutputPath = "";
    private bool _isLoadingUi;
    private bool _isUiInitialized;

    public MainWindow()
        : this(new MainRunOrchestrator(), AppRuntimePaths.CreateDefault())
    {
    }

    internal MainWindow(AppRuntimePaths appPaths)
        : this(new MainRunOrchestrator(), appPaths)
    {
    }

    internal MainWindow(IMainRunOrchestrator runOrchestrator, AppRuntimePaths? appPaths = null)
    {
        _appPaths = appPaths ?? AppRuntimePaths.CreateDefault();
        _settingsService = new AppSettingsService(_appPaths);
        _patchStorageService = new PatchStorageService(_appPaths);
        _requestBuilder = new RunRequestBuilder(_appPaths);
        _settingsBinder = new SettingsBinder(_appPaths);
        _sessionLog = new SessionLog(_appPaths);

        InitializeComponent();
        _runOrchestrator = runOrchestrator;

        DataContext = _viewModel;

        cmbSourcePath.ItemsSource = _viewModel.RecentBackupFiles;
        cmbFromVersion.ItemsSource = _viewModel.SourceVersions;
        cmbToVersion.ItemsSource = _viewModel.TargetVersions;
        lstValidationIssues.ItemsSource = _viewModel.ValidationIssues;
        lstRunWarnings.ItemsSource = _runWarnings;

        lstValidationIssues.DisplayMemberPath = nameof(ValidationIssue.Message);
        cmbSourcePath.LostFocus += (_, _) => RefreshRunSummary();
        cmbSqlServer.LostFocus += (_, _) => RefreshRunSummary();
        cmbSourcePath.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(EditableComboTextChanged));
        cmbSqlServer.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(EditableComboTextChanged));
        _logFlushTimer.Tick += (_, _) => FlushPendingLogLines();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        ApplyReadyState();
        _isUiInitialized = true;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoadingUi = true;
        try
        {
            _defaultPatchesFolder = _appPaths.PatchesFolder;
            _settings = await _settingsService.LoadAsync();
            var initialPatchesFolder = await _patchStorageService.ResolvePatchesFolderAsync(_settings, _defaultPatchesFolder);

            ApplySettingsToUi();
            _viewModel.StatusText = "Loading SQL Server suggestions...";
            await LoadSqlServerSuggestionsAsync();
            _viewModel.StatusText = "Loading patch library...";
            await SetPatchesFolderAsync(initialPatchesFolder, closeAdminWindow: false);
            _sessionLog.WriteLine($"[{DateTime.Now:HH:mm:ss}] App started in portable mode. Root: {_appPaths.RootDirectory}");
            RefreshRunSummary();
            await PersistSettingsAsync();
            FocusPrimaryInput();
            _viewModel.StatusText = "Ready";
        }
        catch (Exception ex)
        {
            _sessionLog.WriteLine($"[{DateTime.Now:HH:mm:ss}] Startup failed: {ex.Message}");
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
            if (!_dialogs.Confirm(
                    "A patch run is still in progress. Cancel it and close the app?",
                    "Close Application",
                    useYesNo: true))
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
        _viewModel.PatchVersionText = "";
        _viewModel.AppBuildText = AppMetadata.BuildLabel;
        _viewModel.StatusText = "Loading portable workspace...";
        _viewModel.LogText = "";
        _viewModel.SqlTestResultText = "";
        _viewModel.SourceFileHintText = "";
        _viewModel.UpgradePathText = "";
        _visibleLogLines.Clear();
        _retainedLogLines.Clear();
        _pendingLogLines.Clear();

        _viewModel.ProgressValue = 0;
        _viewModel.ClearValidation();
        _viewModel.NotificationMessage = "";
        _viewModel.NotificationLevel = NotificationLevel.None;
        _runWarnings.Clear();
        UpdateWarningChip(0);
        bdStatusBanner.Visibility = Visibility.Collapsed;
        ApplyRunExecutionState(_runExecutionPresenter.BuildInitialState());

        btnRetryFromValidation.IsEnabled = false;
        btnCopyDiagnostics.IsEnabled = false;
        btnOpenOutputFolder.IsEnabled = false;
        btnCancel.IsEnabled = false;

        Title = $"{AppMetadata.Title} {AppMetadata.DisplayVersion}";
        ApplyGuidanceState(StepGuidanceState.Initial);
    }

    private void ApplySettingsToUi()
    {
        var snapshot = _settingsBinder.BuildViewSnapshot(_settings);
        txtLastImportedPack.Text = string.IsNullOrWhiteSpace(snapshot.LastImportedPack)
            ? "Normal updates arrive as a replacement app folder. Use patch-pack import only for manual admin updates."
            : snapshot.LastImportedPack;

        _viewModel.RecentBackupFiles.Clear();
        foreach (var item in snapshot.RecentBackups)
            _viewModel.RecentBackupFiles.Add(item);

        if (_viewModel.RecentBackupFiles.Count > 0)
            cmbSourcePath.SelectedIndex = 0;

        cmbSqlServer.Text = snapshot.LastSqlServer;
        txtSqlUsername.Text = snapshot.SqlUsername;
        txtTempFolder.Text = snapshot.PatchTempFolder;
        txtWarningThreshold.Text = snapshot.WarningThreshold.ToString(CultureInfo.InvariantCulture);
        cmbErrorMode.SelectedValue = snapshot.PatchErrorMode;

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
        return _patchStorageService.GetDefaultPatchesFolder();
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
        _runOrchestrator.UpdateVersionService(_versionService);

        _patchImportCoordinator = new PatchPackImportCoordinator(new PatchPackService(_appPaths.BackupsDirectory));

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
            _viewModel.PatchVersionText = "";
            return;
        }

        var latest = _versionService.GetAllVersions()
            .OrderBy(v => v.Order)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();

        _viewModel.PatchVersionText = latest == null ? "Patch unknown" : $"Patch {latest.Id}";
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
            _viewModel.UpgradePathText = $"{string.Join("  →  ", segments)}    ({steps.Count} step{(steps.Count == 1 ? "" : "s")}, {totalScripts} script{(totalScripts == 1 ? "" : "s")})";
            txtUpgradePath.Foreground = (Brush)FindResource("TextPrimary");
        }
        catch (Exception ex)
        {
            _viewModel.UpgradePathText = $"No path available: {ex.Message}";
            txtUpgradePath.Foreground = (Brush)FindResource("Error");
        }

        bdUpgradePath.Visibility = Visibility.Visible;
    }

    private void RefreshRunSummary()
    {
        if (!_isUiInitialized ||
            txtRunSummarySource == null ||
            txtRunSummaryUpgradePath == null ||
            txtRunSummaryConnection == null ||
            txtRunSummaryOutput == null ||
            txtRunSummarySafeguards == null ||
            txtRunSummaryPlan == null ||
            cmbSourcePath == null ||
            cmbFromVersion == null ||
            cmbToVersion == null ||
            cmbSqlServer == null)
        {
            return;
        }

        SyncExecutionSettingsFromUi();
        ApplyRunSummaryState(BuildRunSummaryState());
        UpdateGuidanceUi();
    }

    private RunSummaryState BuildRunSummaryState()
    {
        var request = BuildRunRequest();
        return _runSummaryComposer.Compose(new RunSummaryInput(
            SourceBakPath: request.SourceBakPath,
            FromVersionId: request.FromVersionId,
            ToVersionId: request.ToVersionId,
            SqlServer: request.ConnectionSettings.Server,
            SqlAuthMode: request.ConnectionSettings.AuthMode,
            SqlConnectionTestPassed: _sqlConnectionTestCoordinator.MatchesLastSuccessfulSettings(request.ConnectionSettings),
            TempFolder: request.TempFolder,
            ErrorMode: request.ExecutionOptions.ErrorMode,
            WarningThreshold: request.ExecutionOptions.WarningThreshold,
            OutputBakPath: request.OutputBakPath,
            VersionService: _versionService));
    }

    private void ApplyRunSummaryState(RunSummaryState state)
    {
        _viewModel.SourceFileHintText = state.SourceFileHint;
        _viewModel.SourceFileHintKind = state.SourceFileHintKind;
        txtSourceFileHint.Foreground = state.SourceFileHintKind switch
        {
            SourceFileHintKind.Error => (Brush)FindResource("Error"),
            SourceFileHintKind.Success => (Brush)FindResource("Success"),
            _ => (Brush)FindResource("TextMuted")
        };

        _viewModel.RunSummarySource = state.SourceText;
        _viewModel.RunSummaryUpgradePath = state.UpgradePathText;
        _viewModel.RunSummaryConnection = state.ConnectionText;
        _viewModel.RunSummaryOutput = state.OutputText;
        _viewModel.RunSummaryPlan = state.PlanText;
        _viewModel.RunSummarySafeguards = state.SafeguardsText;
    }

    private void UpdateGuidanceUi()
    {
        var state = EvaluateGuidanceState();
        ApplyGuidanceState(state);
    }

    private StepGuidanceState EvaluateGuidanceState()
    {
        var sourcePath = (cmbSourcePath.Text ?? "").Trim();
        var fromVersion = GetSelectedVersionId(cmbFromVersion);
        var toVersion = GetSelectedVersionId(cmbToVersion);
        var settings = BuildSqlConnectionSettings();
        _sqlConnectionTestCoordinator.InvalidateIfSettingsChanged(SqlConnectionTestCoordinator.BuildSignature(settings));

        var sourceExists = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath);
        var sourcePathEntered = !string.IsNullOrWhiteSpace(sourcePath);
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

        var localSql = !string.IsNullOrWhiteSpace(settings.Server) && LocalSqlValidator.IsLocalServer(settings.Server);
        var hasSqlUser = settings.AuthMode != SqlAuthMode.SqlLogin || !string.IsNullOrWhiteSpace(settings.Username);
        var hasSqlPassword = settings.AuthMode != SqlAuthMode.SqlLogin || !string.IsNullOrWhiteSpace(settings.Password);

        var state = _runStateEvaluator.Evaluate(new RunStateInput(
            SourcePathEntered: sourcePathEntered,
            SourceExists: sourceExists,
            VersionSelectionPresent: versionSelectionPresent,
            VersionPathValid: versionPathValid,
            LocalSqlServer: localSql,
            HasSqlUser: hasSqlUser,
            HasSqlPassword: hasSqlPassword,
            SqlConnectionTestPassed: _sqlConnectionTestCoordinator.IsConnectionTestPassed,
            RunEngineReady: _runOrchestrator.IsReady));

        if (state.RunReady)
        {
            var validation = ValidateCurrentRequest(requirePassword: true);
            if (!validation.IsValid)
            {
                return state with
                {
                    Step4State = RunStepState.NeedsAttention,
                    RunReady = false,
                    PatchHintText = "Review validation issues before starting."
                };
            }
        }

        return state;
    }

    private void ApplyGuidanceState(StepGuidanceState state)
    {
        SetStepStatusChip(bdStep1Status, txtStep1Status, RunStateEvaluator.GetStepText(state.Step1State), state.Step1State);
        SetStepStatusChip(bdStep2Status, txtStep2Status, RunStateEvaluator.GetStepText(state.Step2State), state.Step2State);
        SetStepStatusChip(bdStep3Status, txtStep3Status, RunStateEvaluator.GetStepText(state.Step3State), state.Step3State);
        SetStepStatusChip(bdStep4Status, txtStep4Status, state.RunReady ? "Ready" : RunStateEvaluator.GetStepText(state.Step4State), state.Step4State);

        _viewModel.NextActionText = state.RunReady
            ? "Next: Review the run summary, then click Start Patch."
            : state.NextActionText;
        _viewModel.PatchActionHint = state.RunReady
            ? "Ready to run. Review the patch path, output, and safeguards, then click Start Patch."
            : state.PatchHintText;

        if (_runCancellation == null)
            btnPatch.IsEnabled = state.RunReady;
    }

    private void SetStepStatusChip(Border border, TextBlock textBlock, string text, RunStepState state)
    {
        textBlock.Text = text;

        switch (state)
        {
            case RunStepState.Done:
                border.Background = (Brush)FindResource("SuccessSoft");
                border.BorderBrush = (Brush)FindResource("Success");
                textBlock.Foreground = (Brush)FindResource("Success");
                break;
            case RunStepState.Ready:
                border.Background = (Brush)FindResource("InfoSoft");
                border.BorderBrush = (Brush)FindResource("Info");
                textBlock.Foreground = (Brush)FindResource("Info");
                break;
            case RunStepState.NeedsAttention:
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

    private void ApplyRunExecutionState(RunExecutionState state)
    {
        _viewModel.ProgressValue = state.ProgressValue;
        _viewModel.StatusText = state.StatusText;
        _viewModel.RunProgressDetailText = state.DetailText;
        _viewModel.ResultSummary = state.ResultSummary;
    }

    private void ApplyRunCompletionState(PatchRunResult result, RunCompletionState state)
    {
        _viewModel.StatusText = state.StatusText;
        _viewModel.RunProgressDetailText = state.DetailText;
        _viewModel.ResultSummary = state.ResultSummary;
        btnOpenOutputFolder.IsEnabled = state.EnableOpenOutputFolder;
        btnCopyDiagnostics.IsEnabled = state.EnableCopyDiagnostics;
        expRunWarnings.IsExpanded = state.ExpandDiagnostics;
        UpdateWarningChip(state.WarningCount);
        SetBanner(state.Banner);
    }

    private void ApplySqlConnectionFeedback(SqlConnectionTestFeedback feedback)
    {
        _viewModel.SqlTestResultText = feedback.Message;
        txtSqlTestResult.Foreground = feedback.Tone switch
        {
            SqlTestMessageTone.Success => (Brush)FindResource("Success"),
            SqlTestMessageTone.Error => (Brush)FindResource("Error"),
            _ => (Brush)FindResource("TextMuted")
        };

        if (feedback.Banner != null)
            SetBanner(feedback.Banner);
    }

    private string BuildOutputBakPath(string sourceBakPath, string toVersionId)
        => _requestBuilder.BuildOutputBakPath(sourceBakPath, toVersionId);

    private void SyncExecutionSettingsFromUi()
    {
        _settings.PatchTempFolder = string.IsNullOrWhiteSpace(txtTempFolder.Text)
            ? _patchStorageService.GetDefaultTempFolder()
            : txtTempFolder.Text.Trim();
        _settings.WarningThreshold = ParseWarningThreshold();
        _settings.PatchErrorMode = GetSelectedPatchErrorMode();
    }

    private PatchErrorMode GetSelectedPatchErrorMode()
    {
        return cmbErrorMode.SelectedValue is PatchErrorMode mode
            ? mode
            : PatchErrorMode.WarnAndContinue;
    }

    private int ParseWarningThreshold()
    {
        return int.TryParse(txtWarningThreshold.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold) && threshold > 0
            ? threshold
            : 10;
    }

    private void UpdateAuthModeUi()
    {
        if (pnlSqlLogin == null || rbAuthSql == null)
            return;

        pnlSqlLogin.Visibility = rbAuthSql.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditableComboTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        RefreshRunSummary();
    }

    private void AdvancedRunSettingTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        RefreshRunSummary();
    }

    private void AdvancedRunSettingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        RefreshRunSummary();
    }

    private void AdvancedRunSettingPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        RefreshRunSummary();
    }

    private void SetBanner(NotificationLevel level, string message, bool warningBanner = false)
        => SetBanner(new NotificationState(level, message, warningBanner));

    private void SetBanner(NotificationState state)
    {
        _viewModel.NotificationMessage = state.Message;
        _viewModel.NotificationLevel = state.Level;
        bdStatusBanner.Visibility = Visibility.Visible;

        if (state.WarningBanner)
        {
            bdStatusBanner.Style = (Style)FindResource("StatusBannerWarning");
            return;
        }

        switch (state.Level)
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
        var sqlLogin = rbAuthSql.IsChecked == true;
        return new SqlConnectionSettings
        {
            Server = (cmbSqlServer.Text ?? "").Trim(),
            AuthMode = sqlLogin ? SqlAuthMode.SqlLogin : SqlAuthMode.Windows,
            Username = (txtSqlUsername.Text ?? "").Trim(),
            Password = sqlLogin ? pwdSqlPassword.Password : null
        };
    }

    private PatchRunRequest BuildRunRequest()
    {
        SyncExecutionSettingsFromUi();
        var sourceBakPath = (cmbSourcePath.Text ?? "").Trim();
        var fromVersion = GetSelectedVersionId(cmbFromVersion) ?? "";
        var toVersion = GetSelectedVersionId(cmbToVersion) ?? "";
        var connectionSettings = BuildSqlConnectionSettings();
        return _requestBuilder.Build(sourceBakPath, fromVersion, toVersion, _settings, connectionSettings);
    }

    private (bool IsValid, PatchRunRequest Request, List<ValidationIssue> Issues) ValidateCurrentRequest(bool requirePassword)
    {
        var request = BuildRunRequest();
        var issues = _runOrchestrator.Validate(request, requirePassword).ToList();
        return (issues.Count == 0, request, issues);
    }

    private void AppendLogLine(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _pendingLogLines.Enqueue(stamped);
        _sessionLog.WriteLine(stamped);
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
            var line = _pendingLogLines.Dequeue();
            EnqueueWithCap(_retainedLogLines, line, MaxRetainedLogLines);
            EnqueueWithCap(_visibleLogLines, line, MaxVisibleLogLines);
        }

        _viewModel.LogText = string.Join(Environment.NewLine, _visibleLogLines);
        txtLog.ScrollToEnd();
    }

    private static void EnqueueWithCap(Queue<string> queue, string line, int maxCount)
    {
        queue.Enqueue(line);
        while (queue.Count > maxCount)
            queue.Dequeue();
    }

    private bool HasRetainedLogs() => _retainedLogLines.Count > 0;

    private void ClearLogBuffers()
    {
        _pendingLogLines.Clear();
        _visibleLogLines.Clear();
        _retainedLogLines.Clear();
        _viewModel.LogText = string.Empty;
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
        cmbSqlServer.IsEnabled = state.SourceSelectorEnabled;
        btnTestSql.IsEnabled = state.BrowseEnabled;
        rbAuthWindows.IsEnabled = state.SourceSelectorEnabled;
        rbAuthSql.IsEnabled = state.SourceSelectorEnabled;
        txtSqlUsername.IsEnabled = state.SourceSelectorEnabled;
        pwdSqlPassword.IsEnabled = state.SourceSelectorEnabled;
        txtTempFolder.IsEnabled = state.SourceSelectorEnabled;
        btnBrowseTempFolder.IsEnabled = state.BrowseEnabled;
        btnResetTempFolder.IsEnabled = state.BrowseEnabled;
        cmbErrorMode.IsEnabled = state.SourceSelectorEnabled;
        txtWarningThreshold.IsEnabled = state.SourceSelectorEnabled;
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
        SyncExecutionSettingsFromUi();
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
        return _runDiagnosticsCoordinator.BuildPatchPlan(_runOrchestrator, request);
    }

    private string BuildDiagnosticsText()
    {
        FlushPendingLogLines();
        return _runDiagnosticsCoordinator.BuildDiagnostics(
            _runOrchestrator,
            BuildRunRequest(),
            _viewModel.StatusText,
            _viewModel.ResultSummary,
            _runWarnings,
            _retainedLogLines,
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
        var writableFolder = _patchStorageService.GetDefaultPatchesFolder();
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
        if (!_runOrchestrator.IsReady)
            return;

        var settings = BuildSqlConnectionSettings();
        if (string.IsNullOrWhiteSpace(settings.Server))
        {
            ApplySqlConnectionFeedback(_sqlConnectionTestCoordinator.CreateMissingServerFeedback());
            return;
        }

        if (!LocalSqlValidator.IsLocalServer(settings.Server))
        {
            ApplySqlConnectionFeedback(_sqlConnectionTestCoordinator.CreateNonLocalServerFeedback());
            return;
        }

        btnTestSql.IsEnabled = false;
        ApplySqlConnectionFeedback(_sqlConnectionTestCoordinator.CreateTestingFeedback());

        try
        {
            var ok = await _runOrchestrator.TestConnectionAsync(settings);
            if (ok)
            {
                ApplySqlConnectionFeedback(_sqlConnectionTestCoordinator.RegisterSuccess(settings));
            }
            else
            {
                ApplySqlConnectionFeedback(_sqlConnectionTestCoordinator.RegisterFailure(
                    "Failed to connect.",
                    "SQL connection failed. Check server/auth settings."));
            }
        }
        catch (Exception ex)
        {
            ApplySqlConnectionFeedback(_sqlConnectionTestCoordinator.RegisterFailure(
                ex.Message,
                "SQL connection test failed."));
        }
        finally
        {
            btnTestSql.IsEnabled = true;
            RefreshRunSummary();
            UpdateGuidanceUi();
        }
    }

    private void BtnBrowseTempFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select temp folder",
            InitialDirectory = string.IsNullOrWhiteSpace(txtTempFolder.Text) ? Environment.CurrentDirectory : txtTempFolder.Text.Trim(),
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        txtTempFolder.Text = dialog.FolderName;
        RefreshRunSummary();
    }

    private void BtnResetTempFolder_Click(object sender, RoutedEventArgs e)
    {
        txtTempFolder.Text = _patchStorageService.GetDefaultTempFolder();
        RefreshRunSummary();
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
        var writableFolder = _patchStorageService.GetDefaultPatchesFolder();
        await _patchStorageService.EnsureSeededAsync(writableFolder, _defaultPatchesFolder);
        await SetPatchesFolderAsync(writableFolder);
        await PersistSettingsAsync();
        SetBanner(NotificationLevel.Success, "Patches folder reset to the portable app folder.");
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
            _sessionLog.WriteLine($"[{DateTime.Now:HH:mm:ss}] Importing patch pack: {dialog.FileName}");
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
            _sessionLog.WriteLine($"[{DateTime.Now:HH:mm:ss}] Patch pack imported successfully. Backup: {result.BackupFolder}");
        }
        catch (Exception ex)
        {
            _sessionLog.WriteLine($"[{DateTime.Now:HH:mm:ss}] Patch pack import failed: {ex.Message}");
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
        if (!_runOrchestrator.IsReady)
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
        if (!_dialogs.Confirm(
                $"{planPreview}\n\nStart patch now?",
                "Confirm Patch Run"))
            return;

        RememberRecentBackup(validation.Request.SourceBakPath);
        _lastOutputPath = validation.Request.OutputBakPath;

        ClearLogBuffers();
        _logFlushTimer.Stop();
        _runWarnings.Clear();
        UpdateWarningChip(0);
        expRunWarnings.IsExpanded = false;

        _viewModel.StatusText = "Starting...";
        btnOpenOutputFolder.IsEnabled = false;
        btnCopyDiagnostics.IsEnabled = false;
        ApplyRunExecutionState(_runExecutionPresenter.BuildStartingState(validation.Request));

        ApplyRunUiLock(isRunning: true);
        SetBanner(NotificationLevel.Info, "Patch run started.");

        _runCancellation = new CancellationTokenSource();

        try
        {
            var progress = new Progress<PatchRunProgress>(p =>
            {
                ApplyRunExecutionState(_runExecutionPresenter.BuildProgressState(p));
            });

            var logProgress = new Progress<string>(AppendLogLine);
            var result = await _runOrchestrator.RunAsync(validation.Request, progress, logProgress, _runCancellation.Token);

            _lastOutputPath = result.OutputPath;

            _runWarnings.Clear();
            foreach (var warning in result.Warnings)
            {
                _runWarnings.Add(
                    $"SQL {warning.ErrorNumber} in {warning.ScriptName} (batch {warning.BatchIndex}/{warning.BatchCount}): {warning.ErrorMessage}");
            }

            var completionState = _runExecutionPresenter.BuildCompletionState(result, HasRetainedLogs());
            ApplyRunCompletionState(result, completionState);
            if (result.Success)
                _viewModel.ProgressValue = 100;
        }
        catch (Exception ex)
        {
            ApplyRunCompletionState(
                new PatchRunResult { Summary = ex.Message },
                _runExecutionPresenter.BuildUnexpectedFailureState(ex));
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
