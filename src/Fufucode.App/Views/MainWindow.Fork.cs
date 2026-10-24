using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    private async Task ForkIntoWorktreeAsync()
    {
        if (IdleReal() is { } reason) throw new InvalidOperationException(reason);
        var host = _host!;
        var destination = Path.Combine(Fufucode.Core.AppDataLayout.DefaultRoot(),
            "worktrees", Guid.NewGuid().ToString("N"));
        _switching = true;
        _actions.RefreshCapabilities();
        try
        {
            var worktree = await GitWorktreeService.CreateAsync(host.ProjectPath, destination);
            host.Manager.SetProjectTrustDecision(worktree.Path, ProjectTrustDecision.DoNotTrust);
            var result = await host.Manager.ForkIntoProjectAsync(host.Current, worktree.Path);
            var id = result.Session.Session.Id;
            await result.Session.DisposeAsync();
            new MainWindow(_options with { ProjectPath = worktree.Path, SessionId = id }).Show();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"工作树分支失败：{error.Message}\n若工作树已经创建，将保留在 {destination}，不会自动删除。", error);
        }
        finally { _switching = false; _actions.RefreshCapabilities(); }
    }

    internal async Task ForkFromMessageAsync(TimelineEntryViewModel? source = null, bool retry = false, Views.ChatPage? origin = null)
    {
        var host = _host ?? throw new InvalidOperationException("Mock 没有持久 Pi entry，无法分叉");
        var side = ReferenceEquals(origin, _sideChatPage);
        var activeVm = side ? _sideChatPage.DataContext as MainViewModel : _vm;
        var session = side ? _sideSession : host.Current;
        if (session is null) throw new InvalidOperationException("会话已关闭");
        if (activeVm?.Input.IsTurnActive == true) throw new InvalidOperationException("请等待本轮完全结束后分叉");
        await session.RefreshHistoryAsync();
        var entries = session.History.Entries.Where(e => e.OnCurrentBranch && e.Message?.Role == "user").ToArray();
        var selected = -1;
        if (source is not null && activeVm is { } vm)
        {
            var before = vm.Timeline.Entries.TakeWhile(e => e != source).OfType<UserMessageViewModel>().ToList();
            var user = source as UserMessageViewModel ?? before.LastOrDefault();
            if (source is UserMessageViewModel) before.Add(user!);
            var index = before.Count - 1;
            if (user is not null && index >= 0 && index < entries.Length &&
                string.Join("\n", entries[index].Message!.Content.OfType<PiTextBlock>().Select(b => b.Text)) == user.Text) selected = index;
        }
        var picker = new ListBox { ItemsSource = entries.Select(e => string.Join("\n", e.Message!.Content.OfType<PiTextBlock>().Select(b => b.Text))).ToArray(), SelectedIndex = selected };
        var confirm = new Button { Content = retry ? "分叉并重试" : "从此请求分叉", IsEnabled = selected >= 0 };
        var cancel = new Button { Content = "取消", IsCancel = true };
        picker.SelectionChanged += (_, _) => confirm.IsEnabled = picker.SelectedIndex >= 0;
        var dialog = new Window { Title = retry ? "重试可能再次执行工具，请确认" : "选择真实 Pi 分叉点", Width = 620, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20), RowSpacing = 12 };
        grid.Children.Add(picker);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm } };
        Grid.SetRow(row, 1); grid.Children.Add(row); dialog.Content = grid;
        cancel.Click += (_, _) => dialog.Close(-1); confirm.Click += (_, _) => dialog.Close(picker.SelectedIndex);
        var response = await Fufucode.App.Views.SkinDialog.ShowAsync<int?>(dialog, this);
        if (response is not { } choice || choice < 0 || choice >= entries.Length) return;
        var result = side ? await host.Manager.ForkAsync(session, entries[choice].Id) : await host.ForkAsync(entries[choice].Id);
        if (side)
        {
            if (activeVm is not null) { activeVm.WorkspacePanel.Dispose(); activeVm.Sidebar.Dispose(); }
            _sideSession = result.Session; AttachSideSession(result.Session);
            (_sideChatPage.DataContext as MainViewModel)!.Input.DraftText = host.Manager.Metadata.GetDraft(result.Session.Session.Id) ?? "";
        }
        else AttachRealSession();
        var nextVm = side ? _sideChatPage.DataContext as MainViewModel : _vm;
        if (retry && !string.IsNullOrWhiteSpace(nextVm?.Input.DraftText)) await nextVm.Input.SendCommand.ExecuteAsync(null);
    }
}
