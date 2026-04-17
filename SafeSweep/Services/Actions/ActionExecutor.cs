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
                        "Item was skipped because it is not part of the automatic executor.")
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
            throw new InvalidOperationException("This action record cannot be restored.");
        }

        await _quarantineStore.RestoreAsync(record.QuarantinePath, record.OriginalPath, conflictPolicy, cancellationToken);

        var restored = record with
        {
            EndedAt = DateTimeOffset.UtcNow,
            Status = ActionRecordStatus.Restored,
            Summary = "Item restored from quarantine."
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
            $"Cleaned {item.Category}.");
    }

    private async Task<ActionRecord> ExecuteQuarantineAsync(
        string planId,
        ExecutionPlanItem item,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var quarantinePath = await _quarantineStore.MoveToQuarantineAsync(item.Path, cancellationToken);
        var summary = item.CanActuallyFreeSystemDrive
            ? "Moved to quarantine and released system drive space."
            : "Moved to quarantine, but the quarantine root is still on the system drive.";

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
            throw new InvalidOperationException("Blocked plan item cannot be executed.");
        }

        if (PathSafety.IsProtectedPath(item.Path) && !ProtectedCleanupCategories.Contains(item.Category))
        {
            throw new UnauthorizedAccessException("Protected path blocked by executor.");
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
                    throw new IOException($"SHEmptyRecycleBin failed with code {result}.");
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
}
