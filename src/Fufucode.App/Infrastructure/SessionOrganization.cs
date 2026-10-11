using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

public sealed record SessionSection(string Id, string Name);
public sealed record SessionProjectOptions(string Name, string Folder, string? SectionId = null);
public sealed record SessionPlacement(string? ProjectId = null, string? SectionId = null);
public sealed record SessionOrganizationState
{
    public List<SessionSection> Sections { get; init; } = [];
    public Dictionary<string, SessionProjectOptions> Projects { get; init; } = [];
    public Dictionary<string, SessionPlacement> Sessions { get; init; } = [];
    public bool ActivityView { get; init; }
    public string Priority { get; init; } = "attention";
}

/// <summary>Display organization is separate from Pi's immutable session/project working directory.</summary>
public sealed class SessionOrganization
{
    private const string Key = "sessions:organization:v1";
    private readonly DesktopMetadataStore _store;
    public SessionOrganization(DesktopMetadataStore store)
    {
        _store = store;
        var json = store.GetPreference(Key);
        try
        {
            State = json is null ? new() : JsonSerializer.Deserialize<SessionOrganizationState>(json) ??
                throw new JsonException("Empty organization state.");
            if (State.Sections is null || State.Projects is null || State.Sessions is null ||
                State.Priority is not ("attention" or "pinned" or "scheduled") ||
                State.Sections.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name)) ||
                State.Sections.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != State.Sections.Count ||
                State.Projects.Any(p => p.Value is null || string.IsNullOrWhiteSpace(p.Value.Name) ||
                    string.IsNullOrWhiteSpace(p.Value.Folder) || !Path.IsPathFullyQualified(p.Value.Folder)) ||
                State.Sessions.Any(s => s.Value is null))
                throw new JsonException("Invalid organization state.");
        }
        catch (JsonException)
        {
            State = new();
            RestoreError = "会话分组配置损坏，已临时恢复默认视图。原配置未覆盖。";
        }
    }
    public SessionOrganizationState State { get; private set; }
    public string? RestoreError { get; }
    private void Save(SessionOrganizationState state)
    {
        _store.SetPreference(Key, JsonSerializer.Serialize(state));
        State = state;
    }
    private static string Name(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120) throw new ArgumentException("名称须为 1 到 120 个字符。");
        return name;
    }
    public SessionProjectOptions Project(ProjectRecord project) => State.Projects.GetValueOrDefault(project.Id) ??
        new(Path.GetFileName(project.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), project.Path);
    public SessionPlacement Placement(string id) => State.Sessions.GetValueOrDefault(id) ?? new(_store.GetSession(id).ProjectId);
    public SessionSection AddSection(string name)
    {
        var section = new SessionSection(Guid.NewGuid().ToString("N"), Name(name));
        Save(State with { Sections = [.. State.Sections, section] });
        return section;
    }
    public void RenameSection(string id, string name)
    {
        if (!State.Sections.Any(s => s.Id == id)) throw new KeyNotFoundException("分区不存在。");
        var title = Name(name);
        Save(State with { Sections = State.Sections.Select(s => s.Id == id ? s with { Name = title } : s).ToList() });
    }
    private void ValidateSection(string? id)
    {
        if (id is not null && !State.Sections.Any(s => s.Id == id)) throw new KeyNotFoundException("分区不存在。");
    }
    public void EditProject(ProjectRecord project, string name, string folder, string? sectionId)
    {
        ValidateSection(sectionId);
        folder = AppDataLayout.NormalizeProjectPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var projects = new Dictionary<string, SessionProjectOptions>(State.Projects)
        { [project.Id] = new(Name(name), folder, sectionId) };
        Save(State with { Projects = projects });
    }
    public void PlaceSession(string id, string? projectId, string? sectionId)
    {
        _ = _store.GetSession(id);
        if (projectId is not null) _ = _store.GetProject(projectId);
        ValidateSection(sectionId);
        var sessions = new Dictionary<string, SessionPlacement>(State.Sessions) { [id] = new(projectId, sectionId) };
        Save(State with { Sessions = sessions });
    }
    public void SetActivity(bool active) => Save(State with { ActivityView = active });
    public void SetPriority(string priority)
    {
        if (priority is not ("attention" or "pinned" or "scheduled")) throw new ArgumentException("优先级无效。");
        Save(State with { Priority = priority });
    }
}
