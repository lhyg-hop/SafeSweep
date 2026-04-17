using SafeSweep.Models;

namespace SafeSweep.Services.Actions;

public interface IActionExecutor
{
    Task<IReadOnlyList<ActionRecord>> ExecutePlanAsync(ExecutionPlan plan, CancellationToken cancellationToken = default);
    Task<ActionRecord> RestoreAsync(ActionRecord record, ConflictPolicy conflictPolicy, CancellationToken cancellationToken = default);
}
