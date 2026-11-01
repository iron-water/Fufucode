using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fufucode.Core.Automation.Rebirth;
using Fufucode.Plugins.AutoDev;

namespace Fufucode.App.Features.Automation;

/// <summary>
/// Minimal project-kernel surface. It reads and writes the same rebirth SQLite database as the
/// AutoDev tools; it does not start a model or silently mark a ticket complete.
/// </summary>
internal sealed class RebirthKernelWindow : Window
{
    private readonly string _database;
    private readonly string _projectId;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBox _goal = new()
    {
        AcceptsReturn = true,
        MinHeight = 58,
        TextWrapping = TextWrapping.Wrap,
        PlaceholderText = "项目目标，例如：修复会话切换白屏"
    };
    private readonly ListBox _tickets = new() { MinHeight = 180 };

    public RebirthKernelWindow(string dataRoot, string projectPath)
    {
        _database = RebirthPaths.Database(dataRoot, projectPath);
        _projectId = RebirthPaths.ProjectId(projectPath);
        Title = "项目内核";
        Width = 780;
        Height = 580;
        MinWidth = 520;
        MinHeight = 420;

        var root = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "项目内核",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var refresh = new Button { Content = "刷新", Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += (_, _) => Load();
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        root.Children.Add(header);

        var details = new StackPanel { Spacing = 4 };
        details.Children.Add(_status);
        details.Children.Add(_summary);
        details.Children.Add(new TextBlock
        {
            Text = "项目和工单在此登记；执行、证据和否决由 AutoDev 工具写入同一内核。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var listPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listPanel.Children.Add(new TextBlock { Text = "工单", FontWeight = FontWeight.SemiBold });
        var scroll = new ScrollViewer
        {
            Content = _tickets,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        listPanel.Children.Add(scroll);
        Grid.SetRow(listPanel, 2);
        root.Children.Add(listPanel);

        var createPanel = new StackPanel { Spacing = 6 };
        createPanel.Children.Add(new TextBlock
        {
            Text = "在 Project Brief 内建立工单（WithinMandate）",
            FontSize = 12,
            Opacity = 0.7
        });
        createPanel.Children.Add(_goal);
        var create = new Button { Content = "建立项目 / 添加工单", HorizontalAlignment = HorizontalAlignment.Left };
        create.Click += (_, _) => Create();
        createPanel.Children.Add(create);
        Grid.SetRow(createPanel, 3);
        root.Children.Add(createPanel);

        Content = root;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var kernel = new RebirthKernel(_database);
            var snapshot = kernel.ReadSnapshot(_projectId);
            var openAttention = snapshot.Attention.Count(item => item.Status == RebirthAttentionStatus.Open);
            var activeVetoes = snapshot.Decisions.Count(item =>
                item.Kind == RebirthDecisionKind.Veto && item.Status == "Active");
            _status.Text =
                $"项目 {snapshot.Project.Status} · Brief v{snapshot.Project.BriefVersion} · " +
                $"{snapshot.Tickets.Count} 个工单 · {openAttention} 项待回答 · {activeVetoes} 条有效否决";
            _summary.Text =
                $"目标：{snapshot.Brief.Brief.Statement}\n" +
                $"数据库：{_database}";
            _tickets.ItemsSource = snapshot.Tickets
                .OrderBy(item => item.CreatedUtc)
                .Select(item => $"{item.Status} · {item.Title} · {item.Id}")
                .ToArray();
        }
        catch (KeyNotFoundException)
        {
            _status.Text = "项目尚未建立 Brief。";
            _summary.Text = "填写一个目标后点击“建立项目 / 添加工单”。";
            _tickets.ItemsSource = Array.Empty<string>();
        }
        catch (Exception error)
        {
            _status.Text = "读取失败：" + error.Message;
            _tickets.ItemsSource = Array.Empty<string>();
        }
    }

    private void Create()
    {
        var goal = _goal.Text?.Trim();
        if (string.IsNullOrWhiteSpace(goal))
        {
            _status.Text = "先填写一个具体目标。";
            return;
        }
        try
        {
            using var kernel = new RebirthKernel(_database);
            RebirthSnapshot? existing = null;
            try { existing = kernel.ReadSnapshot(_projectId); }
            catch (KeyNotFoundException) { }
            if (existing is null)
            {
                var brief = new RebirthBrief(goal, new[] { goal }, Array.Empty<string>(),
                    Array.Empty<string>(), new[] { goal }, Array.Empty<string>(), 600,
                    RebirthAutonomy.WithinMandate);
                kernel.CreateProject("ui:project:" + RebirthPaths.Stable(goal), goal, brief, "human",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _projectId);
            }
            var ticketId = "ticket-" + RebirthPaths.Stable(goal);
            try
            {
                kernel.CreateTicket("ui:ticket:" + RebirthPaths.Stable(goal), _projectId, goal, goal,
                    new[] { goal }, "default", "auto-dev",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ticketId: ticketId);
            }
            catch (InvalidOperationException)
            {
                _status.Text = "该目标对应的工单已存在。";
                Load();
                return;
            }
            _goal.Text = "";
            Load();
        }
        catch (Exception error)
        {
            _status.Text = "创建失败：" + error.Message;
        }
    }
}
