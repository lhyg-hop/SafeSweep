using SafeSweep.Models;

namespace SafeSweep.Services.SystemIntegration;

public interface IInstalledAppProvider
{
    Task<IReadOnlyList<InstalledAppRecord>> GetInstalledAppsAsync(CancellationToken cancellationToken = default);
    Task LaunchUninstallerAsync(InstalledAppRecord app, CancellationToken cancellationToken = default);
}
