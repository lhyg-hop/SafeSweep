using System.IO;
using System.Windows;
using SafeSweep.Services.Actions;
using SafeSweep.Services.Inventory;
using SafeSweep.Services.Policy;
using SafeSweep.Services.Storage;
using SafeSweep.Services.SystemIntegration;
using SafeSweep.ViewModels;
using SafeSweep.Views;

namespace SafeSweep;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SafeSweep");
        Directory.CreateDirectory(appData);

        var repository = new SqliteAuditRepository(Path.Combine(appData, "safesweep.db"));
        var quarantineStore = new QuarantineStore(Path.Combine(appData, "Quarantine"), repository);
        var systemAdapter = new WindowsSystemAdapter();
        var appProvider = new RegistryInstalledAppProvider();
        var policy = new DefaultRuleEvaluator(repository);
        var scanProvider = new DefaultScanProvider();
        var planner = new ActionPlanner(quarantineStore);
        var executor = new ActionExecutor(quarantineStore, repository);

        var viewModel = new MainWindowViewModel(
            scanProvider,
            policy,
            planner,
            executor,
            quarantineStore,
            repository,
            systemAdapter,
            appProvider);

        var window = new MainWindow
        {
            DataContext = viewModel
        };

        MainWindow = window;
        window.Show();
    }
}
