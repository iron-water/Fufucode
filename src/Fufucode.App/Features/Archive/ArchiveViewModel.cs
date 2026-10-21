using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.App.Infrastructure;
using Fufucode.Core;

namespace Fufucode.App.Features.Archive;

/// <summary>已归档项目的一行。</summary>
public sealed record ArchivedProjectEntry(
    string Id, string Name, string Folder, int SessionCount, DateTimeOffset LastOpened);

/// <summary>已归档会话的一行；ProjectArchived 表示其所属项目也处于归档状态。</summary>
public sealed record ArchivedSessionEntry(
    string Id, string Title, string ProjectName, string ProjectPath, bool ProjectArchived,
    DateTimeOffset LastOpened);

/// <summary>
/// 归档管理（对标 Codex 的归档项目/归档会话）：项目与会话分开归档、分开恢复。
/// 归档只写桌面元数据，任何会话文件、项目目录与历史都不会被移动或删除。
/// </summary>
public sealed class ArchiveViewModel
{
    private readonly DesktopMetadataStore _store;
    private readonly Func<SessionRecord, Task> _openSession;
    private readonly Func<string, Task> _openProject;
    private readonly Action? _afterChange;

    public ArchiveViewModel(DesktopMetadataStore store, Func<SessionRecord, Task> openSession,
        Func<string, Task> openProject, Action? afterChange = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _openSession = openSession ?? throw new ArgumentNullException(nameof(openSession));
        _openProject = openProject ?? throw new ArgumentNullException(nameof(openProject));
        _afterChange = afterChange;
    }

    /// <summary>恢复/打开后触发，供概览与侧栏同步刷新。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ArchivedProjectEntry> LoadProjects()
    {
        var organization = new SessionOrganization(_store);
        var entries = new List<ArchivedProjectEntry>();
        foreach (var id in ProjectArchive.Load(_store))
        {
            ProjectRecord project;
            try { project = _store.GetProject(id); }
            catch (KeyNotFoundException) { continue; }
            var options = organization.Project(project);
            var sessions = _store.ListSessions(project.Id);
            var lastOpened = sessions.Count == 0 ? DateTimeOffset.MinValue : sessions.Max(s => s.LastOpened);
            entries.Add(new ArchivedProjectEntry(project.Id, options.Name, options.Folder,
                sessions.Count, lastOpened));
        }
        return entries;
    }

    public IReadOnlyList<ArchivedSessionEntry> LoadSessions()
    {
        var projects = _store.ListProjects().ToDictionary(project => project.Id);
        var archivedProjects = ProjectArchive.LoadSet(_store);
        var organization = new SessionOrganization(_store);
        var entries = new List<ArchivedSessionEntry>();
        foreach (var project in projects.Values)
        {
            var projectName = organization.Project(project).Name;
            foreach (var session in _store.ListSessions(project.Id))
            {
                if (!SessionPresentation.Load(_store, session.Id).Archived) continue;
                var title = SessionTitles.Display(session);
                entries.Add(new ArchivedSessionEntry(session.Id, title, projectName, project.Path,
                    archivedProjects.Contains(project.Id), session.LastOpened));
            }
        }
        return entries.OrderByDescending(entry => entry.LastOpened).ToArray();
    }

    /// <summary>恢复会话显示；不改动会话内容。
    /// 所属项目也归档时一并恢复项目，否则恢复后的会话仍会被项目归档隐藏。</summary>
    public void RestoreSession(ArchivedSessionEntry entry) => RestoreSessionCore(entry.Id);

    private void RestoreSessionCore(string sessionId)
    {
        var presentation = SessionPresentation.Load(_store, sessionId);
        if (presentation.Archived)
            SessionPresentation.Save(_store, sessionId, presentation with { Archived = false });
        var record = _store.GetSession(sessionId);
        if (ProjectArchive.IsArchived(_store, record.ProjectId))
            ProjectArchive.SetArchived(_store, record.ProjectId, false);
        NotifyChanged();
    }

    public void RestoreProject(ArchivedProjectEntry entry)
    {
        ProjectArchive.SetArchived(_store, entry.Id, false);
        NotifyChanged();
    }

    /// <summary>打开已归档会话：先恢复显示，再切到它所属项目与会话。</summary>
    public async Task OpenSessionAsync(ArchivedSessionEntry entry)
    {
        RestoreSessionCore(entry.Id);
        await _openSession(_store.GetSession(entry.Id));
    }

    /// <summary>打开已归档项目：先恢复显示，再切换工作目录。</summary>
    public async Task OpenProjectAsync(ArchivedProjectEntry entry)
    {
        ProjectArchive.SetArchived(_store, entry.Id, false);
        NotifyChanged();
        await _openProject(entry.Folder);
    }

    private void NotifyChanged()
    {
        _afterChange?.Invoke();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
