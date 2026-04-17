using System.IO;
using SafeSweep.Models;

namespace SafeSweep.Utils;

public static class PathSafety
{
    private static readonly string[] ProtectedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData\\Roaming\\Microsoft\\Windows\\Start Menu"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "System Volume Information")
    ];

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.ToUpperInvariant();
    }

    public static bool IsProtectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = Normalize(path);
        return ProtectedRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Normalize)
            .Any(root => normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCloudPlaceholder(FileSystemInfo info)
    {
        const FileAttributes placeholderAttributes =
            (FileAttributes)0x00400000 | // RecallOnDataAccess
            (FileAttributes)0x00040000 | // RecallOnOpen
            (FileAttributes)0x00080000;  // Offline

        return (info.Attributes & placeholderAttributes) != 0;
    }

    public static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    public static string GetVolumeId(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrWhiteSpace(root) ? "UNKNOWN" : root.ToUpperInvariant();
    }

    public static bool IsOnSystemDrive(string path)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return root.Equals(systemRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryCreateTarget(string path, TargetType targetType, out ScanTarget target, out string? reason)
    {
        target = default!;
        reason = null;

        try
        {
            var normalized = Path.GetFullPath(path);
            var volumeId = GetVolumeId(normalized);

            if (File.Exists(normalized))
            {
                var file = new FileInfo(normalized);
                target = new ScanTarget(
                    normalized,
                    TargetType.File,
                    volumeId,
                    IsProtectedPath(normalized),
                    IsReparsePoint(file),
                    IsCloudPlaceholder(file) ? CloudFileState.Placeholder : CloudFileState.Local);
                return true;
            }

            if (Directory.Exists(normalized))
            {
                var directory = new DirectoryInfo(normalized);
                target = new ScanTarget(
                    normalized,
                    targetType == TargetType.Virtual ? TargetType.Virtual : TargetType.Directory,
                    volumeId,
                    IsProtectedPath(normalized),
                    IsReparsePoint(directory),
                    IsCloudPlaceholder(directory) ? CloudFileState.Placeholder : CloudFileState.Local);
                return true;
            }

            target = new ScanTarget(
                normalized,
                targetType,
                volumeId,
                IsProtectedPath(normalized),
                false,
                CloudFileState.Unknown);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
