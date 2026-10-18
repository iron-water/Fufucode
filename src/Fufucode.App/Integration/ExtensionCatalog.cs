using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>get_commands 返回的单条命令（扩展命令 / 提示词模板 / 技能）。</summary>
public sealed record CommandEntry(
    string Name,
    string? Description,
    string Source,
    string? SourcePath,
    string? SourceBaseDir,
    string? SourceScope)
{
    /// <summary>输入框调用名：技能形如 skill:x → /skill:x；扩展/提示词 → /name（decisions.md [轨道B]）。</summary>
    public string Invocation => "/" + Name;
}

/// <summary>一次扩展事件错误（ChatSessionCoordinator.ExtensionError）。</summary>
public sealed record ExtensionErrorEntry(DateTimeOffset When, string ExtensionPath, string EventName, string Error);

/// <summary>已加载扩展：按 sourceInfo.baseDir ?? dirname(sourceInfo.path) 分组的命令集合。</summary>
public sealed record LoadedExtension(
    string Path,
    string Name,
    string? Scope,
    IReadOnlyList<CommandEntry> Commands,
    IReadOnlyList<ExtensionErrorEntry> RecentErrors)
{
    public bool IsApprovalBridge =>
        Name.Equals("fufucode-bridge", StringComparison.OrdinalIgnoreCase);
}

/// <summary>磁盘上发现但未被当前进程加载的扩展（Pi 发现规则的镜像）。</summary>
public sealed record DiscoveredExtension(string Path, string Scope, bool Loaded);

/// <summary>一次扩展目录查询的完整快照。</summary>
public sealed class ExtensionCatalogSnapshot
{
    public IReadOnlyList<LoadedExtension> LoadedExtensions { get; init; } = [];
    public IReadOnlyList<CommandEntry> Commands { get; init; } = [];
    public IReadOnlyList<DiscoveredExtension> DiscoveredExtensions { get; init; } = [];
    public IReadOnlyList<ExtensionErrorEntry> RecentErrors { get; init; } = [];
    public ApprovalAvailability? ApprovalAvailability { get; init; }
    public string? ApprovalDiagnostic { get; init; }
    public DateTimeOffset QueriedAt { get; init; }
    public string? QueryError { get; init; }
}

/// <summary>
/// 扩展目录（H3/M5）：真实会话下用 Core 的 QueryCommandsAsync 取 get_commands，
/// 并按 Pi 的发现规则扫描项目与全局扩展目录，合成扩展页快照。
/// 只读真实数据——本版 Core 以 --no-extensions 启动，App 不提供启用/停用开关的真实生效路径。
/// </summary>
public sealed class ExtensionCatalog
{
    private readonly ISessionCoordinator _coordinator;
    private readonly string _projectPath;
    private readonly string _agentDirectory;
    private readonly object _errorLock = new();
    private readonly List<ExtensionErrorEntry> _errors = [];

    public ExtensionCatalog(ISessionCoordinator coordinator, string projectPath, string agentDirectory)
    {
        _coordinator = coordinator;
        _projectPath = projectPath;
        _agentDirectory = agentDirectory;
        _coordinator.ExtensionError += OnExtensionError;
    }

    /// <summary>当前保留的扩展事件错误（按收到顺序，最多保留 50 条）。</summary>
    public IReadOnlyList<ExtensionErrorEntry> RecentErrors
    {
        get { lock (_errorLock) return _errors.ToArray(); }
    }

    private void OnExtensionError(PiExtensionErrorEvent error)
    {
        lock (_errorLock)
        {
            _errors.Add(new ExtensionErrorEntry(DateTimeOffset.Now,
                error.ExtensionPath, error.EventName, error.Error));
            if (_errors.Count > 50)
            {
                _errors.RemoveRange(0, _errors.Count - 50);
            }
        }
    }

    /// <summary>查询 get_commands 并扫描发现目录，生成快照；查询失败填 QueryError。</summary>
    public async Task<ExtensionCatalogSnapshot> QueryAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CommandEntry> commands = [];
        string? queryError = null;
        try
        {
            var response = await _coordinator.QueryCommandsAsync(cancellationToken);
            if (!response.Success || response.Data is not { } data)
            {
                queryError = response.Error ?? "get_commands 未返回数据";
            }
            else
            {
                commands = ParseCommands(data, out var parseError);
                queryError = parseError;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            queryError = ex.GetBaseException().Message;
        }

        var errors = RecentErrors;
        var loaded = GroupLoadedExtensions(commands, errors);
        var discovered = ScanDiscovered(_projectPath, _agentDirectory, loaded);

        return new ExtensionCatalogSnapshot
        {
            LoadedExtensions = loaded,
            Commands = commands,
            DiscoveredExtensions = discovered,
            RecentErrors = errors,
            ApprovalAvailability = _coordinator.ApprovalAvailability,
            ApprovalDiagnostic = _coordinator.ApprovalDiagnostic,
            QueriedAt = DateTimeOffset.Now,
            QueryError = queryError,
        };
    }

    /// <summary>解析 get_commands 的 data.commands[]：缺字段容忍为 null；结构完全不符时报 parseError。</summary>
    public static IReadOnlyList<CommandEntry> ParseCommands(JsonElement data, out string? parseError)
    {
        parseError = null;
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("commands", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            parseError = "get_commands 响应缺少 commands 数组";
            return [];
        }

        var commands = new List<CommandEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = String(item, "name");
            if (name is null)
            {
                continue;
            }
            // 自有加载事实证明是内部命令，不向用户命令选择器暴露实现细节。
            if (name.StartsWith("fufucode-proof-", StringComparison.Ordinal)) continue;

            string? sourcePath = null, baseDir = null, scope = null;
            if (item.TryGetProperty("sourceInfo", out var info) &&
                info.ValueKind == JsonValueKind.Object)
            {
                sourcePath = String(info, "path");
                baseDir = String(info, "baseDir");
                scope = String(info, "scope");
            }

            commands.Add(new CommandEntry(
                name,
                String(item, "description"),
                String(item, "source") ?? "unknown",
                sourcePath,
                baseDir,
                scope));
        }

        return commands;
    }

    /// <summary>扩展命令按扩展根目录分组；事件错误按 ExtensionPath 归到对应组。</summary>
    public static IReadOnlyList<LoadedExtension> GroupLoadedExtensions(
        IReadOnlyList<CommandEntry> commands, IReadOnlyList<ExtensionErrorEntry> errors)
    {
        return commands
            .Where(command => command.Source == "extension" && command.SourcePath is not null)
            // GetDirectoryName 对无目录的相对路径返回空串；回退到 path 本身。
            .GroupBy(command => command.SourceBaseDir is { Length: > 0 } baseDir
                ? baseDir
                : Path.GetDirectoryName(command.SourcePath!) is { Length: > 0 } dir
                    ? dir
                    : command.SourcePath!)
            .Select(group =>
            {
                var path = group.Key;
                var normalized = Normalize(path);
                var groupErrors = errors
                    .Where(error => Normalize(error.ExtensionPath) == normalized ||
                                    Normalize(error.ExtensionPath).StartsWith(normalized + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var scope = group.Select(command => command.SourceScope)
                    .FirstOrDefault(scope => scope is not null);
                return new LoadedExtension(
                    path,
                    Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    scope,
                    group.ToArray(),
                    groupErrors);
            })
            .OrderBy(extension => extension.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Pi 发现规则镜像（dist/core/extensions/loader.js:515–620）：
    /// 扩展目录下的 *.ts/*.js 直接文件；子目录含 package.json 的 pi.extensions 声明、
    /// index.ts 或 index.js。无有效入口的子目录不计入。
    /// </summary>
    public static IReadOnlyList<DiscoveredExtension> ScanDiscovered(
        string projectPath, string agentDirectory, IReadOnlyList<LoadedExtension> loaded)
    {
        // 已加载判定：任何已加载命令的 sourceInfo.path 落在该路径之下（或等于该文件）。
        var commandPaths = loaded
            .SelectMany(extension => extension.Commands)
            .Select(command => command.SourcePath)
            .Where(path => path is not null)
            .Select(path => Normalize(path!))
            .ToArray();
        bool IsLoaded(string candidate)
        {
            var normalized = Normalize(candidate);
            var prefix = normalized + Path.DirectorySeparatorChar;
            return commandPaths.Any(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                                            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        var results = new List<DiscoveredExtension>();

        foreach (var (dir, scope) in new[]
                 {
                     (Path.Combine(projectPath, ".pi", "extensions"), "project"),
                     (Path.Combine(agentDirectory, "extensions"), "global"),
                 })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".js", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new DiscoveredExtension(file, scope, IsLoaded(file)));
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    if (HasExtensionEntry(child))
                    {
                        results.Add(new DiscoveredExtension(child, scope, IsLoaded(child)));
                    }
                }
            }
            catch (Exception)
            {
                // 目录不可读不是致命错误；已发现部分照常返回。
            }
        }

        return results;
    }

    /// <summary>“添加扩展”入口校验：.ts/.js 文件，或含扩展入口的目录（index.ts/js 或 package.json pi.extensions）。</summary>
    public static bool IsValidExtensionPath(string path)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".js", StringComparison.OrdinalIgnoreCase);
        }

        return Directory.Exists(path) && HasExtensionEntry(path);
    }

    private static bool HasExtensionEntry(string directory)
    {
        try
        {
            if (File.Exists(Path.Combine(directory, "index.ts")) ||
                File.Exists(Path.Combine(directory, "index.js")))
            {
                return true;
            }

            var packageJson = Path.Combine(directory, "package.json");
            if (!File.Exists(packageJson))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            // pi.extensions 声明存在即视为有效扩展入口（具体字段值交给 Pi 自身解析）。
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("pi", out var pi) &&
                   pi.ValueKind == JsonValueKind.Object &&
                   pi.TryGetProperty("extensions", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            // 虚拟/相对路径无法规范化时按原文比较。
            return path;
        }
    }

    private static string? String(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
