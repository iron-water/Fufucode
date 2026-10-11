using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Core;
using Fufucode.Pi;

namespace Fufucode.App.ViewModels;

public enum UsageDetailScope { CurrentSession, CurrentTurn, AllRecorded }
public sealed record UsageQuerySnapshot(SessionUsageReport Report, string Scope, bool PartialTail, DateTimeOffset QueriedAt);

public sealed record UsageCounterViewModel(string Label, UsageCounter Counter, bool IsPartial)
{
    public bool IsUnknown => IsPartial || Counter.Value is null;
    public string ValueText => IsUnknown ? "暂无完整数据" : Number(Counter.Value!.Value);
    public string KnownSubtotalText => IsUnknown && Counter.KnownSubtotal is { } subtotal
        ? "已知小计 " + Number(subtotal) + "（不完整）" : string.Empty;
    public bool HasKnownSubtotal => KnownSubtotalText.Length > 0;
    public string CountText => $"已知 {Counter.KnownMessageCount} 条响应 · 未知 {Counter.UnknownMessageCount} 条";
    internal static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
public sealed record UsageTurnViewModel(int TurnNumber, string? UserEntryId, UsageCounter Counter, bool IsPartial)
{
    public UsageTotals? Totals { get; init; }
    public string Title => TurnNumber == 0 ? "首条用户消息之前" : $"第 {TurnNumber} 轮";
    public string ValueText => IsPartial || Counter.Value is null ? "暂无完整数据" : UsageCounterViewModel.Number(Counter.Value.Value);
    public string Detail => Totals is { } totals
        ? $"输入 {Format(totals.Input)} · 输出 {Format(totals.Output)} · 缓存读 {Format(totals.CacheRead)} · 缓存写 {Format(totals.CacheWrite)}"
        : "暂无细分数据";
    private string Format(UsageCounter value) => !IsPartial && value.Value is { } known ? UsageCounterViewModel.Number(known) : "未知";
    public bool HasUserEntry => UserEntryId is not null;
}

/// <summary>A8 snapshot projection; the UI never sums streaming updates.</summary>
public sealed partial class UsageDetailsViewModel : ViewModelBase, IDisposable
{
    private Func<CancellationToken, Task<UsageQuerySnapshot>>? _live;
    private Func<CancellationToken, Task<UsageQuerySnapshot>>? _recorded;
    private CancellationTokenSource? _queryCts;
    private int _contextVersion;
    private int _queryVersion;
    private bool _disposed;
    [ObservableProperty] private string _sessionId = string.Empty;
    [ObservableProperty] private bool _isMock;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _selectedScopeIndex;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _queryError;
    [ObservableProperty] private string _sourceScope = "尚未查询";
    [ObservableProperty] private string _updatedText = string.Empty;
    [ObservableProperty] private string _completenessText = "暂无完整数据";
    [ObservableProperty] private string _cacheShareText = "缓存占比未知";
    public ObservableCollection<UsageCounterViewModel> Counters { get; } = new();
    public ObservableCollection<UsageTurnViewModel> Turns { get; } = new();
    public bool HasQueryError => QueryError is not null;
    public bool IsCurrentSession => SelectedScopeIndex == 0;
    public bool IsCurrentTurn => SelectedScopeIndex == 1;
    public bool IsAllRecorded => SelectedScopeIndex == 2;
    public bool HasTurns => Turns.Count > 0;
    public string SourceLabel => IsMock ? "mock 用量演示 · 未连接真实 Pi" : "Pi 报告的 token 用量";
    public string ScopeDescription => SelectedScopeIndex switch
    {
        1 => "当前分支的最近一轮；仅计已结束的 assistant 响应。",
        2 => "此会话全部已记录分支，含压缩前保留记录；继承消息也计入记录。",
        _ => "当前分支会话累计；不代表压缩前记录或其他分支。",
    };
    public event Action? CloseRequested;
    partial void OnIsMockChanged(bool value) => OnPropertyChanged(nameof(SourceLabel));
    partial void OnQueryErrorChanged(string? value) => OnPropertyChanged(nameof(HasQueryError));
    partial void OnSelectedScopeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCurrentSession)); OnPropertyChanged(nameof(IsCurrentTurn));
        OnPropertyChanged(nameof(IsAllRecorded)); OnPropertyChanged(nameof(ScopeDescription));
        _ = RefreshAsync();
    }
    public void AttachContext(string sessionId, Func<CancellationToken, Task<UsageQuerySnapshot>> live,
        Func<CancellationToken, Task<UsageQuerySnapshot>> recorded, bool isMock = false)
    {
        _contextVersion++; _queryVersion++; _queryCts?.Cancel();
        SessionId = sessionId; IsMock = isMock; _live = live; _recorded = recorded;
        QueryError = null; IsLoading = false; SourceScope = "尚未查询"; UpdatedText = string.Empty;
        CompletenessText = "暂无完整数据"; CacheShareText = "缓存占比未知"; Counters.Clear(); Turns.Clear();
        OnPropertyChanged(nameof(HasTurns));
        if (IsOpen) _ = RefreshAsync();
    }
    public void AttachRealContext(AppDataLayout layout, string projectPath, string sessionId, ChatSessionCoordinator coordinator)
    {
        AttachContext(sessionId, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                return Task.FromResult(new UsageQuerySnapshot(coordinator.GetUsage(), "live-session-current-branch", false, DateTimeOffset.Now));
            }, cancellation => Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var source = ReadOnlySessionReport.Load(layout, projectPath, sessionId);
                cancellation.ThrowIfCancellationRequested();
                return new UsageQuerySnapshot(source.History.GetUsage(), "recorded-session-all-branches", source.PartialTail, DateTimeOffset.Now);
            }, cancellation));
    }
    public void AttachMock(string sessionId = "mock-session")
    {
        var user = new PiMessage("user", [], null, null, null, default);
        var measured = new PiMessage("assistant", [], "stop", null, new PiUsage(4200, 860, 1180, 0, 6240, default), default);
        var unknown = new PiMessage("assistant", [], "stop", null, new PiUsage(null, 240, null, null, null, default), default);
        var report = SessionUsageQuery.Query([user, measured, user, unknown]);
        AttachContext(sessionId, _ => Task.FromResult(new UsageQuerySnapshot(report, "live-session-current-branch", false, DateTimeOffset.Now)),
            _ => Task.FromResult(new UsageQuerySnapshot(report, "recorded-session-all-branches", false, DateTimeOffset.Now)), true);
    }
    public void Open() { IsOpen = true; _ = RefreshAsync(); }
    [RelayCommand] private void Close() { IsOpen = false; CloseRequested?.Invoke(); }
    [RelayCommand] private void CurrentSession() => SelectedScopeIndex = 0;
    [RelayCommand] private void CurrentTurn() => SelectedScopeIndex = 1;
    [RelayCommand] private void AllRecorded() => SelectedScopeIndex = 2;
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed || _live is null || _recorded is null) return;
        _queryCts?.Cancel(); _queryCts?.Dispose();
        var cancellation = _queryCts = new CancellationTokenSource();
        var queryVersion = ++_queryVersion; var contextVersion = _contextVersion; var scope = SelectedScopeIndex;
        var query = scope == 2 ? _recorded : _live;
        IsLoading = true; QueryError = null;
        try
        {
            var snapshot = await query(cancellation.Token);
            if (_disposed || queryVersion != _queryVersion || contextVersion != _contextVersion) return;
            ApplySnapshot(snapshot, (UsageDetailScope)scope);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && queryVersion == _queryVersion && contextVersion == _contextVersion)
            {
                QueryError = "读取用量失败：" + error.GetBaseException().Message;
                Counters.Clear(); Turns.Clear(); SourceScope = "查询失败"; UpdatedText = string.Empty;
                CompletenessText = "暂无完整数据"; CacheShareText = "缓存占比未知"; OnPropertyChanged(nameof(HasTurns));
            }
        }
        finally { if (!_disposed && queryVersion == _queryVersion && contextVersion == _contextVersion) IsLoading = false; }
    }
    public void ApplySnapshot(UsageQuerySnapshot snapshot, UsageDetailScope scope)
    {
        var turn = snapshot.Report.Turns.LastOrDefault();
        var totals = scope == UsageDetailScope.CurrentTurn
            ? turn?.Usage ?? SessionUsageQuery.Query(Array.Empty<PiMessage>()).Session : snapshot.Report.Session;
        var excluded = scope == UsageDetailScope.CurrentTurn ? turn?.ExcludedActiveAssistantCount ?? 0 : snapshot.Report.ExcludedActiveAssistantCount;
        var incomplete = snapshot.PartialTail || excluded > 0;
        SourceScope = scope == UsageDetailScope.CurrentTurn ? "live-turn-current-branch" : snapshot.Scope;
        UpdatedText = "统计时间 " + snapshot.QueriedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        CompletenessText = snapshot.PartialTail ? "记录尾部尚未写完 · 仅显示已完成前缀的已知小计（不完整）"
            : excluded > 0 ? $"{excluded} 条进行中的 assistant 响应已排除 · 已知小计不完整"
            : new[] { totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite, totals.TotalTokens }.Any(value => value.Value is null)
                ? "缺少部分用量字段 · 已知小计不完整，未知值不会换成 0"
                : $"已记录 {totals.AssistantMessageCount} 条 assistant 响应；所选范围内字段完整";
        Counters.Clear();
        CacheShareText = !incomplete && totals.Input.Value is { } input && totals.CacheRead.Value is { } read && totals.CacheWrite.Value is { } write && (decimal)input + read + write > 0
            ? $"输入侧缓存读取占比 {read / ((decimal)input + read + write):P1}" : "输入侧缓存读取占比未知";
        Counters.Add(new("输入 tokens", totals.Input, incomplete)); Counters.Add(new("输出 tokens", totals.Output, incomplete));
        Counters.Add(new("缓存读取", totals.CacheRead, incomplete)); Counters.Add(new("缓存写入", totals.CacheWrite, incomplete));
        Counters.Add(new("总 tokens（Pi 原始字段）", totals.TotalTokens, incomplete));
        Turns.Clear();
        foreach (var item in scope == UsageDetailScope.CurrentTurn ? snapshot.Report.Turns.TakeLast(1) : snapshot.Report.Turns)
            Turns.Add(new(item.TurnNumber, item.UserEntryId, item.Usage.TotalTokens, snapshot.PartialTail || item.ExcludedActiveAssistantCount > 0) { Totals = item.Usage });
        OnPropertyChanged(nameof(HasTurns));
    }
    public void Dispose() { _disposed = true; _queryCts?.Cancel(); _queryCts?.Dispose(); }
}
