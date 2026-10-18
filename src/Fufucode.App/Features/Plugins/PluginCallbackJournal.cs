using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Features.Plugins;

/// <summary>仅证明应用收到原宿主Task的返回/异常；不证明引擎退出、目录提交或安全释放。</summary>
public sealed record PluginCallbackCompletion(int SchemaVersion, string PluginId, string Token, string VersionId,
    DateTimeOffset StartedAtUtc, HostIdentity HostIdentity, string? HostContext, string Outcome,
    string? Message, DateTimeOffset ReturnedAtUtc)
{
    public Guid? OriginalGeneration { get; init; }
    public RpcProcessExit? OriginalProcessExit { get; init; }
    public PluginHostContext? OriginalContext { get; init; }
    public int RequestLeaseVersion { get; init; }
}

public static class PluginCallbackJournal
{
    public const int SchemaVersion = 1;

    public static string PathFor(string root, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("缺少原请求标识。");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return Path.Combine(root, ".callback-results", key + ".json");
    }

    private static bool Valid(PluginCallbackCompletion value) => value.SchemaVersion == SchemaVersion &&
        !string.IsNullOrWhiteSpace(value.PluginId) && !string.IsNullOrWhiteSpace(value.Token) &&
        !string.IsNullOrWhiteSpace(value.VersionId) && value.HostIdentity is not null &&
        value.StartedAtUtc != default && value.ReturnedAtUtc != default &&
        value.Outcome is "returned_success" or "returned_failure" or "cancelled" or "threw" &&
        (value.OriginalProcessExit is not { } exit || value.OriginalGeneration == exit.Generation &&
            exit.Generation != Guid.Empty && exit.ProcessId > 0 && exit.ObservedAtUtc != default);

    private static void RejectLinks(string root, string path)
    {
        foreach (var candidate in new[] { root, Path.GetDirectoryName(path)!, path })
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("返回记录路径含链接，未读写其他目标。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void Save(string root, PluginCallbackCompletion completion)
    {
        if (!Valid(completion)) throw new InvalidOperationException("原宿主返回记录身份或格式不完整。");
        var path = PathFor(root, completion.Token);
        RejectLinks(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectLinks(root, path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var text = JsonSerializer.Serialize(completion);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static (PluginCallbackCompletion? Completion, string Diagnostic) Read(string root,
        string pluginId, InFlightCandidate request)
    {
        try
        {
            var path = PathFor(root, request.Token);
            RejectLinks(root, path);
            if (!File.Exists(path)) return (null, "尚无可核对的宿主返回记录；不能据此断言回调仍运行或已终止。");
            var completion = JsonSerializer.Deserialize<PluginCallbackCompletion>(File.ReadAllText(path));
            if (completion is null || !Valid(completion) || completion.PluginId != pluginId ||
                completion.Token != request.Token || completion.VersionId != request.VersionId ||
                completion.StartedAtUtc != request.StartedAtUtc)
                return (null, "宿主返回记录格式或原请求身份不符；原文件与保护保持。");
            return (completion, "已记录应用收到原宿主调用的返回；目录提交、引擎终止与可安全释放尚未由此证明。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return (null, "宿主返回记录不可核对，原文件与保护保持：" + error.GetBaseException().Message); }
    }
}
