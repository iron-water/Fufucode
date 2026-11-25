using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.Core;
using Fufucode.Core.Shell;

namespace Fufucode.App.ViewModels;

/// <summary>设置 → 高级：FUMO 内核的运行参数。每项改动立即写入 settings.json，下一次工具调用生效。</summary>
public sealed partial class AdvancedSettingsViewModel : ObservableObject
{
    public sealed record ShellOption(string Id, string Label, bool Installed)
    {
        public override string ToString() => Label;
    }

    private AppSettingsStore? _store;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _hasError;

    public IReadOnlyList<ShellOption> ShellOptions { get; private set; } = [];

    public void Attach(AppSettingsStore store)
    {
        _store = store;
        IsAvailable = true;
        Refresh();
    }

    /// <summary>重新探测本机解释器（设置页每次显示时调用：用户可能刚安装了 pwsh）。</summary>
    public void Refresh()
    {
        ShellOptions = CommandShells.All
            .Select(kind =>
            {
                var installed = CommandShells.FindExecutable(kind.Id) is not null;
                return new ShellOption(kind.Id, installed ? kind.DisplayName : kind.DisplayName + "（未安装）", installed);
            })
            .ToArray();
        OnPropertyChanged(nameof(ShellOptions));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(EffectiveShellText));
    }

    public FumoSettings Current => _store?.Current.Fumo ?? new FumoSettings();

    /// <summary>下拉框选中的解释器：未设置时就是默认（PowerShell 7）。</summary>
    public string SelectedShellId => Current.Shell ?? CommandShells.DefaultId;

    /// <summary>实际会使用的解释器与路径；不可用时给出原因。</summary>
    public string EffectiveShellText
    {
        get
        {
            var resolved = CommandShells.Resolve(Current.Shell, out var error);
            if (resolved is null) return error ?? "不可用";
            return resolved.IsFallback
                ? $"{CommandShells.Find(CommandShells.DefaultId)?.DisplayName} 未安装，正在使用 {resolved.Kind.DisplayName} · {resolved.Executable}"
                : resolved.Executable;
        }
    }

    public void SetShell(string id)
    {
        // 下拉框绑定/重建列表时会异步重放当前选中项；与显示值相同就不是用户改动，不得写盘。
        if (string.Equals(id, SelectedShellId, StringComparison.OrdinalIgnoreCase)) return;
        Save(current => current with { Shell = CommandShells.Find(id)?.Id ?? id });
    }

    public void SetLoadProfile(bool value) => Save(current => current with { LoadShellProfile = value });

    public void SetCommandTimeout(int seconds) =>
        Save(current => current with { CommandTimeoutSeconds = Math.Clamp(seconds, 1, FumoSettings.MaxCommandTimeoutSeconds) });

    public void Reset() => Save(_ => new FumoSettings(), clear: true);

    private void Save(Func<FumoSettings, FumoSettings> change, bool clear = false)
    {
        if (_store is null) return;
        try
        {
            _store.Update(settings =>
            {
                var next = change(settings.Fumo ?? new FumoSettings());
                return settings with { Fumo = clear || next == new FumoSettings() ? null : next };
            });
            HasError = false;
            Status = null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            HasError = true;
            Status = "保存失败：" + error.GetBaseException().Message;
        }
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(EffectiveShellText));
    }
}
