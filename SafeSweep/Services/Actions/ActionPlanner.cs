using SafeSweep.Models;

namespace SafeSweep.Services.Actions;

public sealed class ActionPlanner : IActionPlanner
{
    private readonly IQuarantineStore _quarantineStore;

    public ActionPlanner(IQuarantineStore quarantineStore)
    {
        _quarantineStore = quarantineStore;
    }

    public Task<ExecutionPlan> BuildPlanAsync(
        IReadOnlyList<ScanFinding> findings,
        bool preferImmediateFreeSpace,
        CancellationToken cancellationToken = default)
    {
        var items = new List<ExecutionPlanItem>();
        var predictedFailures = new List<PredictedFailure>();

        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var warnings = new List<string>();
            var actionPolicy = finding.ActionPolicy;
            var rollback = finding.Recoverability;
            var canFreeSystemDrive = true;
            var status = PlanItemStatus.Ready;

            if (finding.RiskTier == RiskTier.L0ReadOnly || actionPolicy == ActionPolicy.None)
            {
                items.Add(new ExecutionPlanItem(
                    finding.Id,
                    finding.Path,
                    finding.Category,
                    ActionPolicy.None,
                    Recoverability.None,
                    0,
                    false,
                    finding.RequiresElevation,
                    PlanItemStatus.Blocked,
                    ["This item can only be handled by an official tool or manual action."]));
                continue;
            }

            if (finding.IsReparsePoint || finding.CloudState == CloudFileState.Placeholder)
            {
                predictedFailures.Add(new PredictedFailure(finding.Path, "Blocked reparse point or cloud placeholder."));
                items.Add(new ExecutionPlanItem(
                    finding.Id,
                    finding.Path,
                    finding.Category,
                    ActionPolicy.None,
                    Recoverability.None,
                    0,
                    false,
                    finding.RequiresElevation,
                    PlanItemStatus.Blocked,
                    ["The target is a symbolic link, mount point, or cloud placeholder, so it is blocked."]));
                continue;
            }

            if (finding.IsLocked)
            {
                warnings.Add("The item appears to be in use and may be skipped during execution.");
                status = PlanItemStatus.Warning;
                predictedFailures.Add(new PredictedFailure(finding.Path, "File is locked by another process."));
            }

            if (finding.ActionPolicy == ActionPolicy.Quarantine && !_quarantineStore.HasNonSystemDriveQuarantine(finding.Path))
            {
                warnings.Add("No non-system-drive quarantine root is available.");
                canFreeSystemDrive = false;

                if (preferImmediateFreeSpace)
                {
                    actionPolicy = ActionPolicy.DirectDelete;
                    rollback = Recoverability.AuditOnly;
                    warnings.Add("Plan switched to direct cleanup to prioritize actual free space.");
                }
                else
                {
                    warnings.Add("Plan keeps rollback ability, but may not really free C drive space.");
                }

                status = PlanItemStatus.Warning;
            }

            if (finding.IsProtected && !string.Equals(finding.Source, "SafeCleanup", StringComparison.Ordinal))
            {
                warnings.Add("Protected paths are only allowed through explicit allow-list rules.");
                predictedFailures.Add(new PredictedFailure(finding.Path, "Protected path is not eligible for direct handling."));
                items.Add(new ExecutionPlanItem(
                    finding.Id,
                    finding.Path,
                    finding.Category,
                    ActionPolicy.None,
                    Recoverability.None,
                    0,
                    false,
                    finding.RequiresElevation,
                    PlanItemStatus.Blocked,
                    warnings));
                continue;
            }

            var estimatedFreed = canFreeSystemDrive ? finding.Size : 0;
            items.Add(new ExecutionPlanItem(
                finding.Id,
                finding.Path,
                finding.Category,
                actionPolicy,
                rollback,
                estimatedFreed,
                canFreeSystemDrive,
                finding.RequiresElevation,
                status,
                warnings));
        }

        var executableItems = items.Where(static item => item.Status != PlanItemStatus.Blocked).ToList();
        var plan = new ExecutionPlan(
            Guid.NewGuid().ToString("N"),
            items,
            executableItems.Sum(static item => item.EstimatedFreedBytes),
            executableItems.Select(static item => item.RollbackMode).DefaultIfEmpty(Recoverability.None).Max(),
            executableItems.Any(static item => item.RequiresElevation),
            predictedFailures);

        return Task.FromResult(plan);
    }
}
