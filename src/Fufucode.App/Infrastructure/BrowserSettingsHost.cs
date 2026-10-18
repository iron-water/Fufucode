using System;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 进程内唯一的浏览器设置宿主（BRW-1）：设置页与内置浏览器读取同一份
/// <see cref="BrowserSettingsStore"/>，避免两处各自持有不同副本导致显示与行为不一致。
/// 组合根负责创建并 <see cref="Attach"/>；视图只订阅。
/// </summary>
public sealed class BrowserSettingsHost
{
    private readonly BrowserSettingsStore? _store;

    public BrowserSettingsHost(string? dataRoot)
    {
        try
        {
            _store = dataRoot is { Length: > 0 } root ? new BrowserSettingsStore(root) : null;
        }
        catch (Exception error)
        {
            LastError = error.GetBaseException().Message;
            _store = null;
        }
        ViewModel = new BrowserSettingsViewModel();
        ViewModel.Attach(dataRoot);
    }

    public BrowserSettingsViewModel ViewModel { get; }
    public string? LastError { get; }
    public BrowserSettingsStore? Store => _store;
}
