using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>
/// 同一 UserDataFolder 只创建一个 CoreWebView2Environment，供同一进程内的多个页签 controller 共享。
/// WebView2 要求同一个 user data folder 的环境选项完全一致；共享环境可避免多页签并发创建时的选项/进程竞争。
/// 复用现有 profile 路径（BrowserUserDataLayout），不更换存储根。
/// </summary>
internal static class BrowserEnvironmentProvider
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<CoreWebView2Environment> GetAsync(string userDataFolder)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(userDataFolder, out var existing) && !existing.IsFaulted && !existing.IsCanceled)
                return await existing.ConfigureAwait(false);

            var created = CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            Cache[userDataFolder] = created;
            try
            {
                return await created.ConfigureAwait(false);
            }
            catch
            {
                // 失败不缓存，允许下一次创建重试。
                if (Cache.TryGetValue(userDataFolder, out var stored) && ReferenceEquals(stored, created)) Cache.Remove(userDataFolder);
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>测试用：丢弃缓存，让下一次重新创建环境。</summary>
    internal static void ResetForTesting()
    {
        Gate.Wait();
        try { Cache.Clear(); }
        finally { Gate.Release(); }
    }
}
