namespace SafeSweep.Models;

public enum RiskTier
{
    L0ReadOnly = 0,
    L1SafeCleanup = 1,
    L2Recoverable = 2,
    L3Guided = 3
}

public enum TargetType
{
    File,
    Directory,
    Virtual,
    System
}

public enum CloudFileState
{
    Unknown,
    Local,
    Placeholder
}

public enum Recoverability
{
    None,
    AuditOnly,
    RecycleBin,
    Quarantine
}

public enum ActionPolicy
{
    None,
    DirectDelete,
    RecycleBin,
    Quarantine,
    OfficialTool,
    Uninstall
}

public enum SuggestedActionKind
{
    None,
    SafeCleanup,
    Review,
    MoveToOtherDrive,
    UseOfficialTool,
    Uninstall
}

public enum PlanItemStatus
{
    Ready,
    Warning,
    Blocked
}

public enum ActionRecordStatus
{
    Pending,
    Completed,
    Failed,
    Restored,
    Skipped
}

public enum ConflictPolicy
{
    Skip,
    Rename,
    Overwrite
}

public enum ScanMode
{
    Quick,
    Deep
}
