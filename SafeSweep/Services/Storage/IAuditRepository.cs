using SafeSweep.Models;

namespace SafeSweep.Services.Storage;

public interface IAuditRepository
{
    Task<IReadOnlyCollection<string>> GetIgnoredPathsAsync(CancellationToken cancellationToken = default);
    Task AddIgnoredPathAsync(string path, CancellationToken cancellationToken = default);
    Task SaveActionRecordAsync(ActionRecord record, CancellationToken cancellationToken = default);
    Task SaveRestoreRecordAsync(RestoreRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActionRecord>> GetActionRecordsAsync(CancellationToken cancellationToken = default);
    Task SaveScanSnapshotAsync(string sessionId, ScanMode mode, IEnumerable<ScanFinding> findings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanSnapshotSummary>> GetGrowthSummariesAsync(CancellationToken cancellationToken = default);
}
