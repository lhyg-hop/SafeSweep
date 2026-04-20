using System.IO;
using System.Runtime.InteropServices;
using SafeSweep.Models;
using SafeSweep.Services.Storage;
using SafeSweep.Utils;

namespace SafeSweep.Services.Actions;

public sealed class ActionExecutor : IActionExecutor
{
    private const uint ShErbNoConfirmation = 0x00000001;
    private const uint ShErbNoProgressUi = 0x00000002;
    private const uint ShErbNoSound = 0x00000004;

    private static readonly HashSet<string> ProtectedCleanupCategories =
    [
        "UserTemp",
        "SystemTemp",
        "ThumbnailCache",
        "BrowserCache",
        "RecycleBin"
    ];

    private readonly IQuarantineStore _quarantineStore;
    private readonly IAuditRepository _repository;

    public ActionExecutor(IQuarantineStore quarantineStore, IAuditRepository repository)
    {
        _quarantineStore = quarantineStore;
        _repository = repository;
    }

    public async Task<IReadOnlyList<ActionRecord>> ExecutePlanAsync(ExecutionPlan plan, CancellationToken cancellationToken = default)
    {
        var results = new List<ActionRecord>();

        foreach (var item in plan.Items.Where(static item => item.Status != PlanItemStatus.Blocked))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startedAt = DateTimeOffset.UtcNow;
            ActionRecord record;

            try
            {
                ValidateExecutionTarget(item);
                string? quarantinePath = null;

                record = item.ActionPolicy switch
                {
                    ActionPolicy.DirectDelete => await ExecuteDirectDeleteAsync(plan.PlanId, item, startedAt, cancellationToken),
                    ActionPolicy.Quarantine => await ExecuteQuarantineAsync(plan.PlanId, item, startedAt, cancellationToken),
                    _ => new ActionRecord(
                        Guid.NewGuid().ToString("N"),
                        plan.PlanId,
                        item.Path,
                        item.ActionPolicy,
                        item.Path,
                        quarantinePath,
                        0,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        ActionRecordStatus.Skipped,
                        null,
                        false,
                        "该项目不在自动执行范围内，已跳过。")
                };
            }
            catch (Exception ex)
            {
                record = new ActionRecord(
                    Guid.NewGuid().ToString("N"),
                    plan.PlanId,
                    item.Path,
                    item.ActionPolicy,
                    item.Path,
                    null,
                    0,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    ActionRecordStatus.Failed,
                    ex.GetType().Name,
                    false,
                    ex.Message);
            }

            await _repository.SaveActionRecordAsync(record, cancellationToken);
            results.Add(record);
        }

        return results;
    }

    public async Task<ActionRecord> RestoreAsync(
        ActionRecord record,
        ConflictPolicy conflictPolicy,
        CancellationToken cancellationToken = default)
    {
        if (!record.CanRestore || string.IsNullOrWhiteSpace(record.QuarantinePath) || string.IsNullOrWhiteSpace(record.OriginalPath))
        {
            throw new InvalidOperationException("当前记录不支持恢复。");
        }

        await _quarantineStore.RestoreAsync(record.QuarantinePath, record.OriginalPath, conflictPolicy, cancellationToken);

        var restored = record with
        {
            EndedAt = DateTimeOffset.UtcNow,
            Status = ActionRecordStatus.Restored,
            Summary = "已从隔离区恢复。"
        };

        await _repository.SaveActionRecordAsync(restored, cancellationToken);
        await _repository.SaveRestoreRecordAsync(
            new RestoreRecord(record.ActionId, ActionRecordStatus.Restored, conflictPolicy, DateTimeOffset.UtcNow),
            cancellationToken);

        return restored;
    }

    private async Task<ActionRecord> ExecuteDirectDeleteAsync(
        string planId,
        ExecutionPlanItem item,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var freedBytes = await DeletePathContentAsync(item.Path, item.Category, cancellationToken);

        return new ActionRecord(
            Guid.NewGuid().ToString("N"),
            planId,
            item.Path,
            item.ActionPolicy,
            item.Path,
            null,
            freedBytes,
            startedAt,
            DateTimeOffset.UtcNow,
            ActionRecordStatus.Completed,
            null,
            false,
            $"已清理：{ToDisplayCategory(item.Category)}。");
    }

    private async Task<ActionRecord> ExecuteQuarantineAsync(
        string planId,
        ExecutionPlanItem item,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var quarantinePath = await _quarantineStore.MoveToQuarantineAsync(item.Path, cancellationToken);
        var summary = item.CanActuallyFreeSystemDrive
            ? "已移入隔离区，并释放系统盘空间。"
            : "已移入隔离区，但隔离区仍位于系统盘。";

        return new ActionRecord(
            Guid.NewGuid().ToString("N"),
            planId,
            item.Path,
            item.ActionPolicy,
            item.Path,
            quarantinePath,
            item.CanActuallyFreeSystemDrive ? item.EstimatedFreedBytes : 0,
            startedAt,
            DateTimeOffset.UtcNow,
            ActionRecordStatus.Completed,
            null,
            true,
            summary);
    }

    private static void ValidateExecutionTarget(ExecutionPlanItem item)
    {
        if (item.ActionPolicy == ActionPolicy.None)
        {
            throw new InvalidOperationException("被阻止的计划项不能直接执行。");
        }

        if (PathSafety.IsProtectedPath(item.Path) && !ProtectedCleanupCategories.Contains(item.Category))
        {
            throw new UnauthorizedAccessException("该路径属于受保护位置，执行器已阻止操作。");
        }
    }

    private static Task<long> DeletePathContentAsync(string path, string category, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                var size = file.Length;
                file.IsReadOnly = false;
                file.Delete();
                return size;
            }

            if (!Directory.Exists(path))
            {
                return 0L;
            }

            if (category.Equals("RecycleBin", StringComparison.OrdinalIgnoreCase))
            {
                var result = SHEmptyRecycleBin(
                    IntPtr.Zero,
                    null,
                    ShErbNoConfirmation | ShErbNoProgressUi | ShErbNoSound);

                if (result != 0)
                {
                    throw new IOException($"清空回收站失败，系统返回代码：{result}。");
                }

                return 0L;
            }

            long deletedBytes = 0;
            var pending = new Stack<DirectoryInfo>();
            var directories = new List<DirectoryInfo>();
            pending.Push(new DirectoryInfo(path));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                directories.Add(current);

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
                    try
                    {
                        switch (entry)
                        {
                            case FileInfo fileInfo:
                                if (PathSafety.IsReparsePoint(fileInfo) || PathSafety.IsCloudPlaceholder(fileInfo))
                                {
                                    continue;
                                }

                                deletedBytes += fileInfo.Length;
                                fileInfo.IsReadOnly = false;
                                fileInfo.Delete();
                                break;

                            case DirectoryInfo childDirectory
                                when !PathSafety.IsReparsePoint(childDirectory) &&
                                     !PathSafety.IsCloudPlaceholder(childDirectory):
                                pending.Push(childDirectory);
                                break;
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup. Skipped entries are surfaced by audit logs, not by hard failure.
                    }
                }
            }

            foreach (var directory in directories.OrderByDescending(static directory => directory.FullName.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directory.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (!PathSafety.IsReparsePoint(directory))
                    {
                        directory.Delete();
                    }
                }
                catch
                {
                    // Leave partially cleaned folders in place when a child could not be removed.
                }
            }

            return deletedBytes;
        }, cancellationToken);
    }

    [DllImport("Shell32.dll")]
    private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private static string ToDisplayCategory(string category) => category switch
    {
        "UserTemp" => "用户临时文件",
        "SystemTemp" => "系统临时文件",
        "ThumbnailCache" => "缩略图缓存",
        "BrowserCache" => "浏览器缓存",
        "RecycleBin" => "回收站",
        "Downloads" => "下载目录",
        "Desktop" => "桌面",
        "Videos" => "视频",
        "Documents" => "文档",
        "DiskImage" => "磁盘镜像",
        "Archive" => "压缩包",
        "Installer" => "安装包",
        "VirtualDisk" => "虚拟磁盘",
        "Database" => "数据库文件",
        "LargeFile" => "大文件",
        _ => category
    };
}
