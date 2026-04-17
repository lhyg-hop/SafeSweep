using SafeSweep.Models;

namespace SafeSweep.Services.Inventory;

public interface IScanProvider
{
    IAsyncEnumerable<ScanObservation> ScanAsync(ScanMode mode, CancellationToken cancellationToken = default);
}
