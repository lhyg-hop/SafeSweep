namespace SafeSweep.Models;

public sealed record ScanTarget(
    string Path,
    TargetType TargetType,
    string VolumeId,
    bool IsProtected,
    bool IsReparsePoint,
    CloudFileState CloudState);

public sealed record ScanObservation(
    ScanTarget Target,
    long Size,
    DateTimeOffset? ModifiedAt,
    bool IsLocked,
    string Source,
    bool IsDirectory,
    string CategoryHint,
    string? DetailHint = null);

public sealed record ScanFinding(
    string Id,
    string Path,
    string Category,
    string Source,
    long Size,
    DateTimeOffset? ModifiedAt,
    RiskTier RiskTier,
    double Confidence,
    string ExplanationKey,
    SuggestedActionKind SuggestedAction,
    Recoverability Recoverability,
    ActionPolicy ActionPolicy,
    bool IsLocked,
    bool IsProtected,
    bool IsReparsePoint,
    CloudFileState CloudState,
    bool RequiresElevation,
    string WhatItIs,
    string WhyItConsumesSpace,
    string WhySuggested,
    string Impact,
    string RestoreMethod,
    string FailurePrediction);

public sealed record RuleDefinition(
    string RuleId,
    string Scope,
    IReadOnlyList<string> Matchers,
    RiskTier RiskTier,
    Recoverability Recoverability,
    ActionPolicy ActionPolicy,
    string ExplanationTemplate,
    bool Enabled);

public sealed record PredictedFailure(
    string Path,
    string Reason);

public sealed record ExecutionPlanItem(
    string FindingId,
    string Path,
    string Category,
    ActionPolicy ActionPolicy,
    Recoverability RollbackMode,
    long EstimatedFreedBytes,
    bool CanActuallyFreeSystemDrive,
    bool RequiresElevation,
    PlanItemStatus Status,
    IReadOnlyList<string> Warnings);

public sealed record ExecutionPlan(
    string PlanId,
    IReadOnlyList<ExecutionPlanItem> Items,
    long EstimatedFreedBytes,
    Recoverability RollbackMode,
    bool RequiresElevation,
    IReadOnlyList<PredictedFailure> PredictedFailures);

public sealed record ActionRecord(
    string ActionId,
    string PlanId,
    string Path,
    ActionPolicy OperationType,
    string? OriginalPath,
    string? QuarantinePath,
    long BytesFreed,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    ActionRecordStatus Status,
    string? ErrorCode,
    bool CanRestore,
    string Summary);

public sealed record RestoreRecord(
    string ActionId,
    ActionRecordStatus RestoreStatus,
    ConflictPolicy ConflictPolicy,
    DateTimeOffset RestoredAt);

public sealed record InstalledAppRecord(
    string DisplayName,
    string Publisher,
    string? InstallLocation,
    long EstimatedSizeBytes,
    string? UninstallCommand,
    string Source);

public sealed record DuplicateFileEntry(
    string Path,
    long Size,
    DateTimeOffset? ModifiedAt);

public sealed record DuplicateGroup(
    string GroupId,
    string HashState,
    IReadOnlyList<DuplicateFileEntry> Files,
    string SuggestedKeepPolicy);

public sealed record SystemGuidanceItem(
    string Id,
    string Title,
    string Description,
    string ActionLabel,
    string LaunchTarget);

public sealed record ScanSnapshotSummary(
    string Source,
    long CurrentBytes,
    long PreviousBytes)
{
    public long DeltaBytes => CurrentBytes - PreviousBytes;
}
