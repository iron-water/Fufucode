using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 自有 Pi 包装入口：先 await 原插件初始化，成功后才登记唯一证明命令。
/// 仅证明固定入口完成初始化，不是通用扩展清单、运行健康检查或 OS 安全沙箱。
/// 不修改固定版本副本、不启动进程、不登记已加载；调用者仍须绑定当前真实会话的 RPC。
/// </summary>
public sealed class PiPluginLoadProof
{
    private PiPluginLoadProof(string packageRoot, string entryPath, string loaderPath,
        string commandName, string description, string contentHash, string loaderHash, string pluginId, string version)
    {
        PackageRoot = packageRoot; EntryPath = entryPath; LoaderPath = loaderPath;
        CommandName = commandName; Description = description;
        ContentHash = contentHash; LoaderHash = loaderHash;
        PluginId = pluginId; Version = version;
    }

    public string PackageRoot { get; }
    public string EntryPath { get; }
    public string LoaderPath { get; }
    public string CommandName { get; }
    public string Description { get; }
    public string ContentHash { get; }
    public string LoaderHash { get; }
    public string PluginId { get; }
    public string Version { get; }

    private sealed record StoredProof(int SchemaVersion, string PluginId, string Version,
        string PackageRelativePath, string EntryRelativePath, string ContentHash, string LoaderHash);

    /// <summary>从启用列表中的自有包装入口恢复证明身份；未知/损坏元数据拒绝，不能重造成功事实。</summary>
    public static PiPluginLoadProof Open(AppDataLayout layout, string loaderPath)
    {
        loaderPath = Path.GetFullPath(loaderPath);
        var loaderRoot = Path.Combine(layout.Root, "plugins", "loaders");
        var nonce = Path.GetFileNameWithoutExtension(loaderPath);
        if (!PluginPackageScanner.IsInsideRoot(loaderRoot, loaderPath) ||
            !string.Equals(Path.GetDirectoryName(loaderPath), loaderRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Path.GetExtension(loaderPath) != ".ts" || !Guid.TryParseExact(nonce, "N", out _))
            throw new IOException("不是本产品的固定包装入口。");
        var metadataPath = loaderPath + ".json";
        EnsureNoReparse(metadataPath);
        if (new FileInfo(metadataPath).Length > 64 * 1024) throw new IOException("证明元数据过大。");
        var stored = JsonSerializer.Deserialize<StoredProof>(File.ReadAllText(metadataPath))
            ?? throw new IOException("证明元数据为空。");
        if (stored.SchemaVersion != 1 || string.IsNullOrWhiteSpace(stored.PluginId) || string.IsNullOrWhiteSpace(stored.Version) ||
            Path.IsPathRooted(stored.PackageRelativePath) || Path.IsPathRooted(stored.EntryRelativePath))
            throw new IOException("证明元数据版本/身份/路径无效。");
        var localRoot = Path.Combine(layout.Root, "plugins", "local");
        var packageRoot = Path.GetFullPath(Path.Combine(localRoot, stored.PackageRelativePath));
        var entryPath = Path.GetFullPath(Path.Combine(packageRoot, stored.EntryRelativePath));
        if (!PluginPackageScanner.IsInsideRoot(localRoot, packageRoot) || !PluginPackageScanner.IsInsideRoot(packageRoot, entryPath))
            throw new IOException("证明元数据路径越界。");
        var description = $"Fufucode initialization {nonce} | {stored.PluginId} | {stored.Version} | {stored.ContentHash}";
        return new PiPluginLoadProof(packageRoot, entryPath, loaderPath, "fufucode-proof-" + nonce,
            description, stored.ContentHash, stored.LoaderHash, stored.PluginId, stored.Version);
    }

    /// <summary>宿主使用的入口：核对实际 RPC 命令/成功状态和本次连接代次，然后检查证明内容。</summary>
    public bool TryConfirmInitialization(RpcResponse response, Guid observedGeneration,
        Guid expectedGeneration, out string reason)
    {
        if (expectedGeneration == Guid.Empty || observedGeneration != expectedGeneration ||
            response.Command != "get_commands" || !response.Success || response.Data is null)
        {
            reason = "RPC 失败、命令或连接代次不匹配，初始化结果未知。";
            return false;
        }
        return MatchesCommandData(response.Data.Value, out reason);
    }

    internal static PiPluginLoadProof Create(AppDataLayout layout, string packageRoot,
        string entryPath, string pluginId, string version, string contentHash)
    {
        var managedRoot = Path.Combine(layout.Root, "plugins", "local");
        packageRoot = Path.GetFullPath(packageRoot);
        entryPath = Path.GetFullPath(entryPath);
        if (!PluginPackageScanner.IsInsideRoot(managedRoot, packageRoot) ||
            !PluginPackageScanner.IsInsideRoot(packageRoot, entryPath))
            throw new InvalidOperationException("初始化证明只允许本产品托管固定副本的入口。");
        EnsureNoReparse(entryPath);
        if (PluginPackageScanner.Inspect(packageRoot, LocalPluginLimits.Default) is { } error)
            throw new InvalidOperationException(error.Message);
        if (PluginPackageScanner.ComputeContentHash(packageRoot) != contentHash)
            throw new InvalidOperationException("固定副本摘要已改变，不能生成加载证明。");

        var nonce = Guid.NewGuid().ToString("N");
        var command = "fufucode-proof-" + nonce;
        var description = $"Fufucode initialization {nonce} | {pluginId} | {version} | {contentHash}";
        var parent = Path.Combine(layout.Root, "plugins");
        EnsureNoReparse(parent);
        var loaders = Path.Combine(parent, "loaders");
        if (Directory.Exists(loaders)) EnsureNoReparse(loaders);
        Directory.CreateDirectory(loaders);
        EnsureNoReparse(loaders);
        var loader = Path.Combine(loaders, nonce + ".ts");
        var relative = Path.GetRelativePath(loaders, entryPath).Replace('\\', '/');
        var text = "// Fufucode owned initialization proof. The original package is immutable.\n" +
            "import initialize from " + JsonSerializer.Serialize(relative) + ";\n" +
            "export default async function(pi) {\n" +
            "  if (typeof initialize !== 'function') throw new Error('Plugin default export must be a function');\n" +
            "  await initialize(pi);\n" +
            "  pi.registerCommand(" + JsonSerializer.Serialize(command) + ", { description: " +
            JsonSerializer.Serialize(description) + ", handler: async () => {} });\n" +
            "}\n";
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using (var stream = new FileStream(loader, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        var loaderHash = Convert.ToHexString(SHA256.HashData(bytes));
        var stored = new StoredProof(1, pluginId, version,
            Path.GetRelativePath(managedRoot, packageRoot), Path.GetRelativePath(packageRoot, entryPath), contentHash, loaderHash);
        using (var stream = new FileStream(loader + ".json", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, stored);
            stream.Flush(flushToDisk: true);
        }
        return new PiPluginLoadProof(packageRoot, entryPath, loader, command, description,
            contentHash, loaderHash, pluginId, version);
    }

    /// <summary>
    /// 只接 get_commands.data：命令名称、唯一描述、extension 来源与精确包装入口全部匹配。
    /// 证明产物/固定副本若改变则拒收；调用者不能把历史响应、别的进程或超时当当前加载事实。
    /// </summary>
    public bool MatchesCommandData(JsonElement data, out string reason)
    {
        try
        {
            EnsureNoReparse(LoaderPath);
            EnsureNoReparse(EntryPath);
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(LoaderPath))) != LoaderHash ||
                PluginPackageScanner.Inspect(PackageRoot, LocalPluginLimits.Default) is not null ||
                PluginPackageScanner.ComputeContentHash(PackageRoot) != ContentHash)
            {
                reason = "证明入口或固定副本内容已改变，初始化结果未知。";
                return false;
            }
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("commands", out var commands) ||
                commands.ValueKind != JsonValueKind.Array)
            {
                reason = "get_commands 缺少有效 commands 数组。";
                return false;
            }
            var matching = commands.EnumerateArray().Where(command =>
                String(command, "name") == CommandName && String(command, "source") == "extension" &&
                String(command, "description") == Description && command.TryGetProperty("sourceInfo", out var info) &&
                info.ValueKind == JsonValueKind.Object && PathMatches(String(info, "path"))).Count();
            if (matching != 1)
            {
                reason = "未发现唯一匹配当前包装入口的初始化证明命令。";
                return false;
            }
            reason = "固定插件入口已完成初始化（仅本次证明的入口与内容）。";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            reason = "初始化证明不可核对：" + error.GetBaseException().Message;
            return false;
        }
    }

    private bool PathMatches(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFullPath(path), LoaderPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? String(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void EnsureNoReparse(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("证明路径或祖先目录存在链接/重解析点，拒绝核对。");
        }
    }
}
