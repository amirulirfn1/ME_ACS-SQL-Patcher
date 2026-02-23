using System.Collections.ObjectModel;
using MagDbPatcher.Models;

namespace MagDbPatcher.ViewModels;

public sealed class MainViewModel : BindableBase
{
    private PatchFlowState _flowState = PatchFlowState.SelectSource;
    private string _sourceBackupPath = "";
    private VersionInfo? _fromVersion;
    private VersionInfo? _toVersion;
    private string _upgradePath = "";
    private string _statusText = "Ready";
    private int _progressValue;
    private string _logText = "";
    private string _notificationMessage = "";
    private NotificationLevel _notificationLevel;
    private string _diagnosticsText = "";
    private string _resultSummary = "";
    private bool _canRetry;

    public SqlConnectionViewModel Sql { get; } = new();
    public AdminToolsViewModel Admin { get; } = new();
    public ObservableCollection<string> RecentBackupFiles { get; } = new();
    public ObservableCollection<VersionInfo> SourceVersions { get; } = new();
    public ObservableCollection<VersionInfo> TargetVersions { get; } = new();
    public ObservableCollection<ValidationIssue> ValidationIssues { get; } = new();

    public PatchFlowState FlowState
    {
        get => _flowState;
        set => SetProperty(ref _flowState, value);
    }

    public string SourceBackupPath
    {
        get => _sourceBackupPath;
        set => SetProperty(ref _sourceBackupPath, value);
    }

    public VersionInfo? FromVersion
    {
        get => _fromVersion;
        set => SetProperty(ref _fromVersion, value);
    }

    public VersionInfo? ToVersion
    {
        get => _toVersion;
        set => SetProperty(ref _toVersion, value);
    }

    public string UpgradePath
    {
        get => _upgradePath;
        set => SetProperty(ref _upgradePath, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public int ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetProperty(ref _logText, value);
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        set => SetProperty(ref _notificationMessage, value);
    }

    public NotificationLevel NotificationLevel
    {
        get => _notificationLevel;
        set => SetProperty(ref _notificationLevel, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        set => SetProperty(ref _diagnosticsText, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        set => SetProperty(ref _resultSummary, value);
    }

    public bool CanRetry
    {
        get => _canRetry;
        set => SetProperty(ref _canRetry, value);
    }

    public void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogText += $"[{timestamp}] {message}\n";
    }

    public void ClearValidation() => ValidationIssues.Clear();

    public void SetValidationIssues(IEnumerable<ValidationIssue> issues)
    {
        ValidationIssues.Clear();
        foreach (var issue in issues)
            ValidationIssues.Add(issue);
    }
}
