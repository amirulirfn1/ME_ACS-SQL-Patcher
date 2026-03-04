using MagDbPatcher.ViewModels;

namespace MagDbPatcher.Infrastructure;

public enum RunStepState
{
    Pending,
    Ready,
    Done,
    NeedsAttention
}

public enum SourceFileHintKind
{
    None,
    Success,
    Error
}

public sealed record StepGuidanceState(
    RunStepState Step1State,
    RunStepState Step2State,
    RunStepState Step3State,
    RunStepState Step4State,
    bool RunReady,
    string NextActionText,
    string PatchHintText)
{
    public static StepGuidanceState Initial { get; } = new(
        RunStepState.Pending,
        RunStepState.Pending,
        RunStepState.Pending,
        RunStepState.Pending,
        false,
        "Next: Select a source backup file.",
        "Complete Step 1 to continue.");
}

public sealed record RunSummaryState(
    string SourceFileHint,
    SourceFileHintKind SourceFileHintKind,
    string VersionsText,
    string TempFolderText,
    string OutputText,
    string PlanText);

public sealed record NotificationState(
    NotificationLevel Level,
    string Message,
    bool WarningBanner = false);
