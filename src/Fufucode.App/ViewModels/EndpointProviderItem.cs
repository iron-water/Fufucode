using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 端点配置里的一个提供商（一个 Base URL 即一个提供商）。
/// 每个提供商各自持有一份模型勾选清单；会话的模型下拉来自所选提供商的清单。
/// </summary>
public sealed partial class EndpointProviderItem : ObservableObject
{
    public EndpointProviderItem(string name, string baseUrl, string apiKeyEnvironmentVariable)
    {
        _name = name;
        _baseUrl = baseUrl;
        _apiKeyEnvironmentVariable = apiKeyEnvironmentVariable;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _baseUrl;
    [ObservableProperty] private string _apiKeyEnvironmentVariable;

    /// <summary>该提供商下可选的全部模型。</summary>
    public ObservableCollection<EndpointModelOption> Models { get; } = new();

    /// <summary>左侧列表标题：名称 + Base URL。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "（未命名）" : Name;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    public bool HasModels => Models.Count > 0;

    internal void NotifyModelsChanged()
    {
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(SelectedCountText));
    }

    public string SelectedCountText =>
        $"已选 {CountSelected()} / {Models.Count}";

    public int CountSelected()
    {
        var count = 0;
        foreach (var model in Models) if (model.IsSelected) count++;
        return count;
    }

    public string? PrimaryModelId()
    {
        foreach (var model in Models) if (model.IsSelected) return model.Id;
        return null;
    }

    public string[] SelectedModelIds()
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var model in Models) if (model.IsSelected) list.Add(model.Id);
        return list.ToArray();
    }
}
