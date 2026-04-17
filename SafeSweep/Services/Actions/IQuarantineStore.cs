using SafeSweep.Models;

namespace SafeSweep.Services.Actions;

public interface IQuarantineStore
{
    string RootPath { get; }
    string? GetPreferredQuarantineRoot(string sourcePath);
    bool HasNonSystemDriveQuarantine(string sourcePath);
    Task<string> MoveToQuarantineAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task RestoreAsync(string quarantinePath, string originalPath, ConflictPolicy conflictPolicy, CancellationToken cancellationToken = default);
}
