using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaInsight.Services;
using Serilog;

namespace DotaInsight.ViewModels.Dialogs;

/// <summary>
/// 清理缓存弹窗的阶段。
/// </summary>
public enum ClearCacheDialogState
{
    /// <summary>展示缓存统计，等待用户确认。</summary>
    Confirm,

    /// <summary>正在清理，显示进度条。</summary>
    Progress,

    /// <summary>已结束（成功或失败）。</summary>
    Done
}

/// <summary>
/// 清理缓存弹窗：确认 → 进度 → 结果三段式。
/// 清理由本 VM 直接驱动（<see cref="IAppCacheService"/>），宿主窗口只负责显示与关闭。
/// </summary>
public sealed partial class ClearCacheDialogViewModel : ObservableObject
{
    private readonly IAppCacheService _cacheService;
    private readonly ILogger _logger;

    /// <summary>
    /// 进度回调。<see cref="Progress{T}"/> 捕获构造它的同步上下文（UI 线程），
    /// 因此后台线程 Report 之后 <see cref="OnProgress"/> 仍回到 UI 线程执行，可安全写绑定属性。
    /// </summary>
    private readonly IProgress<AppCacheClearProgress> _progress;

    public ClearCacheDialogViewModel(IAppCacheService cacheService, ILogger logger)
    {
        _cacheService = cacheService;
        _logger = logger.ForContext<ClearCacheDialogViewModel>();
        _progress = new Progress<AppCacheClearProgress>(OnProgress);
        Stats = _cacheService.GetStats();
    }

    /// <summary>请求宿主窗口关闭（宿主据 <see cref="WasCleared"/> 决定对话框返回值）。</summary>
    public event Action? RequestClose;

    /// <summary>本次是否真的执行过清理。</summary>
    public bool WasCleared { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirm))]
    [NotifyPropertyChangedFor(nameof(IsProgress))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsSuccess))]
    [NotifyPropertyChangedFor(nameof(IsFailure))]
    [NotifyPropertyChangedFor(nameof(DoneHeadline))]
    [NotifyPropertyChangedFor(nameof(DoneSummary))]
    [NotifyPropertyChangedFor(nameof(DoneElapsedText))]
    [NotifyPropertyChangedFor(nameof(HasElapsed))]
    private ClearCacheDialogState state = ClearCacheDialogState.Confirm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconCountText))]
    [NotifyPropertyChangedFor(nameof(IconSizeText))]
    [NotifyPropertyChangedFor(nameof(DataSizeText))]
    [NotifyPropertyChangedFor(nameof(TotalSizeText))]
    [NotifyPropertyChangedFor(nameof(HasCache))]
    [NotifyPropertyChangedFor(nameof(HasNoCache))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private AppCacheStats stats = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuccess))]
    [NotifyPropertyChangedFor(nameof(IsFailure))]
    [NotifyPropertyChangedFor(nameof(DoneHeadline))]
    [NotifyPropertyChangedFor(nameof(DoneSummary))]
    [NotifyPropertyChangedFor(nameof(DoneElapsedText))]
    [NotifyPropertyChangedFor(nameof(HasElapsed))]
    private AppCacheClearResult? result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private double progressPercent;

    [ObservableProperty]
    private string stageText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressDetail))]
    private string progressDetailText = string.Empty;

    public bool IsConfirm => State == ClearCacheDialogState.Confirm;

    public bool IsProgress => State == ClearCacheDialogState.Progress;

    public bool IsDone => State == ClearCacheDialogState.Done;

    public bool IsSuccess => State == ClearCacheDialogState.Done && Result is not null;

    public bool IsFailure => State == ClearCacheDialogState.Done && Result is null;

    public bool HasCache => Stats.TotalBytes > 0 || Stats.HeroIconFiles > 0;

    public bool HasNoCache => !HasCache;

    public bool CanStart => HasCache;

    public bool HasProgressDetail => !string.IsNullOrEmpty(ProgressDetailText);

    public string IconCountText => Stats.HeroIconFiles <= 0 ? "—" : $"{Stats.HeroIconFiles} 张";

    public string IconSizeText => AppCacheStats.FormatBytes(Stats.HeroIconBytes);

    public string DataSizeText => AppCacheStats.FormatBytes(Stats.DatabaseBytes);

    public string TotalSizeText => AppCacheStats.FormatBytes(Stats.TotalBytes);

    public string ProgressPercentText => $"{ProgressPercent:F0}%";

    public string DoneHeadline => IsSuccess ? "清理完成" : "清理未完成";

    public string DoneSummary => Result is { } r
        ? $"释放 {AppCacheStats.FormatBytes(r.FreedIconBytes)}　·　头像 {r.DeletedIconFiles} 张　·　数据条目 {r.DeletedDbEntries} 条"
        : StageText;

    public bool HasElapsed => Result is { Elapsed.TotalMilliseconds: >= 100 };

    public string DoneElapsedText => Result is { } r && HasElapsed ? $"耗时 {r.Elapsed.TotalSeconds:F1} 秒" : string.Empty;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (State != ClearCacheDialogState.Confirm)
        {
            return;
        }

        ProgressPercent = 0;
        StageText = "正在准备…";
        ProgressDetailText = string.Empty;
        State = ClearCacheDialogState.Progress;

        try
        {
            Result = await _cacheService.ClearAllAsync(_progress);
            WasCleared = true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "清理本地缓存失败");
            Result = null;
            StageText = $"清理过程中出现异常：{ex.Message}";
            ProgressDetailText = string.Empty;
        }

        State = ClearCacheDialogState.Done;
    }

    [RelayCommand]
    private void Dismiss() => RequestClose?.Invoke();

    /// <summary>进度回调：在 UI 线程执行（见 <see cref="_progress"/> 的说明）。</summary>
    private void OnProgress(AppCacheClearProgress progress)
    {
        // 完成后不再回退进度：末段 Report(Done,100) 与状态切换之间可能还有零星回调
        if (State == ClearCacheDialogState.Progress)
        {
            ProgressPercent = progress.Percent;
        }

        StageText = progress.Message;

        ProgressDetailText = progress.Stage switch
        {
            AppCacheClearStage.Images when progress.Total > 0
                => $"已处理 {progress.Done} / {progress.Total} 张",
            AppCacheClearStage.Images => string.Empty,
            AppCacheClearStage.Scanning when progress.Total > 0
                => $"待清理头像 {progress.Total} 张",
            _ => string.Empty
        };
    }
}
