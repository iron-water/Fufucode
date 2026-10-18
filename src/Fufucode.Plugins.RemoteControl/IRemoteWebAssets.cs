using System;
using System.Collections.Generic;
using System.Text;

namespace Fufucode.Core.RemoteControl;

/// <summary>一个可提供的静态资源。</summary>
public sealed record RemoteWebAsset(string ContentType, byte[] Content)
{
    public static RemoteWebAsset Text(string contentType, string content) =>
        new(contentType, Encoding.UTF8.GetBytes(content));
}

/// <summary>Web UI 静态资源提供方；App 侧实现内嵌页面，测试可用内存实现。</summary>
public interface IRemoteWebAssets
{
    bool TryGet(string path, out RemoteWebAsset asset);
}

/// <summary>内存资源集合（供独立测试与轻量宿主使用）。</summary>
public sealed class InMemoryRemoteWebAssets : IRemoteWebAssets
{
    private readonly Dictionary<string, RemoteWebAsset> _assets = new(StringComparer.Ordinal);

    public InMemoryRemoteWebAssets Add(string path, string contentType, string content)
    {
        _assets[path] = RemoteWebAsset.Text(contentType, content);
        return this;
    }

    public bool TryGet(string path, out RemoteWebAsset asset)
    {
        if (_assets.TryGetValue(path, out var found))
        {
            asset = found;
            return true;
        }

        asset = null!;
        return false;
    }
}
