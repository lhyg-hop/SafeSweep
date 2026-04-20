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

    private string _statusText = "准备就绪。建议先执行快速扫描。";
    private string _planOverview = "还没有生成预演。";
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
            new SummaryCardViewModel { Title = "立即安全释放", Value = "-", Subtitle = "等待扫描" },
            new SummaryCardViewModel { Title = "建议你确认", Value = "-", Subtitle = "等待扫描" },
            new SummaryCardViewModel { Title = "官方方式处理", Value = "-", Subtitle = "等待扫描" },
            new SummaryCardViewModel { Title = "增长最快来源", Value = "-", Subtitle = "至少需要两次扫描" }
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
    public ObservableCollection<InstalledAppItemViewModel> InstalledApps { get; }
    public ObservableCollection<ActionRecordItemViewModel> ActionHistory { get; }
    public ObservableCollection<SystemGuidanceItem> GuidanceItems { get; }
    public ObservableCollection<GrowthSummaryItemViewModel> GrowthSummaries { get; }

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
        PlanOverview = "扫描完成后可以生成预演，先看风险再决定是否执行。";
        StatusText = mode == ScanMode.Quick ? "正在执行快速扫描..." : "正在执行深度扫描...";

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
                StatusText = $"已发现 {Findings.Count} 个候选项...";
            }

            var sessionId = Guid.NewGuid().ToString("N");
            await _repository.SaveScanSnapshotAsync(sessionId, mode, collectedFindings);
            await LoadGrowthAsync();
            UpdateSummaryCards();

            StatusText = mode == ScanMode.Quick
                ? $"快速扫描完成，共发现 {Findings.Count} 个候选项。"
                : $"深度扫描完成，共发现 {Findings.Count} 个候选项。";
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
            builder.AppendLine($"计划项：{CurrentPlan.Items.Count}");
            builder.AppendLine($"可执行：{executableCount}");
            builder.AppendLine($"被阻止：{blockedCount}");
            builder.AppendLine($"预计释放：{ByteSizeFormatter.Format(CurrentPlan.EstimatedFreedBytes)}");
            builder.AppendLine($"需要管理员权限：{(CurrentPlan.RequiresElevation ? "是" : "否")}");
            builder.AppendLine($"隔离区位置：{_quarantineStore.GetPreferredQuarantineRoot(Environment.SystemDirectory) ?? "未找到可用位置"}");
            builder.AppendLine($"警告数：{warnings}");
            PlanOverview = builder.ToString().TrimEnd();

            StatusText = "预演完成。请先核对释放空间、恢复方式、警告项和被阻止项，再决定是否执行。";
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
            StatusText = $"执行完成。成功 {succeeded} 项，失败 {failed} 项。";
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
            InstalledApps.Add(new InstalledAppItemViewModel(app));
        }
    }

    private async Task LoadHistoryAsync()
    {
        ActionHistory.Clear();
        foreach (var item in await _repository.GetActionRecordsAsync())
        {
            ActionHistory.Add(new ActionRecordItemViewModel(item));
        }
    }

    private async Task LoadGrowthAsync()
    {
        GrowthSummaries.Clear();
        foreach (var summary in await _repository.GetGrowthSummariesAsync())
        {
            GrowthSummaries.Add(new GrowthSummaryItemViewModel(summary));
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
            case InstalledAppItemViewModel appItem when !string.IsNullOrWhiteSpace(appItem.App.InstallLocation):
                await _systemAdapter.OpenPathAsync(appItem.App.InstallLocation!);
                break;
            case ActionRecordItemViewModel actionItem when !string.IsNullOrWhiteSpace(actionItem.Record.OriginalPath):
                await _systemAdapter.OpenPathAsync(actionItem.Record.OriginalPath!);
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
        StatusText = "已加入忽略列表。";
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
        if (parameter is InstalledAppItemViewModel app)
        {
            await _installedAppProvider.LaunchUninstallerAsync(app.App);
            StatusText = $"已启动 {app.DisplayName} 的官方卸载入口。";
        }
    }

    private async Task RestoreActionAsync(object? parameter)
    {
        if (parameter is not ActionRecordItemViewModel actionItem || !actionItem.Record.CanRestore)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _executor.RestoreAsync(actionItem.Record, ConflictPolicy.Rename);
            await LoadHistoryAsync();
            StatusText = "恢复完成。如果原路径已有同名文件，已自动改名避免覆盖。";
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
        var fastestGrowth = GrowthSummaries.OrderByDescending(item => item.Summary.DeltaBytes).FirstOrDefault();

        SummaryCards[0].Value = ByteSizeFormatter.Format(safeCleanupBytes);
        SummaryCards[0].Subtitle = "白名单规则下可直接清理的内容";
        SummaryCards[1].Value = ByteSizeFormatter.Format(reviewBytes);
        SummaryCards[1].Subtitle = "建议人工确认，默认优先保留可恢复能力";
        SummaryCards[2].Value = ByteSizeFormatter.Format(officialBytes);
        SummaryCards[2].Subtitle = "只解释和引导，不直接删除";
        SummaryCards[3].Value = fastestGrowth is null ? "-" : fastestGrowth.SourceText;
        SummaryCards[3].Subtitle = fastestGrowth is null
            ? "至少需要两次扫描"
            : $"较上次增长：{ByteSizeFormatter.Format(fastestGrowth.Summary.DeltaBytes)}";
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
