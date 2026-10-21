using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>
/// 输入区上方的会话上下文条：会话所属项目 + 已固定后端。
/// 新会话在发送第一条消息前可以改项目（决定在哪里开始工作）；
/// 后端在创建时已确定，这里只读展示。所有真实操作交给宿主窗口。
/// </summary>
public partial class ChatPage
{
    /// <summary>会话项目/后端上下文由主窗口提供；mock 时为 null，整条隐藏。</summary>
    public sealed record SessionContextInfo(
        string? ProjectId,
        string ProjectName,
        string BackendName,
        string BackendColor,
        bool CanChangeProject);

    internal Func<SessionContextInfo?>? SessionContextRequested;
    internal Func<Task>? ChangeProjectRequested;

    internal void RefreshSessionContext()
    {
        var info = SessionContextRequested?.Invoke();
        SessionContextBar.IsVisible = info is not null;
        if (info is null) return;

        SessionProjectChipText.Text = info.ProjectName;
        SessionProjectChip.IsVisible = info.CanChangeProject;
        SessionBackendChipText.Text = info.BackendName;
        SessionBackendDot.Background = Brush.Parse(string.IsNullOrWhiteSpace(info.BackendColor)
            ? "#8B5CF6" : info.BackendColor);
    }

    /// <summary>项目选择浮层：本机项目列表、“不在项目中工作”“新建项目”。</summary>
    internal async Task ShowProjectPickerAsync(IReadOnlyList<(string Id, string Name)> projects,
        string? currentId, Func<string?, Task> select)
    {
        var menu = new MenuFlyout();
        foreach (var project in projects)
        {
            var item = new MenuItem
            {
                Header = project.Name,
                Icon = new PathIcon { Data = (Geometry?)this.FindResource("Icon.Folder"), Width = 14, Height = 14 },
            };
            if (project.Id == currentId) item.Icon = new TextBlock { Text = "✓" };
            item.Click += async (_, _) => await select(project.Id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("不在项目中工作", "Icon.Dismiss", () => select(null)));
        menu.Items.Add(Item("新建项目…", "Icon.Add", async () => await ChangeProjectRequested!() ));
        menu.ShowAt(SessionProjectChip);

        MenuItem Item(string title, string iconKey, Func<Task> action)
        {
            var item = new MenuItem
            {
                Header = title,
                Icon = new PathIcon { Data = (Geometry?)this.FindResource(iconKey), Width = 14, Height = 14 },
            };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception error) { ReportActionError("切换项目失败：" + error.GetBaseException().Message); }
            };
            return item;
        }
    }
}
