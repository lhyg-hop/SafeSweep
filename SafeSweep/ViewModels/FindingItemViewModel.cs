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
        RiskTier.L0ReadOnly => "L0 只读说明",
        RiskTier.L1SafeCleanup => "L1 安全清理",
        RiskTier.L2Recoverable => "L2 可恢复处理",
        RiskTier.L3Guided => "L3 引导处理",
        _ => Finding.RiskTier.ToString()
    };

    public string CategoryLabel => Finding.Category switch
    {
        "Downloads" => "下载目录",
        "Desktop" => "桌面",
        "Videos" => "视频",
        "Documents" => "文档",
        "UserTemp" => "用户临时文件",
        "SystemTemp" => "系统临时文件",
        "ThumbnailCache" => "缩略图缓存",
        "BrowserCache" => "浏览器缓存",
        "RecycleBin" => "回收站",
        "WinSxS" => "WinSxS 组件存储",
        "WindowsUpdateDownload" => "Windows 更新下载",
        "HibernateFile" => "休眠文件",
        "PageFile" => "分页文件",
        "RestorePoints" => "系统还原点",
        "DiskImage" => "磁盘镜像",
        "Archive" => "压缩包",
        "Installer" => "安装包",
        "VirtualDisk" => "虚拟磁盘",
        "Video" => "视频文件",
        "Database" => "数据库文件",
        "LargeFile" => "大文件",
        _ => Finding.Category
    };

    public string SizeText => ByteSizeFormatter.Format(Finding.Size);
    public string ModifiedText => Finding.ModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string ConfidenceText => $"{Finding.Confidence:P0}";
    public string ActionText => Finding.SuggestedAction switch
    {
        SuggestedActionKind.SafeCleanup => "安全处理",
        SuggestedActionKind.Review => "建议确认",
        SuggestedActionKind.MoveToOtherDrive => "移出系统盘",
        SuggestedActionKind.UseOfficialTool => "官方处理",
        SuggestedActionKind.Uninstall => "正规卸载",
        _ => "仅查看"
    };
}
