using System.Diagnostics;
using System.IO;
using SafeSweep.Models;

namespace SafeSweep.Services.SystemIntegration;

public sealed class WindowsSystemAdapter : ISystemAdapter
{
    public IReadOnlyList<SystemGuidanceItem> GetGuidanceItems()
    {
        return
        [
            new SystemGuidanceItem("storage-sense", "存储感知", "打开 Windows 的存储清理建议和临时文件设置。", "打开设置", "ms-settings:storagesense"),
            new SystemGuidanceItem("apps-features", "应用和功能", "通过系统官方入口卸载占空间较大的软件。", "打开设置", "ms-settings:appsfeatures"),
            new SystemGuidanceItem("windows-update", "Windows 更新", "检查更新下载、清理相关设置和系统更新状态。", "打开设置", "ms-settings:windowsupdate"),
            new SystemGuidanceItem("disk-cleanup", "磁盘清理", "打开 Windows 经典磁盘清理工具。", "运行工具", "cleanmgr.exe"),
            new SystemGuidanceItem("system-protection", "系统保护", "查看系统还原点占用和系统保护设置。", "打开面板", "SystemPropertiesProtection.exe")
        ];
    }

    public Task OpenGuidanceAsync(SystemGuidanceItem item, CancellationToken cancellationToken = default)
    {
        Launch(item.LaunchTarget);
        return Task.CompletedTask;
    }

    public Task OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        if (File.Exists(path))
        {
            Launch("explorer.exe", $"/select,\"{path}\"");
            return Task.CompletedTask;
        }

        if (Directory.Exists(path))
        {
            Launch("explorer.exe", $"\"{path}\"");
        }

        return Task.CompletedTask;
    }

    private static void Launch(string fileName, string? arguments = null)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true
        });
    }
}
