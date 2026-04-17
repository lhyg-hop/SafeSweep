using System.Collections.Concurrent;
using SafeSweep.Models;
using SafeSweep.Utils;

namespace SafeSweep.Services.Inventory;

public sealed class DefaultScanProvider : IScanProvider
{
    private const long DeepScanLargeFileThreshold = 256L * 1024 * 1024;

    public async IAsyncEnumerable<ScanObservation> ScanAsync(
        ScanMode mode,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var observation in GetQuickTargets())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await BuildObservationAsync(
                observation.Path,
                observation.Source,
                observation.Category,
                observation.TargetType,
                cancellationToken);

            if (result is not null)
            {
                yield return result;
            }
        }

        if (mode != ScanMode.Deep)
        {
            yield break;
        }

        await foreach (var observation in EnumerateDeepCandidatesAsync(cancellationToken))
        {
            yield return observation;
        }
    }

    private static IEnumerable<(string Path, string Source, string Category, TargetType TargetType)> GetQuickTargets()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        return
        [
            (Path.Combine(userProfile, "Downloads"), "UserFiles", "Downloads", TargetType.Directory),
            (Path.Combine(userProfile, "Desktop"), "UserFiles", "Desktop", TargetType.Directory),
            (Path.Combine(userProfile, "Videos"), "UserFiles", "Videos", TargetType.Directory),
            (Path.Combine(userProfile, "Documents"), "UserFiles", "Documents", TargetType.Directory),
            (Path.GetTempPath(), "SafeCleanup", "UserTemp", TargetType.Directory),
            (Path.Combine(windows, "Temp"), "SafeCleanup", "SystemTemp", TargetType.Directory),
            (Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), "SafeCleanup", "ThumbnailCache", TargetType.Directory),
            (Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"), "SafeCleanup", "BrowserCache", TargetType.Directory),
            (Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"), "SafeCleanup", "BrowserCache", TargetType.Directory),
            (Path.Combine(systemRoot, "$Recycle.Bin"), "SafeCleanup", "RecycleBin", TargetType.Directory),
            (Path.Combine(windows, "WinSxS"), "SystemGuidance", "WinSxS", TargetType.System),
            (Path.Combine(windows, "SoftwareDistribution", "Download"), "SystemGuidance", "WindowsUpdateDownload", TargetType.System),
            (Path.Combine(systemRoot, "hiberfil.sys"), "SystemGuidance", "HibernateFile", TargetType.System),
            (Path.Combine(systemRoot, "pagefile.sys"), "SystemGuidance", "PageFile", TargetType.System),
            (Path.Combine(systemRoot, "System Volume Information"), "SystemGuidance", "RestorePoints", TargetType.System)
        ];
    }

    private static async IAsyncEnumerable<ScanObservation> EnumerateDeepCandidatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Desktop"),
            Path.Combine(userProfile, "Documents"),
            Path.Combine(userProfile, "Videos"),
            Path.Combine(userProfile, "Pictures"),
            Path.Combine(userProfile, "Music"),
            Path.Combine(userProfile, "AppData", "Local", "Temp")
        };

        var visited = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(Directory.Exists))
        {
            await foreach (var item in EnumerateLargeFilesAsync(root, visited, cancellationToken))
            {
                yield return item;
            }
        }
    }

    private static async IAsyncEnumerable<ScanObservation> EnumerateLargeFilesAsync(
        string root,
        ConcurrentDictionary<string, byte> visited,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            if (!visited.TryAdd(directory.FullName, 0))
            {
                continue;
            }

            if (PathSafety.IsProtectedPath(directory.FullName) || PathSafety.IsReparsePoint(directory))
            {
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry is DirectoryInfo childDirectory)
                {
                    if (PathSafety.IsReparsePoint(childDirectory) || PathSafety.IsCloudPlaceholder(childDirectory))
                    {
                        continue;
                    }

                    pending.Push(childDirectory);
                    continue;
                }

                if (entry is not FileInfo file ||
                    PathSafety.IsReparsePoint(file) ||
                    PathSafety.IsCloudPlaceholder(file) ||
                    file.Length < DeepScanLargeFileThreshold)
                {
                    continue;
                }

                var target = new ScanTarget(
                    file.FullName,
                    TargetType.File,
                    PathSafety.GetVolumeId(file.FullName),
                    PathSafety.IsProtectedPath(file.FullName),
                    PathSafety.IsReparsePoint(file),
                    PathSafety.IsCloudPlaceholder(file) ? CloudFileState.Placeholder : CloudFileState.Local);

                yield return new ScanObservation(
                    target,
                    file.Length,
                    file.LastWriteTimeUtc,
                    IsFileLocked(file.FullName),
                    "UserFiles",
                    false,
                    GuessLargeFileCategory(file),
                    file.Extension);

                await Task.Yield();
            }
        }
    }

    private static async Task<ScanObservation?> BuildObservationAsync(
        string path,
        string source,
        string category,
        TargetType targetType,
        CancellationToken cancellationToken)
    {
        if (!PathSafety.TryCreateTarget(path, targetType, out var target, out _))
        {
            return null;
        }

        long size;
        DateTimeOffset? modifiedAt = null;
        var isLocked = false;
        var isDirectory = target.TargetType != TargetType.File;

        if (File.Exists(path))
        {
            try
            {
                var file = new FileInfo(path);
                size = file.Length;
                modifiedAt = file.LastWriteTimeUtc;
                isLocked = IsFileLocked(path);
            }
            catch
            {
                return null;
            }
        }
        else if (Directory.Exists(path))
        {
            size = await Task.Run(() => GetDirectorySize(path, cancellationToken), cancellationToken);
            modifiedAt = Directory.GetLastWriteTimeUtc(path);
        }
        else
        {
            return null;
        }

        return new ScanObservation(
            target,
            size,
            modifiedAt,
            isLocked,
            source,
            isDirectory,
            category);
    }

    private static long GetDirectorySize(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(path));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            if (PathSafety.IsReparsePoint(current))
            {
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = current.EnumerateFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo directory)
                {
                    if (!PathSafety.IsReparsePoint(directory) && !PathSafety.IsCloudPlaceholder(directory))
                    {
                        stack.Push(directory);
                    }

                    continue;
                }

                if (entry is FileInfo file && !PathSafety.IsCloudPlaceholder(file))
                {
                    total += file.Length;
                }
            }
        }

        return total;
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string GuessLargeFileCategory(FileInfo file)
    {
        return file.Extension.ToLowerInvariant() switch
        {
            ".iso" or ".img" => "DiskImage",
            ".zip" or ".7z" or ".rar" or ".tar" => "Archive",
            ".msi" or ".exe" => "Installer",
            ".vhd" or ".vhdx" or ".vmdk" => "VirtualDisk",
            ".mp4" or ".mkv" or ".avi" or ".mov" => "Video",
            ".db" or ".sqlite" => "Database",
            _ => "LargeFile"
        };
    }
}
