using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Core.Automation.Rebirth;
using Fufucode.Plugins.AutoDev;

namespace Fufucode.App.Features.Automation;

public sealed record RebirthKernelTicketRow(
    string Id, string Title, string Status, string Owner, string Acceptance, string Route,
    string TaskClass, string Priority, string? ResumeWhen)
{
    public string TitleLine => "[" + Priority + "] " + TaskClass + " · " + Status + " · " + Title;
    public string DetailLine => "验收：" + Acceptance + " · 路线：" + Route + " · 负责人：" + Owner +
        (string.IsNullOrWhiteSpace(ResumeWhen) ? "" : " · 等待：" + ResumeWhen);
}

public sealed record RebirthKernelAttentionRow(string Id, string TicketId, string Question, string Status)
{
    public string TitleLine => Question;
    public string DetailLine => "工单 " + TicketId + " · " + Status;
}

public sealed record RebirthKernelVetoRow(string Id, string Target, string Text, string Status)
{
    public string TitleLine => Text;
    public string DetailLine => Target + " · " + Status;
}

/// <summary>
/// The kernel-backed main page. Reads/writes only rebirth tables; the legacy AutomationStore
/// remains available on the old tabs but is not the source of this view.
/// </summary>
public sealed partial class RebirthKernelViewModel : ObservableObject
{
    private readonly string _dataRoot;
    private string? _projectPath;
    private string? _goalDraft;
    private string? _answerDraft;
    private RebirthKernelTicketRow? _selectedTicket;
    private RebirthKernelAttentionRow? _selectedAttention;
    private string _statusText = "尚未选择项目。";
    private string _summaryText = "";
    private bool _hasProject;

    public RebirthKernelViewModel(string dataRoot)
    {
        _dataRoot = dataRoot;
        Tickets = new ObservableCollection<RebirthKernelTicketRow>();
        Attention = new ObservableCollection<RebirthKernelAttentionRow>();
        Vetoes = new ObservableCollection<RebirthKernelVetoRow>();
    }

    public ObservableCollection<RebirthKernelTicketRow> Tickets { get; }
    public ObservableCollection<RebirthKernelAttentionRow> Attention { get; }
    public ObservableCollection<RebirthKernelVetoRow> Vetoes { get; }

    public string? GoalDraft
    {
        get => _goalDraft ?? "";
        set { if (_goalDraft == value) return; _goalDraft = value; OnPropertyChanged(); }
    }

    public string? AnswerDraft
    {
        get => _answerDraft ?? "";
        set { if (_answerDraft == value) return; _answerDraft = value; OnPropertyChanged(); }
    }

    public RebirthKernelTicketRow? SelectedTicket
    {
        get => _selectedTicket;
        set { if (_selectedTicket == value) return; _selectedTicket = value; OnPropertyChanged(); StopSelectedCommand.NotifyCanExecuteChanged(); TakeoverSelectedCommand.NotifyCanExecuteChanged(); }
    }

    public RebirthKernelAttentionRow? SelectedAttention
    {
        get => _selectedAttention;
        set { if (_selectedAttention == value) return; _selectedAttention = value; OnPropertyChanged(); AnswerSelectedCommand.NotifyCanExecuteChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set { if (_summaryText == value) return; _summaryText = value; OnPropertyChanged(); }
    }

    public bool HasProject
    {
        get => _hasProject;
        private set { if (_hasProject == value) return; _hasProject = value; OnPropertyChanged(); }
    }

    public string DatabasePath => string.IsNullOrWhiteSpace(_projectPath)
        ? "" : RebirthPaths.Database(_dataRoot, _projectPath);

    public void Configure(string? projectPath)
    {
        if (string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase)) { Refresh(); return; }
        _projectPath = projectPath;
        SelectedTicket = null;
        SelectedAttention = null;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Tickets.Clear();
        Attention.Clear();
        Vetoes.Clear();
        SelectedTicket = null;
        SelectedAttention = null;
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            HasProject = false;
            StatusText = "尚未选择项目。";
            SummaryText = "";
            return;
        }
        try
        {
            using var kernel = new RebirthKernel(DatabasePath);
            var snapshot = kernel.ReadSnapshot(RebirthPaths.ProjectId(_projectPath));
            HasProject = true;
            StatusText = "项目 " + snapshot.Project.Status + " · Brief v" + snapshot.Project.BriefVersion;
            SummaryText = snapshot.Brief.Brief.Statement + "\n" + snapshot.Tickets.Count + " 个工单 · " +
                          snapshot.Attention.Count(item => item.Status == RebirthAttentionStatus.Open) + " 项待回答 · " +
                          snapshot.Decisions.Count(item => item.Kind == RebirthDecisionKind.Veto && item.Status == "Active") + " 条有效否决";
            foreach (var ticket in snapshot.Tickets.OrderBy(item => item.CreatedUtc))
                Tickets.Add(new RebirthKernelTicketRow(ticket.Id, ticket.Title, ticket.Status.ToString(), ticket.Owner,
                    string.Join("；", ticket.Acceptance), ticket.Route, ticket.TaskClass.ToString(),
                    ticket.Priority.ToString(), ticket.ResumeWhen));
            foreach (var item in snapshot.Attention.OrderBy(item => item.CreatedUtc))
                Attention.Add(new RebirthKernelAttentionRow(item.Id, item.TicketId ?? "", item.Question, item.Status.ToString()));
            foreach (var item in snapshot.Decisions.Where(item => item.Kind == RebirthDecisionKind.Veto).OrderBy(item => item.CreatedUtc))
                Vetoes.Add(new RebirthKernelVetoRow(item.Id, item.Target, item.Text, item.Status));
        }
        catch (KeyNotFoundException)
        {
            HasProject = false;
            StatusText = "项目尚未建立 Brief。";
            SummaryText = "填写一个目标后点击“建立项目 / 添加工单”。";
        }
        catch (Exception error)
        {
            HasProject = false;
            StatusText = "读取失败：" + error.Message;
            SummaryText = "";
        }
    }

    private bool CanMutateSelected() => SelectedTicket is not null;
    private bool CanAnswer() => SelectedAttention is not null;

    [RelayCommand]
    public void CreatePlan()
    {
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            StatusText = "先打开一个项目。";
            return;
        }
        var goal = GoalDraft?.Trim();
        if (string.IsNullOrWhiteSpace(goal))
        {
            StatusText = "先填写一个具体目标。";
            return;
        }
        try
        {
            using var kernel = new RebirthKernel(DatabasePath);
            var projectId = RebirthPaths.ProjectId(_projectPath);
            RebirthSnapshot? existing = null;
            try { existing = kernel.ReadSnapshot(projectId); }
            catch (KeyNotFoundException) { }
            if (existing is null)
            {
                var brief = new RebirthBrief(goal, new[] { goal }, Array.Empty<string>(),
                    Array.Empty<string>(), new[] { goal }, Array.Empty<string>(), 600,
                    RebirthAutonomy.WithinMandate);
                kernel.CreateProject("ui:project:" + RebirthPaths.Stable(goal), goal, brief, "human",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), projectId);
            }
            var ticketId = "ticket-" + RebirthPaths.Stable(goal);
            try
            {
                kernel.CreateTicket("ui:ticket:" + RebirthPaths.Stable(goal), projectId, goal, goal,
                    new[] { goal }, "default", "auto-dev", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ticketId: ticketId);
            }
            catch (InvalidOperationException)
            {
                StatusText = "该目标对应的工单已存在。";
                Refresh();
                return;
            }
            GoalDraft = "";
            Refresh();
        }
        catch (Exception error)
        {
            StatusText = "创建失败：" + error.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    public void StopSelected()
    {
        if (SelectedTicket is not { } selected) return;
        try
        {
            using var kernel = new RebirthKernel(DatabasePath);
            kernel.StopTicket("ui:stop:" + selected.Id, selected.Id, "human stop from kernel view", "human",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Refresh();
        }
        catch (Exception error) { StatusText = "停止失败：" + error.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    public void TakeoverSelected()
    {
        if (SelectedTicket is not { } selected) return;
        try
        {
            using var kernel = new RebirthKernel(DatabasePath);
            kernel.TakeoverTicket("ui:takeover:" + selected.Id, selected.Id, "human",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Refresh();
        }
        catch (Exception error) { StatusText = "接管失败：" + error.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    public void DecideGate(string decision)
    {
        if (SelectedTicket is not { } selected) return;
        if (!Enum.TryParse<RebirthGateDecision>(decision, true, out var gate))
        { StatusText = "未知的 Gate 决定。"; return; }
        try
        {
            using var kernel = new RebirthKernel(DatabasePath);
            kernel.RecordGateDecision("ui:gate:" + selected.Id + ":" + decision, RebirthPaths.ProjectId(_projectPath!),
                selected.Id, gate, "selected user_gate", "kernel view decision", "human",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Refresh();
        }
        catch (Exception error) { StatusText = "Gate 决定失败：" + error.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    public void AnswerSelected()
    {
        if (SelectedAttention is not { } selected) return;
        var answer = AnswerDraft?.Trim();
        if (string.IsNullOrWhiteSpace(answer)) { StatusText = "先填写回答。"; return; }
        try
        {
            using var kernel = new RebirthKernel(DatabasePath);
            kernel.AnswerAttention("ui:answer:" + selected.Id, selected.Id, answer,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            AnswerDraft = "";
            Refresh();
        }
        catch (Exception error) { StatusText = "回答失败：" + error.Message; }
    }
}
