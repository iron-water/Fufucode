using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fufucode.Core.Backend;

namespace Fufucode.App.ViewModels;

/// <summary>提供商的模型协议（Chat Completions / Responses / Anthropic Messages）。选项来自 <see cref="ModelApi"/>。</summary>
public sealed partial class SettingsPageViewModel
{
    public IReadOnlyList<string> EndpointApiOptions { get; } = ModelApi.All.Select(kind => kind.DisplayName).ToArray();

    [ObservableProperty] private int _endpointApiIndex;

    partial void OnEndpointApiIndexChanged(int value) => NotifyDraftState();

    /// <summary>当前表单选择的协议；Chat Completions 存为 null，旧配置文件保持原样。</summary>
    private string? SelectedEndpointApi =>
        EndpointApiIndex > 0 && EndpointApiIndex < ModelApi.All.Count ? ModelApi.All[EndpointApiIndex].Id : null;

    private static int ApiIndex(string? api)
    {
        var id = ModelApi.Normalize(api);
        for (var index = 0; index < ModelApi.All.Count; index++)
            if (string.Equals(ModelApi.All[index].Id, id, StringComparison.Ordinal)) return index;
        return 0;
    }
}
