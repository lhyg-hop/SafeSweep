using SafeSweep.Models;

namespace SafeSweep.Services.SystemIntegration;

public interface ISystemAdapter
{
    IReadOnlyList<SystemGuidanceItem> GetGuidanceItems();
    Task OpenGuidanceAsync(SystemGuidanceItem item, CancellationToken cancellationToken = default);
    Task OpenPathAsync(string path, CancellationToken cancellationToken = default);
}
