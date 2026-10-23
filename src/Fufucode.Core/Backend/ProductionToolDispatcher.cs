using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Fufucode.Core.Diagnostics;
namespace Fufucode.Core.Backend;

/// <summary>
/// 纯 C# 生产级工具调度器：执行文件读写/编辑、代码正则检索、通配发现、目录浏览与受控命令，
/// 具有工作区路径边界守卫与进程树超时清理，零 Python/Node.js 运行时依赖。
/// </summary>
public sealed class ProductionToolDispatcher : IPureToolDispatcher
{
    private readonly string _workspaceRoot;
    private readonly string _workspacePrefix;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // 默认检索与遍历时忽略的噪音文件夹
    private static readonly HashSet<string> ExcludedDirectories = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    {
        ".git", ".claude", "bin", "obj", "node_modules", "out", "artifacts", ".vs", ".idea", ".workbuddy",
        "dist", "target", "coverage", "venv", ".venv", ".next", ".gradle", "__pycache__", ".dart_tool",
        ".pytest_cache", ".mypy_cache", ".terraform", "Pods", "DerivedData", ".next-cache"
    };

    public ProductionToolDispatcher(string? workspaceRoot = null)
    {
        _workspaceRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workspaceRoot ?? Environment.CurrentDirectory));
        // 驱动器根（"C:\"）本身已带分隔符，不能再补一个；其余路径统一补一个，
        // 避免根目录带尾分隔符时所有子路径被误判为越界。
        _workspacePrefix = Path.EndsInDirectorySeparator(_workspaceRoot)
            ? _workspaceRoot
            : _workspaceRoot + Path.DirectorySeparatorChar;
    }

    public string WorkspaceRoot => _workspaceRoot;

    private GitIgnoreMatcher? _ignoreMatcher;
    private DateTime _ignoreStamp;
    private readonly object _ignoreGate = new();

    /// <summary>读取根 .gitignore 与 .git/info/exclude（按修改时间缓存）；无文件时为空匹配器。</summary>
    private GitIgnoreMatcher IgnoreMatcher()
    {
        lock (_ignoreGate)
        {
            var gitignore = Path.Combine(_workspaceRoot, ".gitignore");
            var exclude = Path.Combine(_workspaceRoot, ".git", "info", "exclude");
            var stamp = new[]
                {
                    File.Exists(gitignore) ? File.GetLastWriteTimeUtc(gitignore) : DateTime.MinValue,
                    File.Exists(exclude) ? File.GetLastWriteTimeUtc(exclude) : DateTime.MinValue,
                }
                .Max();
            if (_ignoreMatcher is not null && stamp == _ignoreStamp) return _ignoreMatcher;

            var patterns = new List<string>();
            foreach (var path in new[] { gitignore, exclude })
            {
                if (!File.Exists(path)) continue;
                try { patterns.AddRange(File.ReadLines(path)); }
                catch { }
            }
            _ignoreMatcher = GitIgnoreMatcher.Build(patterns);
            _ignoreStamp = stamp;
            return _ignoreMatcher;
        }
    }

    private string Relative(string fullPath) =>
        Path.GetRelativePath(_workspaceRoot, fullPath).Replace('\\', '/');

    /// <summary>轻量 .gitignore 匹配：支持注释、取反、目录规则、前导 /、*、**、?。</summary>
    private sealed class GitIgnoreMatcher
    {
        private readonly List<(Regex Regex, bool Negate)> _rules;

        private GitIgnoreMatcher(List<(Regex, bool)> rules) => _rules = rules;

        public static GitIgnoreMatcher Build(IEnumerable<string> lines)
        {
            var rules = new List<(Regex, bool)>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var negate = line.StartsWith('!');
                if (negate) line = line[1..];
                var anchored = line.StartsWith('/');
                if (anchored) line = line[1..];
                if (line.EndsWith('/')) line = line[..^1];
                if (line.Length == 0) continue;

                var body = Regex.Escape(line)
                    .Replace(@"\*\*/", @"(?:.*/)?")
                    .Replace(@"\*\*", @".*")
                    .Replace(@"\*", @"[^/]*")
                    .Replace(@"\?", @"[^/]");
                var prefix = !anchored && !line.Contains('/') ? "(?:.*/)?" : string.Empty;
                try
                {
                    rules.Add((new Regex("^" + prefix + body + "(?:/.*)?$", RegexOptions.NonBacktracking,
                        TimeSpan.FromSeconds(1)), negate));
                }
                catch { }
            }
            return new GitIgnoreMatcher(rules);
        }

        public bool IsIgnored(string relativePath)
        {
            var ignored = false;
            foreach (var (regex, negate) in _rules)
            {
                if (!regex.IsMatch(relativePath)) continue;
                ignored = !negate;
            }
            return ignored;
        }
    }

    private volatile PermissionMode _mode = PermissionMode.Default;

    /// <summary>当前会话的权限模式（默认 Default）。Plan 模式下禁止修改文件与执行系统命令。</summary>
    public PermissionMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public sealed record PlanStep(string Title, string Status);
    public sealed record AgentPlan(IReadOnlyList<PlanStep> Steps, string? Notes);

    /// <summary>模型当前维护的结构化计划快照。</summary>
    public AgentPlan? CurrentPlan { get; private set; }

    /// <summary>判断工具是否为并发安全（只读无副作用：可多线程并行执行）。</summary>
    public static bool IsConcurrencySafe(string toolName) => toolName switch
    {
        "read_file" or "list_dir" or "grep_search" or "glob_files" => true,
        // 子代理各自持有独立上下文；read 天然只读，patch 的写入被限制在独立 worktree。
        // worktree 创建在运行器内部串行化，因此多次派发可以并行。
        NativeSubagentContract.ToolName => true,
        _ => false,
    };

    /// <summary>判断工具是否具有变更性（文件写/修改或系统命令）。</summary>
    public static bool IsMutatingTool(string toolName) => toolName switch
    {
        "write_file" or "edit_file" or "run_command" => true,
        _ => false,
    };

    /// <summary>
    /// Plan 模式只放行明确只读的原生工具。插件/MCP/子代理等第三方工具
    /// 无法静态证明只读，必须按变更性处理，不能只拦三个内置写工具。
    /// </summary>
    public static bool IsPlanSafeTool(string toolName) => toolName switch
    {
        "read_file" or "list_dir" or "grep_search" or "glob_files" or "update_plan" => true,
        _ => false,
    };

    /// <summary>是否为原生内置工具名（插件不得同名覆盖）。`skill` 也属于保留名。</summary>
    public static bool IsBuiltinToolName(string toolName) =>
        string.Equals(toolName, SkillRegistry.ToolName, StringComparison.Ordinal) ||
        string.Equals(toolName, McpGatewayDispatcher.ToolName, StringComparison.Ordinal) ||
        string.Equals(toolName, NativeSubagentContract.ToolName, StringComparison.Ordinal) ||
        GetBuiltinToolDefinitions().Any(d => string.Equals(d.Name, toolName, StringComparison.Ordinal));

    public async Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();

        // Plan 模式权限守卫：只允许明确只读的原生工具与 update_plan。
        // 第三方插件/MCP/子代理无法证明只读，统一按变更性拦截。
        if (Mode == PermissionMode.Plan && !IsPlanSafeTool(call.Name))
        {
            return new PureToolResult(call.Id, false,
                $"【Plan 规划模式拦截】：当前会话处于只读 Plan 模式，禁止执行变更工具 '{call.Name}'。请先使用 update_plan 整理并确认规划，再由用户切换执行模式。");
        }

        try
        {
            return call.Name switch
            {
                "read_file" => await ExecuteReadFileAsync(call, cancellationToken).ConfigureAwait(false),
                "write_file" => await ExecuteWriteFileAsync(call, cancellationToken).ConfigureAwait(false),
                "edit_file" => await ExecuteEditFileAsync(call, cancellationToken).ConfigureAwait(false),
                "list_dir" => await Task.Run(() => ExecuteListDir(call), cancellationToken).ConfigureAwait(false),
                "grep_search" => await ExecuteGrepSearchAsync(call, cancellationToken).ConfigureAwait(false),
                "glob_files" => await Task.Run(() => ExecuteGlobFiles(call, cancellationToken), cancellationToken).ConfigureAwait(false),
                "run_command" => await ExecuteRunCommandAsync(call, cancellationToken).ConfigureAwait(false),
                "update_plan" => ExecuteUpdatePlan(call),
                _ => new PureToolResult(call.Id, false, $"未知工具：'{call.Name}'。")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            using var errorSpan = PerfTracer.Step("tool.error", "tool", call.Name);
            errorSpan.Fail(ex.GetType().Name);
            return new PureToolResult(call.Id, false, $"工具执行异常：{ex.Message}");
        }
    }

    private string? ResolveSafePath(string relativeOrAbsolute, out string error)
    {
        var resolved = Path.GetFullPath(Path.Combine(_workspaceRoot, relativeOrAbsolute));
        if (!IsWithinWorkspace(resolved) || !IsWithinWorkspace(ResolveRealPath(resolved)))
        {
            error = $"访问被拒绝：路径 '{relativeOrAbsolute}' 超出工作区范围。";
            return null;
        }
        error = string.Empty;
        return resolved;
    }

    private bool IsWithinWorkspace(string path) =>
        path.Equals(_workspaceRoot, PathComparison) ||
        path.StartsWith(_workspacePrefix, PathComparison);

    /// <summary>
    /// 逐级解析符号链接/目录联接。只解析叶子或最近的已存在祖先前不够：
    /// 工作区内若存在 link -&gt; C:\outside，已存在的 link\victim.txt 会绕过检查。
    /// </summary>
    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return full;

        var relative = full[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var index = 0;
        for (; index < relative.Length; index++)
        {
            var next = Path.Combine(current, relative[index]);
            if (!File.Exists(next) && !Directory.Exists(next)) break;
            current = ResolveOne(next);
        }
        for (; index < relative.Length; index++) current = Path.Combine(current, relative[index]);
        return Path.GetFullPath(current);
    }

    private static string ResolveOne(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch
        {
            // 无权限解析链接时保持字面路径；外层仍会做包含判断。
            return path;
        }
    }

    private async Task<PureToolResult> ExecuteReadFileAsync(PureToolCall call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        if (!doc.RootElement.TryGetProperty("path", out var pathElem) || pathElem.GetString() is not { } relPath)
            return new PureToolResult(call.Id, false, "缺少必需参数 'path'。");

        var target = ResolveSafePath(relPath, out var error);
        if (target is null) return new PureToolResult(call.Id, false, error);

        if (!File.Exists(target))
            return new PureToolResult(call.Id, false, $"文件不存在：'{relPath}'。");

        // 二进制文件不读入上下文：检测前缀中的 NUL 字节。
        if (await LooksBinaryAsync(target, ct).ConfigureAwait(false))
            return new PureToolResult(call.Id, false, $"'{relPath}' 看起来是二进制文件，read_file 只读取文本。");

        var hasOffset = doc.RootElement.TryGetProperty("offset", out var offsetElem);
        var hasLimit = doc.RootElement.TryGetProperty("limit", out var limitElem);
        var offset = 1;
        var limit = 2000;
        if (hasOffset && (!offsetElem.TryGetInt32(out offset) || offset < 1))
            return new PureToolResult(call.Id, false, "参数 'offset' 必须是大于 0 的整数。");
        if (hasLimit && (!limitElem.TryGetInt32(out limit) || limit < 1))
            return new PureToolResult(call.Id, false, "参数 'limit' 必须是大于 0 的整数。");
        limit = Math.Min(limit, 5000);

        // 兼容未提供 offset/limit 时的纯文本返回契约：整文件读取按行有界返回。
        // 以前这里会整篇返回，再由 Agent 循环从中间硬截断——既切断行边界，也不给续读指引。
        // 现在在工具层按行边界截断并显式给出下一次 offset，模型可以直接续读。
        if (!hasOffset && !hasLimit)
        {
            var fileInfo = new FileInfo(target);
            if (fileInfo.Length > 10 * 1024 * 1024)
                return new PureToolResult(call.Id, false,
                    $"文件大于 10MB，请使用 offset/limit 分页读取：'{relPath}'。");
            try
            {
                // UTF-8 的字符数不会超过字节数：文件在上限内时必然不会截断，
                // 直接读原文即可，既保留原始换行字节，也避免扫描两遍。
                if (fileInfo.Length <= FullReadCharLimit)
                {
                    await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, bufferSize: 8192, useAsync: true);
                    using var strictReader = new StreamReader(stream, new UTF8Encoding(false, true),
                        detectEncodingFromByteOrderMarks: true);
                    return new PureToolResult(call.Id, true,
                        await strictReader.ReadToEndAsync(ct).ConfigureAwait(false));
                }

                var full = new StringBuilder();
                var readLines = 0;
                var truncated = false;
                using (var reader = new StreamReader(target, new UTF8Encoding(false, true),
                           detectEncodingFromByteOrderMarks: true))
                {
                    while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        if (full.Length + line.Length > FullReadCharLimit)
                        {
                            truncated = true;
                            break;
                        }
                        full.AppendLine(line);
                        readLines++;
                    }
                }

                if (!truncated)
                    return new PureToolResult(call.Id, true, full.ToString());

                full.Append($"\n[文件较长，已按行截断到前 {readLines} 行。使用 offset: {readLines + 1} 继续读取]");
                return new PureToolResult(call.Id, true, full.ToString());
            }
            catch (DecoderFallbackException)
            {
                return new PureToolResult(call.Id, false,
                    $"'{relPath}' 不是 UTF-8/UTF-16 文本（可能是 GBK/ANSI 编码），请改用 run_command 处理。");
            }
        }

        var sb = new StringBuilder();
        var lineNo = 0;
        var emitted = 0;
        var hasMore = false;
        using (var reader = new StreamReader(target, new UTF8Encoding(false, true),
                   detectEncodingFromByteOrderMarks: true))
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lineNo++;
                if (lineNo < offset) continue;
                if (emitted >= limit)
                {
                    hasMore = true;
                    break;
                }
                emitted++;
                sb.Append(lineNo).Append('\t').AppendLine(line);
            }
        }

        if (lineNo == 0)
            return new PureToolResult(call.Id, true, "（文件为空）");
        if (emitted == 0)
            return new PureToolResult(call.Id, false, $"offset {offset} 超出文件总行数（共 {lineNo} 行）。");

        var lastRead = offset + emitted - 1;
        if (hasMore)
            sb.Append($"\n[已读取至第 {lastRead} 行。使用 offset: {lastRead + 1} 继续查看]");

        // 只去掉 AppendLine 引入的末尾换行，保留每一行原有的尾部空白。
        return new PureToolResult(call.Id, true, sb.ToString().TrimEnd('\r', '\n'));
    }

    // 单次整文件读取的字符上限：按行边界截断，避免把超长文件整篇塞进上下文后
    // 再被循环层从中间切断。必须明显小于 NativeAgentLoop 的 64KiB 工具结果预算，
    // 否则工具层刚排好的行边界又会被循环层二次截断，续读提示也会被切掉。
    private const int FullReadCharLimit = 48 * 1024;

    private static async Task<bool> LooksBinaryAsync(string path, CancellationToken ct)
    {
        var buffer = new byte[8192];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0) return false;

        // UTF-16/UTF-32 文本天然含 NUL 字节：先按 BOM 判定为文本，否则大量合法的
        // Windows 文本文件会被当成二进制拒绝，而 write_file/edit_file 却明确支持它们。
        if (read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
            return false;
        if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return false;

        return buffer.AsSpan(0, read).Contains((byte)0);
    }

    /// <summary>
    /// 按 BOM 严格识别可编辑文本的编码；无法识别的非 UTF-8 文本返回 null，
    /// 由调用方拒绝编辑，避免把 GBK/ANSI 文件静默写坏。
    /// </summary>
    private static (string Text, Encoding Encoding)? TryDecodeEditable(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true, true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);
        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// 写盘前守卫（对应 SWE-agent ACI 的 linter）：JSON 目标必须是可解析的结构。
    /// 直接把坏 JSON 写进磁盘，会让模型在后续回合拿到一个自己造成的、更难定位的错误；
    /// 在写盘前拒绝并指出行号，成本更低。允许注释与尾随逗号，避免误伤 JSONC 配置。
    /// </summary>
    private static string? ValidateStructuredText(string target, string content)
    {
        if (!target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var _ = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return null;
        }
        catch (JsonException error)
        {
            var line = error.LineNumber is { } number ? $"第 {number + 1} 行" : "未知位置";
            return $"写入被拒绝：'{Path.GetFileName(target)}' 的内容不是合法 JSON（{line}）。" +
                   $"请修正后再写盘，原始错误：{error.Message}";
        }
    }

    private static async Task WriteAllTextAtomicAsync(string target, string content, Encoding encoding,
        CancellationToken ct)
    {
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await File.WriteAllTextAsync(temp, content, encoding, ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private async Task<PureToolResult> ExecuteWriteFileAsync(PureToolCall call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        if (!doc.RootElement.TryGetProperty("path", out var pathElem) || pathElem.GetString() is not { } relPath)
            return new PureToolResult(call.Id, false, "缺少必需参数 'path'。");

        if (!doc.RootElement.TryGetProperty("content", out var contentElem) ||
            contentElem.ValueKind != JsonValueKind.String || contentElem.GetString() is not { } content)
            return new PureToolResult(call.Id, false, "缺少必需参数 'content'（字符串）。");

        var target = ResolveSafePath(relPath, out var error);
        if (target is null) return new PureToolResult(call.Id, false, error);

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 覆盖已有文件时沿用其原编码与 BOM，避免把 UTF-16/BOM 文件写成 UTF-8。
        var encoding = Encoding.UTF8;
        if (File.Exists(target))
        {
            var existing = TryDecodeEditable(await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false));
            if (existing is { } decoded) encoding = decoded.Encoding;
        }

        if (ValidateStructuredText(target, content) is { } invalid)
            return new PureToolResult(call.Id, false, invalid);

        FileChangeJournal.Capture(call.Id, target);
        await WriteAllTextAtomicAsync(target, content, encoding, ct).ConfigureAwait(false);
        return new PureToolResult(call.Id, true, $"已成功写入文件 '{relPath}'（{content.Length} 字符）。");
    }

    private async Task<PureToolResult> ExecuteEditFileAsync(PureToolCall call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        if (!doc.RootElement.TryGetProperty("path", out var pathElem) || pathElem.GetString() is not { } relPath)
            return new PureToolResult(call.Id, false, "缺少必需参数 'path'。");

        if (!doc.RootElement.TryGetProperty("old_string", out var oldElem) || oldElem.GetString() is not { } oldStr)
            return new PureToolResult(call.Id, false, "缺少必需参数 'old_string'。");

        if (!doc.RootElement.TryGetProperty("new_string", out var newElem) || newElem.GetString() is not { } newStr)
            return new PureToolResult(call.Id, false, "缺少必需参数 'new_string'。");

        var replaceAll = doc.RootElement.TryGetProperty("replace_all", out var repElem) && repElem.ValueKind == JsonValueKind.True;

        var target = ResolveSafePath(relPath, out var error);
        if (target is null) return new PureToolResult(call.Id, false, error);

        if (!File.Exists(target))
            return new PureToolResult(call.Id, false, $"文件不存在：'{relPath}'。");

        if (oldStr.Length == 0)
            return new PureToolResult(call.Id, false, "old_string 不能为空。");

        var decodedFile = TryDecodeEditable(await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false));
        if (decodedFile is null)
            return new PureToolResult(call.Id, false,
                $"'{relPath}' 不是 UTF-8/UTF-16 文本（可能是 GBK/ANSI 编码），为避免损坏文件已拒绝编辑。");
        var (rawContent, fileEncoding) = decodedFile.Value;

        // 换行符按文件真实字节匹配：依次尝试原样 / LF / CRLF 三种候选，选中第一个命中者，
        // 替换时使用同风格的新文本。这样均匀 CRLF、均匀 LF、以及混合换行文件都不会被整体改写。
        var lfOld = oldStr.Replace("\r\n", "\n");
        var lfNew = newStr.Replace("\r\n", "\n");
        var crlfOld = lfOld.Replace("\n", "\r\n");
        var crlfNew = lfNew.Replace("\n", "\r\n");

        string? chosenOld = null;
        string? chosenNew = null;
        foreach (var (candidateOld, candidateNew) in new[]
                 {
                     (oldStr, newStr),
                     (lfOld, lfNew),
                     (crlfOld, crlfNew),
                 })
        {
            if (candidateOld.Length == 0 || !rawContent.Contains(candidateOld, StringComparison.Ordinal)) continue;
            chosenOld = candidateOld;
            chosenNew = candidateNew;
            break;
        }

        if (chosenOld is null)
            return new PureToolResult(call.Id, false, $"在 '{relPath}' 中未找到匹配的 old_string。请使用 read_file 确认最新内容。");

        if (string.Equals(chosenOld, chosenNew, StringComparison.Ordinal))
            return new PureToolResult(call.Id, false, "old_string 与 new_string 完全相同，无需替换。");

        var count = 0;
        var idx = 0;
        while ((idx = rawContent.IndexOf(chosenOld, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += chosenOld.Length;
        }

        if (count > 1 && !replaceAll)
        {
            var lineNumbers = FindLineNumbers(rawContent, chosenOld);
            var lineHint = lineNumbers.Count > 0 ? $"（在第 {string.Join("、", lineNumbers.Take(5))} 行）" : "";
            return new PureToolResult(call.Id, false, $"在 '{relPath}' 中找到 {count} 处匹配{lineHint}。请提供更多上下文行使其唯一，或设置 replace_all: true。");
        }

        var replacedContent = replaceAll
            ? rawContent.Replace(chosenOld, chosenNew!, StringComparison.Ordinal)
            : ReplaceFirst(rawContent, chosenOld, chosenNew!);
        if (ValidateStructuredText(target, replacedContent) is { } invalid)
            return new PureToolResult(call.Id, false, invalid);

        FileChangeJournal.Capture(call.Id, target);
        await WriteAllTextAtomicAsync(target, replacedContent, fileEncoding, ct).ConfigureAwait(false);

        var preview = BuildEditPreview(rawContent, chosenOld, chosenNew!);
        var msg = $"已成功更新 '{relPath}'（替换了 {count} 处）。";
        if (!string.IsNullOrEmpty(preview)) msg += $"\n{preview}";
        return new PureToolResult(call.Id, true, msg);
    }

    private static List<int> FindLineNumbers(string text, string pattern)
    {
        var lines = new List<int>();
        var lineNo = 1;
        var idx = 0;
        var textLen = text.Length;
        var patLen = Math.Max(1, pattern.Length);
        while (idx < textLen)
        {
            var matchIdx = text.IndexOf(pattern, idx, StringComparison.Ordinal);
            if (matchIdx < 0) break;
            while (idx < matchIdx)
            {
                if (text[idx] == '\n') lineNo++;
                idx++;
            }
            lines.Add(lineNo);
            idx += patLen;
        }
        return lines;
    }

    private static string BuildEditPreview(string original, string oldStr, string newStr)
    {
        var pos = original.IndexOf(oldStr, StringComparison.Ordinal);
        if (pos < 0) return string.Empty;

        var lineNo = 1;
        for (var i = 0; i < pos; i++) if (original[i] == '\n') lineNo++;

        var oldLines = oldStr.Replace("\r\n", "\n").Split('\n');
        var newLines = newStr.Replace("\r\n", "\n").Split('\n');

        var sb = new StringBuilder();
        sb.AppendLine($"@@ 第 {lineNo} 行附近变更 @@");
        foreach (var line in oldLines.Take(5)) sb.AppendLine($"- {line}");
        if (oldLines.Length > 5) sb.AppendLine($"- ... (其余 {oldLines.Length - 5} 行)");
        foreach (var line in newLines.Take(5)) sb.AppendLine($"+ {line}");
        if (newLines.Length > 5) sb.AppendLine($"+ ... (其余 {newLines.Length - 5} 行)");
        return sb.ToString().TrimEnd();
    }

    private static string ReplaceFirst(string text, string search, string replace)
    {
        var pos = text.IndexOf(search, StringComparison.Ordinal);
        if (pos < 0) return text;
        return text[..pos] + replace + text[(pos + search.Length)..];
    }

    private PureToolResult ExecuteListDir(PureToolCall call)
    {
        var relPath = ".";
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("path", out var pathElem) && pathElem.GetString() is { } p)
                relPath = p;
        }
        catch (JsonException ex)
        {
            return new PureToolResult(call.Id, false, $"参数 JSON 无效：{ex.Message}");
        }

        var target = ResolveSafePath(relPath, out var error);
        if (target is null) return new PureToolResult(call.Id, false, error);

        if (!Directory.Exists(target))
            return new PureToolResult(call.Id, false, $"目录不存在：'{relPath}'。");

        const int limit = 500;
        var entries = Directory.GetFileSystemEntries(target)
            .OrderBy(entry => Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToArray();

        var lines = new List<string>(Math.Min(entries.Length, limit));
        foreach (var entry in entries.Take(limit))
        {
            var isDir = Directory.Exists(entry);
            lines.Add((isDir ? "[DIR]  " : "[FILE] ") + Path.GetFileName(entry));
        }
        if (entries.Length > limit)
            lines.Add($"[列表已截断，仅显示前 {limit} 项]");

        return new PureToolResult(call.Id, true, string.Join("\n", lines));
    }

    private async Task<PureToolResult> ExecuteGrepSearchAsync(PureToolCall call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        if (!doc.RootElement.TryGetProperty("pattern", out var patElem) || patElem.GetString() is not { } pattern || string.IsNullOrEmpty(pattern))
            return new PureToolResult(call.Id, false, "缺少必需参数 'pattern'。");

        var relPath = ".";
        if (doc.RootElement.TryGetProperty("path", out var pathElem) && pathElem.GetString() is { } p)
            relPath = p;

        string? globPattern = null;
        if (doc.RootElement.TryGetProperty("glob", out var globElem) && globElem.GetString() is { } g)
            globPattern = g;

        var caseSensitive = doc.RootElement.TryGetProperty("case_sensitive", out var csElem) && csElem.ValueKind == JsonValueKind.True;

        var contextLines = 0;
        if (doc.RootElement.TryGetProperty("context_lines", out var clElem) && clElem.TryGetInt32(out var cl) && cl > 0)
            // 上限 20：足够覆盖一个完整函数体，同时仍受 max_matches 与输出预算约束。
            contextLines = Math.Clamp(cl, 0, 20);

        var maxMatches = 100;
        if (doc.RootElement.TryGetProperty("max_matches", out var mmElem) && mmElem.TryGetInt32(out var mm) && mm > 0)
            maxMatches = Math.Min(mm, 1000);

        // 分页游标：命中很多时按文件分组后分页，避免有用结果被平铺输出埋掉。
        var offset = 0;
        if (doc.RootElement.TryGetProperty("offset", out var offElem) && offElem.TryGetInt32(out var off) && off > 0)
            offset = off;

        var targetDir = ResolveSafePath(relPath, out var error);
        if (targetDir is null) return new PureToolResult(call.Id, false, error);

        if (!Directory.Exists(targetDir))
            return new PureToolResult(call.Id, false, $"目录不存在：'{relPath}'。");

        Regex regex;
        try
        {
            var options = RegexOptions.Compiled;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            return new PureToolResult(call.Id, false, $"正则表达式无效：{ex.Message}");
        }

        var globRegex = string.IsNullOrEmpty(globPattern) ? null : BuildWildcardRegex(globPattern);
        if (ReferenceEquals(globRegex, NeverMatch))
            return new PureToolResult(call.Id, false, $"无效的 glob 模式：'{globPattern}'。");

        var collector = new GrepCollector(maxMatches);
        try
        {
            await Task.Run(() =>
            {
                SearchDirectoryRecursive(targetDir, regex, globRegex, collector, contextLines,
                    IgnoreMatcher(), ct);
            }, ct).ConfigureAwait(false);
        }
        catch (RegexMatchTimeoutException)
        {
            return new PureToolResult(call.Id, false,
                "正则匹配超时（灾难性回溯），搜索已中止。请简化正则表达式后重试。");
        }

        var skippedNote = collector.SkippedLargeFiles > 0
            ? $"\n[另有 {collector.SkippedLargeFiles} 个超过 {GrepMaxFileBytes / (1024 * 1024)}MB 的文件未参与检索" +
              $"（合计 {collector.SkippedLargeBytes / (1024 * 1024)}MB）；若目标可能在其中，请缩小 path 后重试]"
            : string.Empty;

        if (collector.Hits.Count == 0)
            return new PureToolResult(call.Id, true,
                "未找到匹配项。" + (skippedNote.Length > 0 ? skippedNote : string.Empty));

        return new PureToolResult(call.Id, true,
            FormatGroupedGrepOutput(collector.Hits, maxMatches, offset) + skippedNote);
    }

    /// <summary>一条检索命中；上下文行与命中行分开记录，便于分组输出。</summary>
    private readonly record struct GrepHit(string File, int Line, string Text, bool IsMatch);

    /// <summary>检索结果收集器：单独维护命中计数，避免每次判断都遍历列表。</summary>
    private sealed class GrepCollector(int maxMatches)
    {
        public List<GrepHit> Hits { get; } = new();
        public int MatchCount { get; private set; }
        public bool IsFull => MatchCount >= maxMatches;

        /// <summary>因体积过大被跳过的文件数：必须回报，否则"未找到匹配"会变成假阴性。</summary>
        public int SkippedLargeFiles { get; private set; }
        public long SkippedLargeBytes { get; private set; }

        public void AddContext(string file, int line, string text) =>
            Hits.Add(new GrepHit(file, line, text, false));

        public void AddMatch(string file, int line, string text)
        {
            Hits.Add(new GrepHit(file, line, text, true));
            MatchCount++;
        }

        public void SkipLarge(long bytes)
        {
            SkippedLargeFiles++;
            SkippedLargeBytes += bytes;
        }
    }

    /// <summary>
    /// 按文件分组、按相关度排序、支持游标分页的检索输出。
    ///
    /// 平铺输出的问题是：前 N 条可能全来自同一个大文件，真正需要的另一个文件永远排不到。
    /// 这里先按"文件被多个不同位置命中"的程度排序（命中越密集越可能是定义处），
    /// 同一文件内按行号升序，再分页返回；输出比平铺更短，且第一页信息密度更高。
    /// </summary>
    private static string FormatGroupedGrepOutput(List<GrepHit> hits, int maxMatches, int offset)
    {
        var groups = hits
            .GroupBy(hit => hit.File, StringComparer.Ordinal)
            .Select(group => new
            {
                File = group.Key,
                Hits = group.OrderBy(hit => hit.Line).ToArray(),
                MatchCount = group.Count(hit => hit.IsMatch),
            })
            // 命中次数多的文件更可能是目标；同分时按路径稳定排序，保证结果可复现。
            .OrderByDescending(group => group.MatchCount)
            .ThenBy(group => group.File, StringComparer.Ordinal)
            .ToArray();

        var emittedGroups = 0;
        var emittedHits = 0;
        var builder = new StringBuilder();
        // offset 语义是"跳过前 N 个已看过的文件"，与续页提示中的 offset 一致。
        var skipGroups = offset;

        foreach (var group in groups)
        {
            if (emittedHits >= maxMatches) break;
            if (skipGroups > 0)
            {
                skipGroups--;
                continue;
            }

            emittedGroups++;
            builder.Append("## ").Append(group.File).Append(" (").Append(group.MatchCount).AppendLine(" 处命中)");
            foreach (var hit in group.Hits)
            {
                if (emittedHits >= maxMatches) break;
                var separator = hit.IsMatch ? ": " : "- ";
                builder.Append(hit.Line).Append(separator).AppendLine(hit.Text);
                if (hit.IsMatch) emittedHits++;
            }
        }

        if (emittedGroups == 0)
            return $"[offset {offset} 超出结果范围；共 {groups.Length} 个文件、{hits.Count} 行命中]";

        var totalMatches = hits.Count(hit => hit.IsMatch);
        if (skipGroups == 0 && offset + emittedGroups < groups.Length)
        {
            builder.AppendLine()
                .Append($"[已返回 {emittedGroups} 个文件、{emittedHits} 行命中（共 {groups.Length} 个文件、{totalMatches} 行）。")
                .Append($"使用 offset: {offset + emittedGroups} 继续查看后续文件；")
                .AppendLine("或加 glob / 缩小 path / 把正则写得更具体来收窄范围]");
        }

        return builder.ToString().TrimEnd();
    }

    private void SearchDirectoryRecursive(string currentDir, Regex regex, Regex? globRegex,
        GrepCollector results, int contextLines, GitIgnoreMatcher ignore, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (results.IsFull) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                ct.ThrowIfCancellationRequested();
                if (results.IsFull) return;

                var fileName = Path.GetFileName(file);
                var relative = Path.GetRelativePath(_workspaceRoot, file).Replace('\\', '/');
                if (ignore.IsIgnored(relative)) continue;
                if (globRegex is not null &&
                    !WildcardMatches(globRegex, fileName) && !WildcardMatches(globRegex, relative))
                    continue;

                try
                {
                    var fileLength = new FileInfo(file).Length;
                    if (fileLength > GrepMaxFileBytes)
                    {
                        results.SkipLarge(fileLength);
                        continue;
                    }
                    if (contextLines > 0)
                    {
                        var lines = File.ReadLines(file).ToList();
                        for (var i = 0; i < lines.Count; i++)
                        {
                            if (regex.IsMatch(lines[i]))
                            {
                                var start = Math.Max(0, i - contextLines);
                                var end = Math.Min(lines.Count - 1, i + contextLines);
                                for (var c = start; c <= end; c++)
                                {
                                    if (c == i) results.AddMatch(relative, c + 1, lines[c].TrimEnd());
                                    else results.AddContext(relative, c + 1, lines[c].TrimEnd());
                                }
                                if (results.IsFull) return;
                                i = end;
                            }
                        }
                    }
                    else
                    {
                        var lineNum = 0;
                        foreach (var line in File.ReadLines(file))
                        {
                            lineNum++;
                            if (regex.IsMatch(line))
                            {
                                results.AddMatch(relative, lineNum, line.TrimEnd());
                                if (results.IsFull) return;
                            }
                        }
                    }
                }
                catch (RegexMatchTimeoutException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // 单个文件读取失败（权限/占用/编码）时跳过，不中止整个搜索。
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (RegexMatchTimeoutException) { throw; }
        catch
        {
            // 目录在遍历中被删除或权限变化：停止该目录，继续其它分支。
            return;
        }

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                ct.ThrowIfCancellationRequested();
                if (results.IsFull) return;

                var dirName = Path.GetFileName(subDir);
                if (ExcludedDirectories.Contains(dirName)) continue;
                // 不跟随目录符号链接/联接，避免扫描到工作区外或形成环。
                if (new DirectoryInfo(subDir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (ignore.IsIgnored(Relative(subDir) + "/")) continue;

                SearchDirectoryRecursive(subDir, regex, globRegex, results, contextLines, ignore, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (RegexMatchTimeoutException) { throw; }
        catch { }
    }

    private PureToolResult ExecuteGlobFiles(PureToolCall call, CancellationToken ct)
    {
        using var span = PerfTracer.Step("tool.glob", "tool", "glob_files");
        var relPath = ".";
        var pattern = "*";
        var maxResults = 100;

        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("pattern", out var patElem) && patElem.GetString() is { } p && !string.IsNullOrWhiteSpace(p))
                pattern = p;
            if (doc.RootElement.TryGetProperty("path", out var pathElem) && pathElem.GetString() is { } path)
                relPath = path;
            if (doc.RootElement.TryGetProperty("max_results", out var mrElem) && mrElem.TryGetInt32(out var mr) && mr > 0)
                maxResults = Math.Min(mr, 1000);
        }
        catch (JsonException ex)
        {
            span.Fail("invalid_arguments");
            return new PureToolResult(call.Id, false, $"参数 JSON 无效：{ex.Message}");
        }

        span.Tag("path", relPath);
        span.Tag("pattern", pattern);
        var targetDir = ResolveSafePath(relPath, out var error);
        if (targetDir is null) { span.Fail("path_rejected"); return new PureToolResult(call.Id, false, error); }

        if (!Directory.Exists(targetDir))
            return new PureToolResult(call.Id, false, $"目录不存在：'{relPath}'。");

        var globRegex = BuildWildcardRegex(pattern);
        if (ReferenceEquals(globRegex, NeverMatch))
            return new PureToolResult(call.Id, false, $"无效的通配符模式：'{pattern}'。");

        var matched = new List<string>();
        {
            using var scanSpan = PerfTracer.Step("tool.glob.scan");
            EnumerateGlobRecursive(targetDir, globRegex, maxResults, matched, IgnoreMatcher(), ct);
            scanSpan.Tag("matched", matched.Count);
            scanSpan.Tag("truncated", matched.Count >= maxResults);
        }

        if (matched.Count == 0)
            return new PureToolResult(call.Id, true, "未找到匹配文件。");

        var output = string.Join("\n", matched);
        if (matched.Count >= maxResults)
            output +=
                $"\n[已达到最大数量（{maxResults} 条），列表被截断。收窄方式：让 pattern 更具体（如 src/**/*.cs）、" +
                "指定更小的 path，或提高 max_results 后重试]";

        return new PureToolResult(call.Id, true, output);
    }

    private void EnumerateGlobRecursive(string currentDir, Regex? regex, int maxResults, List<string> results, GitIgnoreMatcher ignore, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (results.Count >= maxResults) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= maxResults) return;

                var fileName = Path.GetFileName(file);
                var rel = Path.GetRelativePath(_workspaceRoot, file).Replace('\\', '/');
                if (ignore.IsIgnored(rel)) continue;
                if (WildcardMatches(regex, fileName) || WildcardMatches(regex, rel))
                {
                    results.Add(rel);
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= maxResults) return;

                var dirName = Path.GetFileName(subDir);
                if (ExcludedDirectories.Contains(dirName)) continue;
                if (new DirectoryInfo(subDir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (ignore.IsIgnored(Relative(subDir) + "/")) continue;

                EnumerateGlobRecursive(subDir, regex, maxResults, results, ignore, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static readonly Regex NeverMatch = new("[^\\s\\S]", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>预编译通配符正则；null 表示匹配任意路径，NeverMatch 表示模式非法。</summary>
    private static Regex? BuildWildcardRegex(string pattern)
    {
        if (pattern is "*" or "**/*" or "**") return null;
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*/", @"(?:.*/)?")
            .Replace(@"\*\*", @".*")
            .Replace(@"\*", @"[^/\\]*")
            .Replace(@"\?", ".") + "$";
        var options = RegexOptions.NonBacktracking;
        if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
        try { return new Regex(regexPattern, options, TimeSpan.FromSeconds(1)); }
        catch { return NeverMatch; }
    }

    private static bool WildcardMatches(Regex? regex, string input) => regex is null || regex.IsMatch(input);

    private async Task<PureToolResult> ExecuteRunCommandAsync(PureToolCall call, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        if (!doc.RootElement.TryGetProperty("command", out var cmdElem) || cmdElem.GetString() is not { } cmd)
            return new PureToolResult(call.Id, false, "缺少必需参数 'command'。");

        var workingDirectory = _workspaceRoot;
        if (doc.RootElement.TryGetProperty("cwd", out var cwdElem) && cwdElem.ValueKind == JsonValueKind.String &&
            cwdElem.GetString() is { Length: > 0 } relativeCwd)
        {
            var resolved = ResolveSafePath(relativeCwd, out var cwdError);
            if (resolved is null) return new PureToolResult(call.Id, false, cwdError);
            if (!Directory.Exists(resolved))
                return new PureToolResult(call.Id, false, $"工作目录不存在：'{relativeCwd}'。");
            workingDirectory = resolved;
        }

        // 构建/测试命令经常超过 2 分钟；默认给 5 分钟，仍保留 600 秒硬上限。
        var timeoutSeconds = 300;
        if (doc.RootElement.TryGetProperty("timeout_seconds", out var timeoutElem) &&
            timeoutElem.TryGetInt32(out var requestedTimeout) && requestedTimeout > 0)
            // 上限 3600 秒：完整构建/测试套件可能远超 10 分钟，600 秒的硬顶会误杀正常任务。
            timeoutSeconds = Math.Clamp(requestedTimeout, 1, 3600);

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (isWindows)
        {
            // chcp 65001：让 cmd 及多数控制台程序输出 UTF-8，避免中文 Windows 下乱码。
            psi.Arguments = $"/d /s /c \"chcp 65001>nul & {cmd}\"";
        }
        else
        {
            // ArgumentList 避免 .NET 按 Windows 规则重新解析并剥离命令中的内层引号。
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(cmd);
        }

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var span = PerfTracer.Step("tool.process", "tool", "run_command");
        span.Tag("command", cmd);
        span.Tag("cwd", workingDirectory);
        span.Tag("timeoutSeconds", timeoutSeconds);

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            span.Fail(ex.GetType().Name);
            return new PureToolResult(call.Id, false, $"进程启动失败：{ex.Message}");
        }
        span.Tag("startMs", (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        // 有界读取：只保留头部 32KB 与尾部 32KB，中间内容边读边丢弃（仍需排空管道，防止子进程因管道写满而卡死）。
        var stdoutTask = ReadHeadTailAsync(process.StandardOutput, CommandOutputHeadChars, CommandOutputTailChars, linkedCts.Token);
        var stderrTask = ReadHeadTailAsync(process.StandardError, CommandOutputHeadChars, CommandOutputTailChars, linkedCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                timedOut = true;
            }
        }

        if (ct.IsCancellationRequested) { span.Cancel(); throw new OperationCanceledException(ct); }

        var stdout = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeReadAsync(stderrTask).ConfigureAwait(false);

        span.Tag("exitCode", timedOut ? -1 : process.ExitCode);
        span.Tag("timedOut", timedOut);
        span.Tag("stdoutBytes", stdout.TotalChars);
        span.Tag("stderrBytes", stderr.TotalChars);
        span.Tag("dropped", stdout.Dropped || stderr.Dropped);
        // 性能页只带一个有界头部（tag 上限 256 字符），完整输出仍在工具结果里。
        if (Head(stdout.Head, 220) is { Length: > 0 } stdoutHead) span.Tag("stdoutHead", stdoutHead);
        if (Head(stderr.Head, 220) is { Length: > 0 } stderrHead) span.Tag("stderrHead", stderrHead);
        if (timedOut) span.Fail("timeout");
        else if (process.ExitCode != 0) span.Tag("ok", false);

        if (timedOut)
        {
            var partial = BuildCommandOutput(-1, stdout, stderr);
            return new PureToolResult(call.Id, false,
                $"命令执行超时（超过 {timeoutSeconds} 秒）已被强行终止。\n" + partial);
        }

        var output = BuildCommandOutput(process.ExitCode, stdout, stderr);
        return new PureToolResult(call.Id, process.ExitCode == 0, output);
    }

    // 超过该体积的文件不参与逐行正则扫描（内存与耗时随体积线性增长）；
    // 跳过时会在结果里明确告知，避免模型把"跳过"误读成"不存在"。
    private const long GrepMaxFileBytes = 5 * 1024 * 1024;

    private const int CommandOutputHeadChars = 32 * 1024;
    private const int CommandOutputTailChars = 32 * 1024;

    private static string Head(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private readonly record struct CapturedOutput(string Head, string Tail, bool Dropped, long TotalChars);

    /// <summary>
    /// 读取命令输出：头部累积到 headLimit；其后内容进入尾部环形缓冲（tailLimit），溢出时丢弃最旧的尾部字符。
    /// 只统计总长度，不保留中间内容，内存上限约 headLimit + tailLimit。
    /// </summary>
    private static async Task<CapturedOutput> ReadHeadTailAsync(StreamReader reader, int headLimit, int tailLimit,
        CancellationToken ct)
    {
        var head = new StringBuilder(capacity: Math.Min(headLimit, 8192));
        var tail = new CircularCharBuffer(tailLimit);
        var buffer = new char[8192];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                var toHead = Math.Min(read, headLimit - head.Length);
                if (toHead > 0) head.Append(buffer, 0, toHead);
                if (toHead < read) tail.Append(buffer.AsSpan(toHead, read - toHead));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return new CapturedOutput(head.ToString(), tail.ToString(), total > (long)headLimit + tailLimit, total);
    }

    private static async Task<CapturedOutput> SafeReadAsync(Task<CapturedOutput> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return new CapturedOutput(string.Empty, string.Empty, false, 0); }
    }

    private static string BuildCommandOutput(int exitCode, CapturedOutput stdout, CapturedOutput stderr)
    {
        var outText = FormatCapturedOutput(stdout, 30 * 1024);
        var errText = FormatCapturedOutput(stderr, 10 * 1024);
        if (stdout.Dropped) outText += "\n[标准输出超过 64KB，中间内容已丢弃]";
        if (stderr.Dropped) errText += "\n[标准错误超过 64KB，中间内容已丢弃]";
        var code = exitCode < 0 ? "aborted" : exitCode.ToString();
        return $"ExitCode: {code}\nSTDOUT:\n{outText}\nSTDERR:\n{errText}".Trim();
    }

    /// <summary>把“头部 + 尾部”的有界快照格式化成与 <see cref="TruncateOutput"/> 相同形状的显示文本。</summary>
    private static string FormatCapturedOutput(CapturedOutput captured, int maxChars)
    {
        // 未超出 head+tail 容量时，两段拼起来就是完整输出。
        if (captured.TotalChars <= captured.Head.Length + captured.Tail.Length)
            return TruncateOutput(captured.Head + captured.Tail, maxChars);

        var head = new StringBuilder();
        var used = 0;
        foreach (var line in captured.Head.Split('\n').Take(50))
        {
            if (used + line.Length + 1 > maxChars * 2 / 3) break;
            head.Append(line).Append('\n');
            used += line.Length + 1;
        }

        var tail = new StringBuilder();
        used = 0;
        foreach (var line in captured.Tail.Split('\n').TakeLast(100).Reverse())
        {
            if (used + line.Length + 1 > maxChars / 3) break;
            tail.Insert(0, line + "\n");
            used += line.Length + 1;
        }

        // 中部是否还删过环形缓冲之外的字符，无法得知行数；用字符数给出折叠量。
        var omitted = captured.TotalChars - captured.Head.Length - captured.Tail.Length;
        return head + $"\n... [中间内容已折叠截断（总输出 {captured.TotalChars} 字符，折叠 {omitted} 字符）] ...\n\n" + tail;
    }

    private static string TruncateOutput(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;

        // 不切分整串：逐行扫描，先收集头部，再从末尾反向收集尾部。
        var head = new StringBuilder();
        var used = 0;
        var lineCount = 0;
        var headDone = false;
        var pos = 0;
        // 与旧实现一致：只考虑前 50 行（含空行），超出字符预算即停止。
        while (pos <= text.Length)
        {
            var newline = text.IndexOf('\n', pos);
            var end = newline < 0 ? text.Length : newline;
            lineCount++;
            var lineLength = end - pos;
            if (!headDone)
            {
                if (lineCount > 50 || used + lineLength + 1 > maxChars * 2 / 3) headDone = true;
                else
                {
                    head.Append(text, pos, lineLength).Append('\n');
                    used += lineLength + 1;
                }
            }
            if (newline < 0) break;
            pos = newline + 1;
        }

        if (lineCount <= 150)
            return text[..SafeSliceLength(text, maxChars)] + $"\n... [已截断，总长度 {text.Length} 字符] ...";

        var tail = new StringBuilder();
        used = 0;
        // 与旧实现（Split('\n') 后 TakeLast(100)）一致：以换行结尾时末尾空行也算一行。
        // 从最后一行向前逐行扫描（不切分整串），先限定最后至多 100 行，再按字符预算保留。
        var lineStart = text.Length;
        if (lineStart > 0 && text[^1] != '\n')
        {
            var lastNewline = text.LastIndexOf('\n');
            lineStart = lastNewline < 0 ? 0 : lastNewline + 1;
        }
        var lineEnd = text.Length;
        for (var examined = 0; examined < 100; examined++)
        {
            var lineLength = lineEnd - lineStart;
            if (used + lineLength + 1 > maxChars / 3) break;
            tail.Insert(0, '\n');
            tail.Insert(0, text.AsSpan(lineStart, lineLength));
            used += lineLength + 1;
            if (lineStart == 0) break;
            var newline = lineStart - 1;
            lineEnd = newline;
            lineStart = newline == 0 ? 0 : text.LastIndexOf('\n', newline - 1) + 1;
        }

        var omitted = lineCount - 150;
        return head + $"\n... [中间 {omitted} 行已折叠截断（总输出 {text.Length} 字符）] ...\n\n" + tail;
    }

    /// <summary>固定容量字符环形缓冲：追加 O(1)，溢出时丢弃最旧字符，只保留最近 tailLimit 个字符。</summary>
    private sealed class CircularCharBuffer(int capacity)
    {
        private readonly char[] _buffer = new char[capacity];
        private int _start;
        private int _count;

        public void Append(ReadOnlySpan<char> source)
        {
            while (!source.IsEmpty)
            {
                // 保留最近 capacity 个字符：先丢弃放不下的最旧字符，再写入。
                var overflow = _count + source.Length - capacity;
                var drop = overflow > 0 ? Math.Min(overflow, _count) : 0;
                _start = (_start + drop) % capacity;
                _count -= drop;

                var take = Math.Min(source.Length, capacity - _count);
                var write = (_start + _count) % capacity;
                var first = Math.Min(take, capacity - write);
                source[..first].CopyTo(_buffer.AsSpan(write, first));
                if (take > first) source.Slice(first, take - first).CopyTo(_buffer);
                _count += take;
                source = source[take..];
            }
        }

        public override string ToString()
        {
            if (_count == 0) return string.Empty;
            var first = Math.Min(_count, capacity - _start);
            return first == _count
                ? new string(_buffer, _start, _count)
                : string.Concat(_buffer.AsSpan(_start, first), _buffer.AsSpan(0, _count - first));
        }
    }

    private static int SafeSliceLength(string text, int length)
    {
        if (length <= 0) return 0;
        if (length >= text.Length) return text.Length;
        // 不要劈开代理对。
        return char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
    }

    private PureToolResult ExecuteUpdatePlan(PureToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (!doc.RootElement.TryGetProperty("steps", out var stepsElem) || stepsElem.ValueKind != JsonValueKind.Array)
                return new PureToolResult(call.Id, false, "缺少必需参数 'steps' 数组。");

            if (stepsElem.GetArrayLength() > 50)
                return new PureToolResult(call.Id, false, "计划步骤过多，最多允许 50 个。");

            var steps = new List<PlanStep>();
            var inProgressCount = 0;
            foreach (var step in stepsElem.EnumerateArray())
            {
                if (!step.TryGetProperty("title", out var titleElem) || titleElem.GetString() is not { Length: > 0 } title)
                    return new PureToolResult(call.Id, false, "每个步骤必须包含非空 'title'。");
                if (title.Length > 200)
                    return new PureToolResult(call.Id, false, "步骤标题过长，最多 200 字符。");

                if (!step.TryGetProperty("status", out var statusElem) || statusElem.GetString() is not { } rawStatus)
                    return new PureToolResult(call.Id, false, "每个步骤必须包含 'status'。");
                var status = rawStatus.ToLowerInvariant();

                if (status is not ("pending" or "in_progress" or "completed"))
                    return new PureToolResult(call.Id, false, $"无效的状态 '{status}'，仅支持 pending / in_progress / completed。");

                if (status == "in_progress") inProgressCount++;
                steps.Add(new PlanStep(title, status));
            }

            if (inProgressCount > 1)
                return new PureToolResult(call.Id, false, "同时最多只能有 1 个步骤处于 'in_progress' 状态。");

            string? notes = null;
            if (doc.RootElement.TryGetProperty("notes", out var notesElem) && notesElem.GetString() is { } n)
                notes = n;

            CurrentPlan = new AgentPlan(steps, notes);

            var sb = new StringBuilder();
            sb.AppendLine("[计划已更新]");
            for (var i = 0; i < steps.Count; i++)
            {
                var icon = steps[i].Status switch
                {
                    "completed" => "[x]",
                    "in_progress" => "[>]",
                    _ => "[ ]"
                };
                sb.AppendLine($"{icon} {i + 1}. {steps[i].Title} ({steps[i].Status})");
            }
            if (!string.IsNullOrEmpty(notes))
                sb.AppendLine($"备注: {notes}");

            return new PureToolResult(call.Id, true, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return new PureToolResult(call.Id, false, $"更新计划失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 原生内置工具的完整 JSON Schema 契约列表。
    /// 结果在进程内只构建一次：每轮请求与模型客户端都会读取它，
    /// 重复分配既浪费 GC，也让同一会话的工具前缀无法保持引用稳定。
    /// </summary>
    public static IReadOnlyList<PureToolDefinition> GetBuiltinToolDefinitions() => BuiltinDefinitions;

    private static readonly PureToolDefinition[] BuiltinDefinitions =
    [
        new("read_file",
            "读取工作区内的文本文件，返回内容（分页时带行号前缀）。改动文件前先读它确认现状；" +
            "不要用它读二进制文件或大目录。超过上限会按行截断并给出 offset，用同一个 offset 继续读即可。",
            """
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "相对于工作区的相对路径或绝对路径" },
                    "offset": { "type": "integer", "description": "起始行号（从 1 开始），可选" },
                    "limit": { "type": "integer", "description": "读取的最大行数，可选，默认 2000" }
                },
                "required": ["path"]
            }
            """),
        new("write_file",
            "写入整个文件（覆盖），父目录不存在会自动创建。适合新建文件或整体重写；" +
            "已有文件的小改动一律用 edit_file，避免覆盖掉未读到的内容。",
            """
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "文件相对路径" },
                    "content": { "type": "string", "description": "待写入的完整内容" }
                },
                "required": ["path", "content"]
            }
            """),
        new("edit_file",
            "把文件中的一段原文精确替换为新文本，自动适配 CRLF/LF。old_string 必须与文件内容逐字一致且唯一；" +
            "匹配到多处时会拒绝并报出行号，此时补上前后几行上下文使其唯一。成功后返回局部 diff 便于确认。",
            """
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "待修改的文件相对路径" },
                    "old_string": { "type": "string", "description": "待替换的原文本，必须在文件中精确且唯一匹配" },
                    "new_string": { "type": "string", "description": "替换后的新文本" },
                    "replace_all": { "type": "boolean", "description": "是否替换所有出现的实例（默认 false）" }
                },
                "required": ["path", "old_string", "new_string"]
            }
            """),
        new("list_dir",
            "列出目录下的直接子项，标记 [DIR]/[FILE]，不递归。适合先看项目结构；" +
            "按名字找文件用 glob_files，按内容找用 grep_search——它们会递归并遵守 .gitignore。",
            """
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "目录相对路径，默认为当前根目录" }
                }
            }
            """),
        new("grep_search",
            "用正则在工作区文件内容中检索。结果按文件分组、命中越多越靠前，每组返回行号与内容；" +
            "自动跳过 .git/bin/obj/node_modules 并遵守 .gitignore。命中很多时用 offset 分页查看后续文件。" +
            "已知符号名时优先用它定位；需要看函数上下文就设 context_lines（0-5）。结果为空是正常结论，不代表工具失败。",
            """
            {
                "type": "object",
                "properties": {
                    "pattern": { "type": "string", "description": "正则表达式模式" },
                    "path": { "type": "string", "description": "搜索起始子目录，默认根目录" },
                    "glob": { "type": "string", "description": "文件名过滤模式，例如 *.cs" },
                    "case_sensitive": { "type": "boolean", "description": "是否区分大小写（默认 false）" },
                    "context_lines": { "type": "integer", "description": "匹配行周围附带的上下文行数（0-5，默认 0）" },
                    "max_matches": { "type": "integer", "description": "本页最大匹配行数（默认 100）" },
                    "offset": { "type": "integer", "description": "按文件分页的游标：跳过前 N 个命中文件，用于查看后续文件（默认 0）" }
                },
                "required": ["pattern"]
            }
            """),
        new("glob_files",
            "按路径通配符递归查找文件，返回相对路径列表（如 **/*.cs、src/**/*.json）。" +
            "适合确认某类文件是否存在；不知道内容在哪时用 grep_search 而不是逐个 glob。",
            """
            {
                "type": "object",
                "properties": {
                    "pattern": { "type": "string", "description": "通配符模式，例如 **/*.cs 或 src/**/*.json" },
                    "path": { "type": "string", "description": "起始相对目录，默认根目录" },
                    "max_results": { "type": "integer", "description": "最大返回数量（默认 100）" }
                },
                "required": ["pattern"]
            }
            """),
        new("run_command",
            "在工作区内执行一条 shell 命令，返回 ExitCode 与标准输出/错误。用于构建、测试、git 等内置工具做不到的事；" +
            "读写文件优先用内置工具。超时会终止整个进程树，输出过长只保留头尾并标注折叠。",
            """
            {
                "type": "object",
                "properties": {
                    "command": { "type": "string", "description": "Shell 命令（CLI 及其参数）" },
                    "cwd": { "type": "string", "description": "工作目录（相对工作区，可选）" },
                    "timeout_seconds": { "type": "integer", "description": "超时秒数，1-600，默认 300" }
                },
                "required": ["command"]
            }
            """),
        new("update_plan",
            "创建或更新结构化执行计划，用于多步骤任务追踪进度。同一时刻只能有 1 个步骤是 in_progress；" +
            "每完成一步就更新一次。计划会在后续回合保留，长任务里它是防止偏离的锚点。",
            """
            {
                "type": "object",
                "properties": {
                    "steps": {
                        "type": "array",
                        "description": "任务执行步骤列表",
                        "items": {
                            "type": "object",
                            "properties": {
                                "title": { "type": "string", "description": "步骤简述" },
                                "status": { "type": "string", "enum": ["pending", "in_progress", "completed"], "description": "步骤状态" }
                            },
                            "required": ["title", "status"]
                        }
                    },
                    "notes": { "type": "string", "description": "整体计划补充说明或风险备忘（可选）" }
                },
                "required": ["steps"]
            }
            """)
    ];
}
