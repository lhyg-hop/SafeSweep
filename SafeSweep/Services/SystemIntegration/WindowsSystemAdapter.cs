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
            new SystemGuidanceItem("storage-sense", "Storage Sense", "Open Windows storage cleanup guidance and temporary file controls.", "Open Settings", "ms-settings:storagesense"),
            new SystemGuidanceItem("apps-features", "Apps & Features", "Use the official uninstall flow for large applications.", "Open Settings", "ms-settings:appsfeatures"),
            new SystemGuidanceItem("windows-update", "Windows Update", "Review update downloads and system cleanup-related settings.", "Open Settings", "ms-settings:windowsupdate"),
            new SystemGuidanceItem("disk-cleanup", "Disk Cleanup", "Open the classic Windows disk cleanup utility.", "Run Tool", "cleanmgr.exe"),
            new SystemGuidanceItem("system-protection", "System Protection", "Inspect restore point storage and protection settings.", "Open Panel", "SystemPropertiesProtection.exe")
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
