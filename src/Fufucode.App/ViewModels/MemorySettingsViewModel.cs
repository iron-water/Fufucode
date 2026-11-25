using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.Core.Memory;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 设置 → 其他 → 记忆：记忆插件的安装/启用，以及 MEMORY.md 条目的查看、添加与删除。
/// 文件是唯一真源，每次显示都重新读取（Agent 可能刚写入）。插件未启用时条目仍可编辑，只是不注入会话。
/// </summary>
public sealed partial class MemorySettingsViewModel : ObservableObject
{
    private MemoryNotebook? _notebook;
    private Func<PluginItemViewModel?> _plugin = () => null;
    private Action<PluginItemViewModel> _install = _ => { };
    private Action<PluginItemViewModel> _toggle = _ => { };
    private Action<string> _openFolder = _ => { };

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private IReadOnlyList<MemoryEntry> _entries = [];

    public PluginItemViewModel? Plugin => _plugin();
    public string? FilePath => _notebook?.FilePath;

    public void Attach(string? dataRoot, Func<PluginItemViewModel?> plugin,
        Action<PluginItemViewModel> install, Action<PluginItemViewModel> toggle, Action<string> openFolder)
    {
        _notebook = string.IsNullOrWhiteSpace(dataRoot) ? null : new MemoryNotebook(dataRoot);
        (_plugin, _install, _toggle, _openFolder) = (plugin, install, toggle, openFolder);
        IsAvailable = _notebook is not null;
        Refresh();
    }

    public void Refresh()
    {
        Status = null;
        try { Entries = _notebook?.Read() ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Fail(error); }
        OnPropertyChanged(nameof(Plugin));
    }

    public void InstallPlugin()
    {
        if (Plugin is { IsInstalled: false } item) _install(item);
        Refresh();
    }

    public void SetPluginEnabled(bool enabled)
    {
        if (Plugin is { IsInstalled: true } item && item.IsEnabled != enabled) _toggle(item);
        Refresh();
    }

    /// <summary>添加一条；返回是否成功（失败原因写入 <see cref="Status"/>）。</summary>
    public bool Add(string? text)
    {
        if (_notebook is null || string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            _notebook.Add(text);
            Refresh();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Fail(error);
            return false;
        }
    }

    public void Remove(MemoryEntry entry)
    {
        if (_notebook is null) return;
        try
        {
            _notebook.Forget(entry.Text);
            Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Fail(error); }
    }

    public void OpenFolder()
    {
        if (FilePath is not { } path) return;
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        _openFolder(folder);
    }

    private void Fail(Exception error) => Status = error.Message;
}
