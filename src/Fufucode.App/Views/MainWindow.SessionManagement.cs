using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow : ISessionManagement
{
    private readonly RelayCommandHost _renameSession = new();
    private readonly RelayCommandHost _pinSession = new();
    private readonly RelayCommandHost _archiveSession = new();
    private SessionItemViewModel? _managedSession;

    ICommand? ISessionManagement.Rename => _renameSession.Command;
    ICommand? ISessionManagement.TogglePin => _pinSession.Command;
    ICommand? ISessionManagement.Archive => _archiveSession.Command;

    private void InitializeSessionManagement()
    {
        _renameSession.Execute = async _ => await RenameManagedSessionAsync();
        _pinSession.Execute = _ => ToggleManagedPin();
        _archiveSession.Execute = async _ =>
        {
            try { await ArchiveManagedSessionAsync(); }
            catch (Exception error) { _vm?.Timeline.AddNotification("归档失败：" + error.Message); }
        };
        _renameSession.CanExecute = CanManageSession;
        _pinSession.CanExecute = CanManageSession;
        _archiveSession.CanExecute = CanManageSession;
        if (_vm?.Sidebar is { } sidebar)
        {
            sidebar.AttachSessionManagement(this);
            sidebar.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(SidebarViewModel.SelectedSession)) return;
                _managedSession = sidebar.SelectedSession;
                _renameSession.RaiseCanExecuteChanged();
                _pinSession.RaiseCanExecuteChanged();
                _archiveSession.RaiseCanExecuteChanged();
            };
        }
    }

    /// <summary>会话管理页当前指向的会话；未选中时不提供操作。</summary>
    private SessionItemViewModel? ManagedSession
    {
        get
        {
            _managedSession = _vm?.Sidebar.SelectedSession;
            return _managedSession;
        }
    }

    private bool CanManageSession() => _host is not null && ManagedSession?.Id is { Length: > 0 };

    bool ISessionManagement.IsPinned(string? sessionId) =>
        _host is not null && sessionId is { Length: > 0 } &&
        SessionPresentation.Load(_host.Manager.Metadata, sessionId).Pinned;

    private void ToggleManagedPin()
    {
        if (_host is null || ManagedSession?.Id is not { } id) return;
        var current = SessionPresentation.Load(_host.Manager.Metadata, id);
        SessionPresentation.Save(_host.Manager.Metadata, id, current with { Pinned = !current.Pinned });
        RefreshSessionList();
    }

    private async Task RenameManagedSessionAsync()
    {
        if (_host is null || ManagedSession?.Id is not { } id) return;
        var session = _host.Sessions.FirstOrDefault(s => s.Id == id);
        if (session is null) return;
        var box = new TextBox { Text = session.Name ?? "", MinWidth = 280, MaxLength = 120 };
        var save = new Button { Content = "保存", Classes = { "accent" }, IsDefault = true, MinWidth = 88 };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        var dialog = Views.AppDialog.Create("重命名会话", 460);
        var body = new StackPanel
        {
            Spacing = 14,
            Children = { Views.AppDialog.Field("会话名称", box, "名称会显示在侧栏与会话管理中。") },
        };
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto") };
        root.Children.Add(Views.AppDialog.Header("Icon.Edit", "重命名会话",
            "给这个会话一个容易识别的名字。"));
        var content = Views.AppDialog.Body(Views.AppDialog.Card(body));
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        var footer = Views.AppDialog.Footer(cancel, save);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        dialog.Content = root;
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        cancel.Click += (_, _) => dialog.Close();
        void Commit()
        {
            var name = (box.Text ?? "").Trim();
            dialog.Close();
            if (name.Length > 0) _ = RenameAndRefreshAsync(name, id);
        }
        save.Click += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            e.Handled = true;
            Commit();
        };
        await Fufucode.App.Views.SkinDialog.ShowAsync(dialog, this);
    }

    private async Task RenameAndRefreshAsync(string name, string id)
    {
        try
        {
            if (_host is null) return;
            if (id != _host.Current.Session.Id) await SwitchSessionAsync(id);
            await _host.RenameCurrentAsync(name);
            RefreshSessionList();
        }
        catch (Exception error) { _vm?.Timeline.AddNotification("重命名失败：" + error.Message); }
    }

    private async Task ArchiveManagedSessionAsync()
    {
        if (_host is null || ManagedSession?.Id is not { } id) return;
        var current = SessionPresentation.Load(_host.Manager.Metadata, id);
        SessionPresentation.Save(_host.Manager.Metadata, id, current with { Archived = true });
        if (id == _host.Current.Session.Id)
        {
            var archivedProjects = ProjectArchive.LoadSet(_host.Manager.Metadata);
            var next = _host.Sessions.FirstOrDefault(s => s.Id != id &&
                !SessionArchive.IsArchived(_host.Manager.Metadata, s, archivedProjects));
            if (next is null) await SwitchSessionAsync(null); else await SwitchSessionAsync(next.Id);
        }
        _archivePage?.Refresh();
        RefreshSessionList();
    }

    private void RefreshSessionList()
    {
        if (_host is null || _vm is null) return;
        _vm.Sidebar.UpdateSessions(BuildSessionItems(_host), _host.Current.Session.Id, _host.Current.Session.Name ?? "");
        _vm.Sidebar.AttachSessionManagement(this);
        RenderOverview();
    }

    /// <summary>极简命令宿主：让会话管理页的按钮按需启用。</summary>
    private sealed class RelayCommandHost
    {
        private Func<bool> _canExecute = () => true;
        private Action<object?>? _execute;
        private readonly SimpleCommand _command;
        public RelayCommandHost() => _command = new SimpleCommand(this);
        public event EventHandler? CanExecuteChanged;
        public Action<object?>? Execute { get => _execute; set { _execute = value; RaiseCanExecuteChanged(); } }
        public Func<bool> CanExecute { get => _canExecute; set { _canExecute = value ?? (() => true); RaiseCanExecuteChanged(); } }
        public ICommand Command => _command;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        private sealed class SimpleCommand : ICommand
        {
            private readonly RelayCommandHost _owner;
            public SimpleCommand(RelayCommandHost owner) { _owner = owner; _owner.CanExecuteChanged += (_, _) => CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
            public event EventHandler? CanExecuteChanged;
            public bool CanExecute(object? parameter) => _owner._canExecute() && _owner._execute is not null;
            public void Execute(object? parameter) => _owner._execute?.Invoke(parameter);
        }
    }
}
