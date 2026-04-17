using SafeSweep.Models;
using SafeSweep.Services.Storage;
using SafeSweep.Utils;

namespace SafeSweep.Services.Policy;

public sealed class DefaultRuleEvaluator : IRuleEvaluator
{
    private readonly IAuditRepository _repository;
    private readonly IReadOnlyList<RuleDefinition> _rules;

    public DefaultRuleEvaluator(IAuditRepository repository)
    {
        _repository = repository;
        _rules = CreateRules();
    }

    public async Task<ScanFinding?> EvaluateAsync(ScanObservation observation, CancellationToken cancellationToken = default)
    {
        var ignored = await _repository.GetIgnoredPathsAsync(cancellationToken);
        if (ignored.Contains(observation.Target.Path, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var rule = MatchRule(observation);
        if (rule is null)
        {
            return null;
        }

        var requiresElevation =
            observation.Target.IsProtected &&
            string.Equals(observation.Source, "SafeCleanup", StringComparison.OrdinalIgnoreCase);

        var explanation = BuildExplanation(observation, rule);

        return new ScanFinding(
            Guid.NewGuid().ToString("N"),
            observation.Target.Path,
            observation.CategoryHint,
            observation.Source,
            observation.Size,
            observation.ModifiedAt,
            rule.RiskTier,
            GetConfidence(observation, rule),
            rule.RuleId,
            GetSuggestedAction(observation, rule),
            rule.Recoverability,
            rule.ActionPolicy,
            observation.IsLocked,
            observation.Target.IsProtected,
            observation.Target.IsReparsePoint,
            observation.Target.CloudState,
            requiresElevation,
            explanation.WhatItIs,
            explanation.WhyConsumesSpace,
            explanation.WhySuggested,
            explanation.Impact,
            explanation.RestoreMethod,
            explanation.FailurePrediction);
    }

    public IReadOnlyList<RuleDefinition> GetRules() => _rules;

    private RuleDefinition? MatchRule(ScanObservation observation)
    {
        if (observation.Target.IsReparsePoint || observation.Target.CloudState == CloudFileState.Placeholder)
        {
            return new RuleDefinition(
                "blocked-indirect-target",
                "Safety",
                ["reparse-point", "cloud-placeholder"],
                RiskTier.L3Guided,
                Recoverability.None,
                ActionPolicy.None,
                "Indirect or cloud-managed targets are never processed automatically.",
                true);
        }

        return _rules.FirstOrDefault(rule =>
            rule.Enabled &&
            rule.Matchers.Any(matcher =>
                observation.CategoryHint.Contains(matcher, StringComparison.OrdinalIgnoreCase) ||
                observation.Source.Contains(matcher, StringComparison.OrdinalIgnoreCase) ||
                observation.Target.Path.Contains(matcher, StringComparison.OrdinalIgnoreCase)));
    }

    private static double GetConfidence(ScanObservation observation, RuleDefinition rule)
    {
        var confidence = 0.6;
        confidence += observation.Source switch
        {
            "SafeCleanup" => 0.25,
            "SystemGuidance" => 0.2,
            "UserFiles" => 0.1,
            _ => 0.0
        };

        if (rule.RiskTier == RiskTier.L2Recoverable)
        {
            confidence -= 0.1;
        }

        if (observation.IsLocked)
        {
            confidence -= 0.15;
        }

        return Math.Clamp(confidence, 0.1, 0.99);
    }

    private static SuggestedActionKind GetSuggestedAction(ScanObservation observation, RuleDefinition rule)
    {
        if (rule.ActionPolicy == ActionPolicy.OfficialTool)
        {
            return SuggestedActionKind.UseOfficialTool;
        }

        if (rule.ActionPolicy == ActionPolicy.Uninstall)
        {
            return SuggestedActionKind.Uninstall;
        }

        return rule.RiskTier switch
        {
            RiskTier.L1SafeCleanup => SuggestedActionKind.SafeCleanup,
            RiskTier.L2Recoverable when observation.Target.TargetType == TargetType.File => SuggestedActionKind.MoveToOtherDrive,
            RiskTier.L2Recoverable => SuggestedActionKind.Review,
            RiskTier.L3Guided => SuggestedActionKind.UseOfficialTool,
            _ => SuggestedActionKind.None
        };
    }

    private static (
        string WhatItIs,
        string WhyConsumesSpace,
        string WhySuggested,
        string Impact,
        string RestoreMethod,
        string FailurePrediction) BuildExplanation(ScanObservation observation, RuleDefinition rule)
    {
        var sizeText = ByteSizeFormatter.Format(observation.Size);

        if (rule.RiskTier == RiskTier.L0ReadOnly)
        {
            return (
                $"This is a system-managed storage area currently using about {sizeText}.",
                "Windows may expand it as updates, hibernation, restore points, or virtual memory accumulate.",
                "SafeSweep only explains this item and routes you to an official system entry point.",
                "Official handling may reduce rollback options or change startup, update, or restore behavior.",
                "SafeSweep does not directly restore this class of item. Recovery depends on Windows itself.",
                "Official cleanup can still fail when elevation is missing or the system is actively using the storage.");
        }

        if (rule.RiskTier == RiskTier.L1SafeCleanup)
        {
            return (
                $"This is a known temporary or rebuildable location currently using about {sizeText}.",
                "Applications and Windows often recreate these files automatically, but they can grow for a long time if left alone.",
                "The path matched SafeSweep allow-list rules for deterministic cleanup.",
                "After cleanup, the related app may rebuild caches and feel slightly slower on first launch.",
                "This action keeps audit records, but it does not promise full file-by-file restore.",
                observation.IsLocked
                    ? "Some files appear locked, so execution may skip a subset of this location."
                    : "Execution may still partially fail if permissions change or files are suddenly in use.");
        }

        if (rule.RiskTier == RiskTier.L2Recoverable)
        {
            return (
                $"This is a large user-controlled item currently using about {sizeText}.",
                "It is not usually required by Windows itself, but it may still matter to the user or a specific app workflow.",
                "SafeSweep suggests review first and prefers a recoverable flow instead of silent permanent deletion.",
                "Handling it may affect offline installers, project indexes, chat attachments, or local media availability.",
                "SafeSweep prefers moving it to a non-system-drive quarantine root when one is available.",
                observation.IsLocked
                    ? "The item seems to be in use, so the app related to it may need to be closed first."
                    : "Quarantine or restore can still fail when the target volume lacks space or a path conflict appears.");
        }

        return (
            "This item should be handled by an official tool or a manual review step.",
            "Direct deletion is not appropriate for this category.",
            "SafeSweep only explains and routes the user, instead of touching the data directly.",
            "Improper handling may impact application behavior, services, or data integrity.",
            "SafeSweep does not provide direct restore for this item.",
            "Automatic handling must stay blocked when the target is protected, indirect, or cloud-managed.");
    }

    private static IReadOnlyList<RuleDefinition> CreateRules()
    {
        return
        [
            new RuleDefinition(
                "system-guidance",
                "SystemGuidance",
                ["WinSxS", "WindowsUpdate", "RestorePoints", "HibernateFile", "PageFile", "SystemGuidance"],
                RiskTier.L0ReadOnly,
                Recoverability.None,
                ActionPolicy.OfficialTool,
                "System-managed storage should only be handled via official tools.",
                true),
            new RuleDefinition(
                "safe-temp",
                "SafeCleanup",
                ["UserTemp", "SystemTemp", "ThumbnailCache", "BrowserCache", "RecycleBin"],
                RiskTier.L1SafeCleanup,
                Recoverability.AuditOnly,
                ActionPolicy.DirectDelete,
                "Known temporary or rebuildable cache path.",
                true),
            new RuleDefinition(
                "user-hotspot",
                "UserFiles",
                ["Downloads", "Desktop", "Videos", "Documents", "LargeFile", "Archive", "Installer", "DiskImage", "VirtualDisk", "Database"],
                RiskTier.L2Recoverable,
                Recoverability.Quarantine,
                ActionPolicy.Quarantine,
                "Large user-controlled content should be reviewed and made recoverable first.",
                true),
            new RuleDefinition(
                "guided-only",
                "Guided",
                ["Program Files", "ProgramData", "Windows"],
                RiskTier.L3Guided,
                Recoverability.None,
                ActionPolicy.OfficialTool,
                "Protected paths must be guided only.",
                true)
        ];
    }
}
