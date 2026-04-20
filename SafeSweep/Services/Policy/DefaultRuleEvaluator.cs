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
                "符号链接、挂载点或云占位文件默认不参与自动处理。",
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
                $"这是系统管理的空间占用项，当前约占 {sizeText}。",
                "它通常会随着 Windows 更新、休眠、还原点或虚拟内存的变化而增减。",
                "SafeSweep 只负责解释原因，并引导你使用系统官方入口处理。",
                "如果通过官方方式处理，可能影响回滚能力、系统启动速度或更新恢复能力。",
                "此类内容不由 SafeSweep 直接恢复，恢复能力依赖 Windows 自身机制。",
                "如果缺少管理员权限，或系统当前正在使用相关空间，官方处理也可能失败。");
        }

        if (rule.RiskTier == RiskTier.L1SafeCleanup)
        {
            return (
                $"这是已知的临时文件或可重建缓存，当前约占 {sizeText}。",
                "系统或应用通常会重新生成这类文件，但长期不清理时它们容易持续膨胀。",
                "该路径命中了 SafeSweep 的白名单规则，属于确定性较高的安全清理项。",
                "清理后，相关应用首次启动或访问时可能需要重建缓存，因此会稍慢一些。",
                "这类动作会保留审计记录，但不承诺逐文件恢复。",
                observation.IsLocked
                    ? "其中一部分文件当前可能被占用，执行时可能会被跳过。"
                    : "如果权限发生变化，或文件在执行时突然被占用，可能会出现部分失败。");
        }

        if (rule.RiskTier == RiskTier.L2Recoverable)
        {
            return (
                $"这是体积较大的用户控制内容，当前约占 {sizeText}。",
                "它通常不是 Windows 本身必须的内容，但仍可能影响你的安装包、素材、项目缓存或聊天附件。",
                "SafeSweep 会先建议人工确认，并优先采用可恢复流程，而不是直接永久删除。",
                "处理后，离线安装、工程索引、本地媒体或聊天附件可用性可能受到影响。",
                "如果有可用的非系统盘，SafeSweep 会优先把它移到隔离区，便于之后恢复。",
                observation.IsLocked
                    ? "该项当前似乎被某个程序占用，通常需要先关闭相关应用。"
                    : "如果目标磁盘空间不足，或恢复路径发生冲突，隔离和恢复仍可能失败。");
        }

        return (
            "这类内容更适合通过官方工具或人工确认后处理。",
            "对它直接删除或移动并不安全。",
            "SafeSweep 只负责解释、定位和引导，不会直接碰它。",
            "如果处理方式不当，可能影响应用运行、系统服务或数据完整性。",
            "SafeSweep 默认不会为这类内容提供直接恢复。",
            "只要路径属于受保护位置、间接路径或云占位对象，自动处理就必须保持阻止状态。");
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
                "系统级空间只允许通过官方方式处理。",
                true),
            new RuleDefinition(
                "safe-temp",
                "SafeCleanup",
                ["UserTemp", "SystemTemp", "ThumbnailCache", "BrowserCache", "RecycleBin"],
                RiskTier.L1SafeCleanup,
                Recoverability.AuditOnly,
                ActionPolicy.DirectDelete,
                "已知的临时文件或可重建缓存路径。",
                true),
            new RuleDefinition(
                "user-hotspot",
                "UserFiles",
                ["Downloads", "Desktop", "Videos", "Documents", "LargeFile", "Archive", "Installer", "DiskImage", "VirtualDisk", "Database"],
                RiskTier.L2Recoverable,
                Recoverability.Quarantine,
                ActionPolicy.Quarantine,
                "大体积用户内容需要先确认，并优先保留可恢复能力。",
                true),
            new RuleDefinition(
                "guided-only",
                "Guided",
                ["Program Files", "ProgramData", "Windows"],
                RiskTier.L3Guided,
                Recoverability.None,
                ActionPolicy.OfficialTool,
                "受保护路径只允许解释和引导。",
                true)
        ];
    }
}
