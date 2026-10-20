using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.App.Features.Archive;

/// <summary>
/// 设置 → 已归档：项目与会话的归档列表，支持打开、恢复。
/// 沿用设置页的分组卡与行式条目语言；所有动作都通过 <see cref="ArchiveViewModel"/>，
/// 不在视图里直接写存储。
/// </summary>
public sealed class ArchivePage : UserControl
{
    private readonly ArchiveViewModel _vm;
    private readonly StackPanel _root = new() { Spacing = 0 };
    private readonly TextBlock _status = new()
    {
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12),
    };

    public ArchivePage(ArchiveViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _status.Classes.Add("settingsRowDesc");
        Content = _root;
        _vm.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public ArchiveViewModel ViewModel => _vm;

    /// <summary>重新读取归档列表（从概览或会话操作改变归档状态后调用）。</summary>
    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        _root.Children.Clear();

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "归档管理", Classes = { "settingsPageTitle" } });
        header.Children.Add(new TextBlock
        {
            Text = "归档只影响列表显示，不移动或删除任何会话文件。项目与其会话分开归档、分开恢复。",
            TextWrapping = TextWrapping.Wrap, Classes = { "settingsRowDesc" },
        });
        _root.Children.Add(header);
        _root.Children.Add(_status);

        var projects = _vm.LoadProjects();
        var sessions = _vm.LoadSessions();

        AddSection("已归档项目", "归档项目后，该项目及其会话不再出现在概览与最近项目中。",
            projects.Select(BuildProjectRow).ToArray(),
            projects.Count == 0 ? "没有已归档的项目" : null);
        AddSection("已归档会话", "已归档会话不再出现在侧栏与概览中；打开或恢复后回到原项目。",
            sessions.Select(BuildSessionRow).ToArray(),
            sessions.Count == 0 ? "没有已归档的会话" : null);
    }

    private void AddSection(string title, string description, IReadOnlyList<Control> rows, string? emptyText)
    {
        _root.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "settingsGroupHeader" },
            Margin = new Thickness(0, 12, 0, 6),
        });
        var panel = new Border { Classes = { "settingsPanel" } };
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "settingsRowDesc" },
            Margin = new Thickness(0, 0, 0, 4),
        });
        if (emptyText is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = emptyText,
                Classes = { "caption" },
                Margin = new Thickness(0, 8, 0, 4),
            });
        }
        else
        {
            foreach (var row in rows) stack.Children.Add(row);
        }
        panel.Child = stack;
        _root.Children.Add(panel);
    }

    private Control BuildProjectRow(ArchivedProjectEntry entry)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = entry.Name, Classes = { "bodyStrong" }, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detail = entry.SessionCount == 0
            ? entry.Folder
            : $"{entry.Folder} · {entry.SessionCount} 个会话";
        var projectDetail = new TextBlock
        {
            Text = detail, Classes = { "caption" },
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(projectDetail, entry.Folder);
        text.Children.Add(projectDetail);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(ActionButton("打开", () => _vm.OpenProjectAsync(entry)));
        actions.Children.Add(ActionButton("恢复", () =>
        {
            _vm.RestoreProject(entry);
            return Task.CompletedTask;
        }));

        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 8) };
        grid.Children.Add(text);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private Control BuildSessionRow(ArchivedSessionEntry entry)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = entry.Title, Classes = { "bodyStrong" }, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detail = entry.ProjectArchived
            ? $"{entry.ProjectName} · 项目也已归档 · {entry.LastOpened.LocalDateTime:MM-dd HH:mm}"
            : $"{entry.ProjectName} · {entry.LastOpened.LocalDateTime:MM-dd HH:mm}";
        var projectDetail = new TextBlock
        {
            Text = detail, Classes = { "caption" },
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(projectDetail, entry.ProjectPath);
        text.Children.Add(projectDetail);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(ActionButton("打开", () => _vm.OpenSessionAsync(entry)));
        actions.Children.Add(ActionButton("恢复", () =>
        {
            _vm.RestoreSession(entry);
            return Task.CompletedTask;
        }));

        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 8) };
        grid.Children.Add(text);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Classes = { "subtle" }, MinWidth = 64 };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                _status.IsVisible = false;
                await action();
            }
            catch (Exception error)
            {
                _status.Text = "操作失败：" + error.GetBaseException().Message;
                _status.IsVisible = true;
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }
}
