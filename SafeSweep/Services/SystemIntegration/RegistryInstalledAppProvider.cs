using System.Diagnostics;
using Microsoft.Win32;
using SafeSweep.Models;

namespace SafeSweep.Services.SystemIntegration;

public sealed class RegistryInstalledAppProvider : IInstalledAppProvider
{
    private static readonly string[] RegistryPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public Task<IReadOnlyList<InstalledAppRecord>> GetInstalledAppsAsync(CancellationToken cancellationToken = default)
    {
        var apps = new List<InstalledAppRecord>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var path in RegistryPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var uninstallKey = baseKey.OpenSubKey(path);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey is null)
                    {
                        continue;
                    }

                    var displayName = subKey.GetValue("DisplayName") as string;
                    var uninstallString = subKey.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(uninstallString))
                    {
                        continue;
                    }

                    apps.Add(new InstalledAppRecord(
                        displayName,
                        subKey.GetValue("Publisher") as string ?? "Unknown",
                        subKey.GetValue("InstallLocation") as string,
                        ReadEstimatedSize(subKey),
                        uninstallString,
                        hive.ToString()));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<InstalledAppRecord>>(
            apps
                .DistinctBy(static app => app.DisplayName)
                .OrderByDescending(static app => app.EstimatedSizeBytes)
                .ThenBy(static app => app.DisplayName)
                .ToList());
    }

    public Task LaunchUninstallerAsync(InstalledAppRecord app, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            return Task.CompletedTask;
        }

        var command = app.UninstallCommand.Trim();
        var fileName = command;
        var arguments = string.Empty;

        if (command.StartsWith("\"", StringComparison.OrdinalIgnoreCase))
        {
            var endIndex = command.IndexOf('"', 1);
            if (endIndex > 1)
            {
                fileName = command[1..endIndex];
                arguments = command[(endIndex + 1)..].Trim();
            }
        }
        else
        {
            var firstSpace = command.IndexOf(' ');
            if (firstSpace > 0)
            {
                fileName = command[..firstSpace];
                arguments = command[(firstSpace + 1)..];
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    private static long ReadEstimatedSize(RegistryKey subKey)
    {
        var raw = subKey.GetValue("EstimatedSize");
        return raw switch
        {
            int sizeInKb when sizeInKb > 0 => sizeInKb * 1024L,
            long sizeInKb when sizeInKb > 0 => sizeInKb * 1024L,
            _ => 0L
        };
    }
}
