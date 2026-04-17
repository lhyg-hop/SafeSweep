using SafeSweep.Models;
using SafeSweep.Utils;

namespace SafeSweep.ViewModels;

public sealed class FindingItemViewModel : ObservableObject
{
    private bool _isSelected;

    public FindingItemViewModel(ScanFinding finding)
    {
        Finding = finding;
        _isSelected = finding.RiskTier is RiskTier.L1SafeCleanup or RiskTier.L2Recoverable;
    }

    public ScanFinding Finding { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string TierLabel => Finding.RiskTier switch
    {
        RiskTier.L0ReadOnly => "L0 Read Only",
        RiskTier.L1SafeCleanup => "L1 Safe Cleanup",
        RiskTier.L2Recoverable => "L2 Recoverable",
        RiskTier.L3Guided => "L3 Guided",
        _ => Finding.RiskTier.ToString()
    };

    public string SizeText => ByteSizeFormatter.Format(Finding.Size);
    public string ModifiedText => Finding.ModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string ConfidenceText => $"{Finding.Confidence:P0}";
    public string ActionText => Finding.SuggestedAction switch
    {
        SuggestedActionKind.SafeCleanup => "Safe Cleanup",
        SuggestedActionKind.Review => "Review",
        SuggestedActionKind.MoveToOtherDrive => "Move Off C",
        SuggestedActionKind.UseOfficialTool => "Official Tool",
        SuggestedActionKind.Uninstall => "Uninstall",
        _ => "View Only"
    };
}
