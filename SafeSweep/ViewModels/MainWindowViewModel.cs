using System.Collections.ObjectModel;
using System.Text;
using SafeSweep.Models;
using SafeSweep.Services.Actions;
using SafeSweep.Services.Inventory;
using SafeSweep.Services.Policy;
using SafeSweep.Services.Storage;
using SafeSweep.Services.SystemIntegration;
using SafeSweep.Utils;

namespace SafeSweep.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IScanProvider _scanProvider;
    private readonly IRuleEvaluator _ruleEvaluator;
    private readonly IActionPlanner _planner;
    private readonly IActionExecutor _executor;
    private readonly IQuarantineStore _quarantineStore;
    private readonly IAuditRepository _repository;
    private readonly ISystemAdapter _systemAdapter;
    private readonly IInstalledAppProvider _installedAppProvider;

    private string _statusText = "Ready. Start with a quick scan.";
    private string _planOverview = "No preview yet.";
    private bool _preferImmediateFreeSpace;
    private bool _isBusy;
    private ExecutionPlan? _currentPlan;

    public MainWindowViewModel(
        IScanProvider scanProvider,
        IRuleEvaluator ruleEvaluator,
        IActionPlanner planner,
        IActionExecutor executor,
        IQuarantineStore quarantineStore,
        IAuditRepository repository,
        ISystemAdapter systemAdapter,
        IInstalledAppProvider installedAppProvider)
    {
        _scanProvider = scanProvider;
        _ruleEvaluator = ruleEvaluator;
        _planner = planner;
        _executor = executor;
        _quarantineStore = quarantineStore;
        _repository = repository;
        _systemAdapter = systemAdapter;
        _installedAppProvider = installedAppProvider;

        SummaryCards =
        [
            new SummaryCardViewModel { Title = "Safe To Release", Value = "-", Subtitle = "Waiting for scan" },
            new SummaryCardViewModel { Title = "Review Needed", Value = "-", Subtitle = "Waiting for scan" },
            new SummaryCardViewModel { Title = "Official Tools", Value = "-", Subtitle = "Waiting for scan" },
            new SummaryCardViewModel { Title = "Fastest Growth", Value = "-", Subtitle = "Needs at least two scans" }
        ];

        GuidanceItems = new ObservableCollection<SystemGuidanceItem>(_systemAdapter.GetGuidanceItems());
        Findings = [];
        InstalledApps = [];
        ActionHistory = [];
        GrowthSummaries = [];

        QuickScanCommand = new AsyncRelayCommand(() => RunScanAsync(ScanMode.Quick), () => !IsBusy);
        DeepScanCommand = new AsyncRelayCommand(() => RunScanAsync(ScanMode.Deep), () => !IsBusy);
        PreviewPlanCommand = new AsyncRelayCommand(PreviewPlanAsync, () => !IsBusy && Findings.Any(static item => item.IsSelected));
        ExecutePlanCommand = new AsyncRelayCommand(ExecutePlanAsync, () => !IsBusy && CurrentPlan is not null);
        RefreshAppsCommand = new AsyncRelayCommand(LoadInstalledAppsAsync, () => !IsBusy);
        RefreshHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, () => !IsBusy);
        OpenPathCommand = new AsyncRelayCommand(OpenPathAsync);
        IgnoreFindingCommand = new AsyncRelayCommand(IgnoreFindingAsync);
        OpenGuidanceCommand = new AsyncRelayCommand(OpenGuidanceAsync);
        UninstallAppCommand = new AsyncRelayCommand(UninstallAppAsync);
        RestoreActionCommand = new AsyncRelayCommand(RestoreActionAsync);

        _ = InitializeAsync();
    }

    public ObservableCollection<SummaryCardViewModel> SummaryCards { get; }
    public ObservableCollection<FindingItemViewModel> Findings { get; }
    public ObservableCollection<InstalledAppRecord> InstalledApps { get; }
    public ObservableCollection<ActionRecord> ActionHistory { get; }
    public ObservableCollection<SystemGuidanceItem> GuidanceItems { get; }
    public ObservableCollection<ScanSnapshotSummary> GrowthSummaries { get; }

    public AsyncRelayCommand QuickScanCommand { get; }
    public AsyncRelayCommand DeepScanCommand { get; }
    public AsyncRelayCommand PreviewPlanCommand { get; }
    public AsyncRelayCommand ExecutePlanCommand { get; }
    public AsyncRelayCommand RefreshAppsCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand OpenPathCommand { get; }
    public AsyncRelayCommand IgnoreFindingCommand { get; }
    public AsyncRelayCommand OpenGuidanceCommand { get; }
    public AsyncRelayCommand UninstallAppCommand { get; }
    public AsyncRelayCommand RestoreActionCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string PlanOverview
    {
        get => _planOverview;
        private set => SetProperty(ref _planOverview, value);
    }

    public bool PreferImmediateFreeSpace
    {
        get => _preferImmediateFreeSpace;
        set => SetProperty(ref _preferImmediateFreeSpace, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public ExecutionPlan? CurrentPlan
    {
        get => _currentPlan;
        private set
        {
            if (SetProperty(ref _currentPlan, value))
            {
                RefreshCommandStates();
            }
        }
    }

    private async Task InitializeAsync()
    {
        await LoadHistoryAsync();
        await LoadInstalledAppsAsync();
        await LoadGrowthAsync();
    }

    private async Task RunScanAsync(ScanMode mode)
    {
        IsBusy = true;
        Findings.Clear();
        CurrentPlan = null;
        PlanOverview = "Build a preview after the scan finishes.";
        StatusText = mode == ScanMode.Quick ? "Running quick scan..." : "Running deep scan...";

        var collectedFindings = new List<ScanFinding>();

        try
        {
            await foreach (var observation in _scanProvider.ScanAsync(mode))
            {
                var finding = await _ruleEvaluator.EvaluateAsync(observation);
                if (finding is null)
                {
                    continue;
                }

                var item = new FindingItemViewModel(finding);
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(FindingItemViewModel.IsSelected))
                    {
                        RefreshCommandStates();
                    }
                };

                Findings.Add(item);
                collectedFindings.Add(finding);
                StatusText = $"Found {Findings.Count} candidate items...";
            }

            var sessionId = Guid.NewGuid().ToString("N");
            await _repository.SaveScanSnapshotAsync(sessionId, mode, collectedFindings);
            await LoadGrowthAsync();
            UpdateSummaryCards();

            StatusText = mode == ScanMode.Quick
                ? $"Quick scan complete. {Findings.Count} candidates found."
                : $"Deep scan complete. {Findings.Count} candidates found.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PreviewPlanAsync()
    {
        IsBusy = true;

        try
        {
            var selected = Findings.Where(static item => item.IsSelected).Select(static item => item.Finding).ToList();
            CurrentPlan = await _planner.BuildPlanAsync(selected, PreferImmediateFreeSpace);

            var executableCount = CurrentPlan.Items.Count(static item => item.Status != PlanItemStatus.Blocked);
            var blockedCount = CurrentPlan.Items.Count - executableCount;
            var warnings = CurrentPlan.Items.Sum(static item => item.Warnings.Count);

            var builder = new StringBuilder();
            builder.AppendLine($"Items: {CurrentPlan.Items.Count}");
            builder.AppendLine($"Executable: {executableCount}");
            builder.AppendLine($"Blocked: {blockedCount}");
            builder.AppendLine($"Estimated release: {ByteSizeFormatter.Format(CurrentPlan.EstimatedFreedBytes)}");
            builder.AppendLine($"Needs elevation: {(CurrentPlan.RequiresElevation ? "Yes" : "No")}");
            builder.AppendLine($"Quarantine root: {_quarantineStore.GetPreferredQuarantineRoot(Environment.SystemDirectory) ?? "Not found"}");
            builder.AppendLine($"Warnings: {warnings}");
            PlanOverview = builder.ToString().TrimEnd();

            StatusText = "Preview complete. Check release estimates, rollback mode, warnings, and blocked items before executing.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecutePlanAsync()
    {
        if (CurrentPlan is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var results = await _executor.ExecutePlanAsync(CurrentPlan);
            var completedPaths = results
                .Where(static item => item.Status == ActionRecordStatus.Completed)
                .Select(static item => item.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var finding in Findings.Where(item => completedPaths.Contains(item.Finding.Path)).ToList())
            {
                Findings.Remove(finding);
            }

            await LoadHistoryAsync();
            UpdateSummaryCards();

            var succeeded = results.Count(static item => item.Status == ActionRecordStatus.Completed);
            var failed = results.Count(static item => item.Status == ActionRecordStatus.Failed);
            StatusText = $"Execution complete. Succeeded: {succeeded}. Failed: {failed}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadInstalledAppsAsync()
    {
        InstalledApps.Clear();
        foreach (var app in await _installedAppProvider.GetInstalledAppsAsync())
        {
            InstalledApps.Add(app);
        }
    }

    private async Task LoadHistoryAsync()
    {
        ActionHistory.Clear();
        foreach (var item in await _repository.GetActionRecordsAsync())
        {
            ActionHistory.Add(item);
        }
    }

    private async Task LoadGrowthAsync()
    {
        GrowthSummaries.Clear();
        foreach (var summary in await _repository.GetGrowthSummariesAsync())
        {
            GrowthSummaries.Add(summary);
        }

        UpdateSummaryCards();
    }

    private async Task OpenPathAsync(object? parameter)
    {
        switch (parameter)
        {
            case FindingItemViewModel findingItem:
                await _systemAdapter.OpenPathAsync(findingItem.Finding.Path);
                break;
            case InstalledAppRecord app when !string.IsNullOrWhiteSpace(app.InstallLocation):
                await _systemAdapter.OpenPathAsync(app.InstallLocation!);
                break;
            case ActionRecord record when !string.IsNullOrWhiteSpace(record.OriginalPath):
                await _systemAdapter.OpenPathAsync(record.OriginalPath!);
                break;
        }
    }

    private async Task IgnoreFindingAsync(object? parameter)
    {
        if (parameter is not FindingItemViewModel findingItem)
        {
            return;
        }

        await _repository.AddIgnoredPathAsync(findingItem.Finding.Path);
        Findings.Remove(findingItem);
        UpdateSummaryCards();
        StatusText = "Item added to ignore list.";
    }

    private async Task OpenGuidanceAsync(object? parameter)
    {
        if (parameter is SystemGuidanceItem guidance)
        {
            await _systemAdapter.OpenGuidanceAsync(guidance);
        }
    }

    private async Task UninstallAppAsync(object? parameter)
    {
        if (parameter is InstalledAppRecord app)
        {
            await _installedAppProvider.LaunchUninstallerAsync(app);
            StatusText = $"Launched the official uninstall entry for {app.DisplayName}.";
        }
    }

    private async Task RestoreActionAsync(object? parameter)
    {
        if (parameter is not ActionRecord record || !record.CanRestore)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _executor.RestoreAsync(record, ConflictPolicy.Rename);
            await LoadHistoryAsync();
            StatusText = "Restore complete. If the original path already existed, the restored item was renamed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateSummaryCards()
    {
        var safeCleanupBytes = Findings
            .Where(static item => item.Finding.RiskTier == RiskTier.L1SafeCleanup)
            .Sum(static item => item.Finding.Size);
        var reviewBytes = Findings
            .Where(static item => item.Finding.RiskTier == RiskTier.L2Recoverable)
            .Sum(static item => item.Finding.Size);
        var officialBytes = Findings
            .Where(static item => item.Finding.RiskTier is RiskTier.L0ReadOnly or RiskTier.L3Guided)
            .Sum(static item => item.Finding.Size);
        var fastestGrowth = GrowthSummaries.OrderByDescending(static item => item.DeltaBytes).FirstOrDefault();

        SummaryCards[0].Value = ByteSizeFormatter.Format(safeCleanupBytes);
        SummaryCards[0].Subtitle = "Deterministic cleanup under allow-list rules";
        SummaryCards[1].Value = ByteSizeFormatter.Format(reviewBytes);
        SummaryCards[1].Subtitle = "Recoverable candidates that still need confirmation";
        SummaryCards[2].Value = ByteSizeFormatter.Format(officialBytes);
        SummaryCards[2].Subtitle = "Explain and route, never delete directly";
        SummaryCards[3].Value = fastestGrowth is null ? "-" : fastestGrowth.Source;
        SummaryCards[3].Subtitle = fastestGrowth is null
            ? "Needs at least two scans"
            : $"Growth since last scan: {ByteSizeFormatter.Format(fastestGrowth.DeltaBytes)}";
    }

    private void RefreshCommandStates()
    {
        QuickScanCommand.NotifyCanExecuteChanged();
        DeepScanCommand.NotifyCanExecuteChanged();
        PreviewPlanCommand.NotifyCanExecuteChanged();
        ExecutePlanCommand.NotifyCanExecuteChanged();
        RefreshAppsCommand.NotifyCanExecuteChanged();
        RefreshHistoryCommand.NotifyCanExecuteChanged();
    }
}
