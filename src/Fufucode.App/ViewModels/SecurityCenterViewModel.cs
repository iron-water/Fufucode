using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.Core.Security;

namespace Fufucode.App.ViewModels;

/// <summary>安全中心使用独立策略文件，执行端在每次操作时读取同一个真源。</summary>
public sealed class SecurityCenterViewModel : ObservableObject
{
    private SecurityPolicyStore? _store;
    private SecurityAuditStore? _audit;
    private string? _dataRoot;
    private SecuritySettings _settings = new();
    private string _status = "";
    private bool _hasError;

    public SecuritySettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
    public bool IsAvailable => _store is not null;
    public string? DataRoot => _dataRoot;
    public Action<string>? Navigate { get; set; }
    public Action<string>? OpenFolder { get; set; }

    public void Attach(string? dataRoot, Action<string>? openFolder = null)
    {
        _dataRoot = dataRoot;
        OpenFolder = openFolder;
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            _store = null;
            _audit = null;
            HasError = true;
            Status = "安全策略存储不可用。请打开有数据目录的应用实例。";
        }
        else
        {
            try
            {
                _store = new SecurityPolicyStore(dataRoot);
                _audit = new SecurityAuditStore(dataRoot);
                Settings = _store.Current;
                HasError = false;
                Status = "规则保存后用于后续工具调用。正在执行的操作不受影响。";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or SecurityPolicyException)
            {
                _store = null;
                _audit = null;
                HasError = true;
                Status = "无法读取安全策略：" + error.Message;
            }
        }
        OnPropertyChanged(nameof(IsAvailable));
    }

    public bool Save(Func<SecuritySettings, SecuritySettings> change)
    {
        if (_store is null) { Fail("安全策略存储不可用，未保存。"); return false; }
        try
        {
            Settings = _store.Update(change);
            HasError = false;
            Status = "已保存，下次工具调用生效。";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or SecurityPolicyException)
        {
            Fail("保存失败：" + error.Message);
            return false;
        }
    }

    public bool Reset()
    {
        if (_dataRoot is null) return false;
        try
        {
            _store ??= new SecurityPolicyStore(_dataRoot);
            Settings = _store.Update(_ => new SecuritySettings());
            _audit ??= new SecurityAuditStore(_dataRoot);
            HasError = false;
            Status = "已恢复默认安全策略。";
            OnPropertyChanged(nameof(IsAvailable));
            return true;
        }
        catch (Exception error) { Fail("恢复默认失败：" + error.Message); return false; }
    }

    public void Refresh()
    {
        if (_store is null) return;
        try { Settings = _store.Current; HasError = false; Status = "已读取最新策略与审计记录。"; }
        catch (Exception error) { Fail("读取失败：" + error.Message); }
    }

    public IReadOnlyList<string> AuditLines()
    {
        try
        {
            return _audit?.ReadRecent(100).Select(entry =>
                $"{entry.At.ToLocalTime():MM-dd HH:mm:ss}  [{entry.Category}] {entry.Action}  {entry.Summary}").ToArray() ?? [];
        }
        catch (Exception error) { Fail("读取审计失败：" + error.Message); return []; }
    }

    public void ExportAudit(string path)
    {
        try
        {
            if (_audit is null) throw new InvalidOperationException("审计存储不可用。");
            _audit.Export(path);
            HasError = false;
            Status = "审计日志已导出到 " + path;
        }
        catch (Exception error) { Fail("导出失败：" + error.Message); }
    }

    public void ClearAudit()
    {
        try
        {
            if (_audit is null) throw new InvalidOperationException("审计存储不可用。");
            _audit.Clear(); HasError = false; Status = "已清空审计记录。";
        }
        catch (Exception error) { Fail("清空失败：" + error.Message); }
    }

    public void Complete(string message) { HasError = false; Status = message; }
    public void Fail(string message) { HasError = true; Status = message; }
}
