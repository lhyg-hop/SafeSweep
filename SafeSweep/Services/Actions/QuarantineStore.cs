using System.IO;
using SafeSweep.Models;
using SafeSweep.Services.Storage;
using SafeSweep.Utils;

namespace SafeSweep.Services.Actions;

public sealed class QuarantineStore : IQuarantineStore
{
    private readonly IAuditRepository _repository;

    public QuarantineStore(string rootPath, IAuditRepository repository)
    {
        RootPath = rootPath;
        _repository = repository;
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string? GetPreferredQuarantineRoot(string sourcePath)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var candidate = DriveInfo.GetDrives()
            .Where(static drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .OrderBy(drive => drive.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(drive => drive.AvailableFreeSpace)
            .Select(drive => Path.Combine(drive.RootDirectory.FullName, "SafeSweep Quarantine"))
            .FirstOrDefault();

        return candidate;
    }

    public bool HasNonSystemDriveQuarantine(string sourcePath)
    {
        var preferred = GetPreferredQuarantineRoot(sourcePath);
        return preferred is not null && !PathSafety.IsOnSystemDrive(preferred);
    }

    public async Task<string> MoveToQuarantineAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var quarantineRoot = GetPreferredQuarantineRoot(sourcePath) ?? RootPath;
        Directory.CreateDirectory(quarantineRoot);

        var targetPath = Path.Combine(
            quarantineRoot,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"),
            Path.GetFileName(sourcePath));

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (File.Exists(sourcePath))
        {
            await Task.Run(() => File.Move(sourcePath, targetPath), cancellationToken);
            return targetPath;
        }

        if (Directory.Exists(sourcePath))
        {
            await Task.Run(() => MoveDirectory(sourcePath, targetPath), cancellationToken);
            return targetPath;
        }

        throw new FileNotFoundException("源路径已不存在。", sourcePath);
    }

    public async Task RestoreAsync(
        string quarantinePath,
        string originalPath,
        ConflictPolicy conflictPolicy,
        CancellationToken cancellationToken = default)
    {
        var originalDirectory = Path.GetDirectoryName(originalPath);
        if (!string.IsNullOrWhiteSpace(originalDirectory))
        {
            Directory.CreateDirectory(originalDirectory);
        }

        var restorePath = conflictPolicy switch
        {
            ConflictPolicy.Skip when File.Exists(originalPath) || Directory.Exists(originalPath) =>
                throw new IOException("原路径已存在同名内容。"),
            ConflictPolicy.Rename when File.Exists(originalPath) || Directory.Exists(originalPath) =>
                BuildRenamedPath(originalPath),
            _ => originalPath
        };

        await Task.Run(() =>
        {
            if (Directory.Exists(quarantinePath))
            {
                if (Directory.Exists(restorePath) && conflictPolicy == ConflictPolicy.Overwrite)
                {
                    Directory.Delete(restorePath, true);
                }

                MoveDirectory(quarantinePath, restorePath);
                return;
            }

            if (File.Exists(quarantinePath))
            {
                if (File.Exists(restorePath) && conflictPolicy == ConflictPolicy.Overwrite)
                {
                    File.Delete(restorePath);
                }

                File.Move(quarantinePath, restorePath);
                return;
            }

            throw new FileNotFoundException("隔离区项目不存在。", quarantinePath);
        }, cancellationToken);
    }

    private static string BuildRenamedPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);
        return Path.Combine(directory, $"{name}-restored-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}{extension}");
    }

    private static void MoveDirectory(string sourcePath, string targetPath)
    {
        if (string.Equals(
                Path.GetPathRoot(sourcePath),
                Path.GetPathRoot(targetPath),
                StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(sourcePath, targetPath);
            return;
        }

        CopyDirectory(sourcePath, targetPath);
        Directory.Delete(sourcePath, true);
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        var source = new DirectoryInfo(sourcePath);
        Directory.CreateDirectory(targetPath);

        foreach (var file in source.GetFiles())
        {
            file.CopyTo(Path.Combine(targetPath, file.Name), overwrite: false);
        }

        foreach (var directory in source.GetDirectories())
        {
            CopyDirectory(directory.FullName, Path.Combine(targetPath, directory.Name));
        }
    }
}
