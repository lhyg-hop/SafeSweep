using SafeSweep.Models;

namespace SafeSweep.Services.Actions;

public interface IActionPlanner
{
    Task<ExecutionPlan> BuildPlanAsync(
        IReadOnlyList<ScanFinding> findings,
        bool preferImmediateFreeSpace,
        CancellationToken cancellationToken = default);
}
