using SafeSweep.Models;

namespace SafeSweep.Services.Policy;

public interface IRuleEvaluator
{
    Task<ScanFinding?> EvaluateAsync(ScanObservation observation, CancellationToken cancellationToken = default);
    IReadOnlyList<RuleDefinition> GetRules();
}
