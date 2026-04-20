using SafeSweep.Models;
using SafeSweep.Utils;

namespace SafeSweep.ViewModels;

public sealed class InstalledAppItemViewModel
{
    public InstalledAppItemViewModel(InstalledAppRecord app)
    {
        App = app;
    }

    public InstalledAppRecord App { get; }
    public string DisplayName => App.DisplayName;
    public string Publisher => string.IsNullOrWhiteSpace(App.Publisher) ? "未知" : App.Publisher;
    public string SizeText => App.EstimatedSizeBytes > 0 ? ByteSizeFormatter.Format(App.EstimatedSizeBytes) : "未提供";
    public string InstallLocation => string.IsNullOrWhiteSpace(App.InstallLocation) ? "未提供" : App.InstallLocation!;
}
