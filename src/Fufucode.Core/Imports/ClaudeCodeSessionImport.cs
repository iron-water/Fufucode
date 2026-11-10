using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Imports;

/// <summary>导入选项。默认只收当前项目范围内的转录，只读原始转录，不动它一个字节。</summary>
public sealed record ClaudeImportOptions(
    IReadOnlyList<string>? SourceDirectories = null,
    /// <summary>把整批历史强制挂到该项目（忽略转录自带的 cwd）。</summary>
    string? ProjectPath = null,
    bool DryRun = false,
    bool IncludeSubagents = false,
    bool Force = false,
    string? ClaudeProjectsDirectory = null,
    /// <summary>范围过滤：只导入原工作目录在该目录之下的转录。null 表示不过滤。</summary>
    string? ScopePath = null);

/// <summary>单条转录的导入结局。<see cref="Status"/> 取 imported / skipped / failed。</summary>
public sealed record ClaudeImportEntry(string TranscriptPath, string? SessionId, string? ProjectPath,
    string? OriginalProjectPath, int MessageCount, long Bytes, string Status, string? Detail);

public sealed record ClaudeImportReport(IReadOnlyList<ClaudeImportEntry> Entries)
{
    public int Imported => Entries.Count(entry => entry.Status == "imported");
    public int Skipped => Entries.Count(entry => entry.Status == "skipped");
    public int Failed => Entries.Count(entry => entry.Status == "failed");
    public long Bytes => Entries.Sum(entry => entry.Bytes);
}

/// <summary>
/// 把 Claude Code 的历史转录导入为本机 Fufucode 会话（fumo 后端）。
///
/// 为什么是“转换”而不是“挂载”：Claude Code 的 jsonl 属于它自己的运行期格式
/// （工具结果内联、快照与计费行混在一起），Fufucode 的会话文件是
/// <c>{"version":2,"messages":[...]}</c>。直接复制过去只会得到一份打不开的文件。
/// 这里把正文抽出来写成原生会话文件，并在桌面元数据里登记项目与会话，
/// 于是它们出现在会话列表里、能读、也能接着聊。
///
/// 原始转录保持只读；重复执行幂等（同一 id 已导入则跳过），只有 <see cref="ClaudeImportOptions.Force"/>
/// 允许覆盖本工具先前写出的文件。
/// </summary>
public static class ClaudeCodeSessionImport
{
    /// <summary>会话名前缀：让导入的历史在列表里一眼可辨，不冒充本机新建会话。</summary>
    public const string NamePrefix = "[CC] ";

    private const int MaxNameChars = 60;

    public static string DefaultClaudeProjectsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static ClaudeImportReport Run(AppDataLayout layout, DesktopMetadataStore metadata,
        ClaudeImportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);

        var entries = new List<ClaudeImportEntry>();
        foreach (var transcript in Candidates(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(ImportOne(layout, metadata, options, transcript));
        }
        return new ClaudeImportReport(entries);
    }

    /// <summary>
    /// 候选转录：同一会话 id 只留最大的那份（不同项目目录里可能各有一份），
    /// 再按 <see cref="ClaudeImportOptions.ScopePath"/> 过滤掉别的项目的历史。
    /// </summary>
    private static IEnumerable<string> Candidates(ClaudeImportOptions options)
    {
        var byId = new Dictionary<string, (string Path, long Length)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Discover(options))
        {
            var id = SessionIdOf(path);
            long length;
            try { length = new FileInfo(path).Length; }
            catch (IOException) { continue; }
            if (!byId.TryGetValue(id, out var existing) || length > existing.Length) byId[id] = (path, length);
        }
        foreach (var candidate in byId.Values)
            yield return candidate.Path;
    }

    /// <summary>
    /// 转录文件枚举。Claude Code 的布局是
    /// <c>projects/&lt;编码路径&gt;/&lt;session&gt;.jsonl</c>，子代理转录在
    /// <c>projects/&lt;编码路径&gt;/&lt;session&gt;/subagents/*.jsonl</c>；
    /// 显式 <c>--from</c> 也可能直接指向单个项目目录，所以按相对深度判定而不是写死根。
    /// </summary>
    private static IEnumerable<string> Discover(ClaudeImportOptions options)
    {
        var roots = options.SourceDirectories is { Count: > 0 } given
            ? given
            : [options.ClaudeProjectsDirectory ?? DefaultClaudeProjectsDirectory];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file);
                var depth = relative.Count(character => character == Path.DirectorySeparatorChar);
                // depth 0：根目录直接放转录；depth 1：一个项目目录里的会话。
                if (depth > 1 && !(options.IncludeSubagents && relative.Contains("subagents", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (seen.Add(Path.GetFullPath(file))) yield return file;
            }
        }
    }

    private static ClaudeImportEntry ImportOne(AppDataLayout layout, DesktopMetadataStore metadata,
        ClaudeImportOptions options, string transcriptPath)
    {
        ClaudeTranscript transcript;
        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            transcript = ClaudeTranscriptReader.Parse(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ClaudeImportEntry(transcriptPath, null, null, null, 0, 0, "failed",
                "无法读取转录：" + error.Message);
        }

        var size = new FileInfo(transcriptPath).Length;
        if (transcript.Messages.Count == 0)
            return new ClaudeImportEntry(transcriptPath, null, null, transcript.WorkingDirectory, 0, size,
                "skipped", "没有可导入的对话正文");

        // 范围判定用转录自己的 cwd（折叠之前），这样已删除的 worktree 仍算在本仓范围内。
        // 双向包含：在当前项目里开的会话算，记录着上级目录（伞目录/解决方案根）的会话也算。
        var originalPath = options.ProjectPath ?? transcript.WorkingDirectory;
        if (options.ScopePath is { Length: > 0 } scope &&
            (originalPath is null || !(IsUnder(originalPath, scope) || IsAncestorOf(originalPath, scope))))
            return new ClaudeImportEntry(transcriptPath, SessionIdOf(transcriptPath), null,
                transcript.WorkingDirectory, transcript.Messages.Count, size, "skipped",
                "不在指定项目范围内（--all 包含全部）");

        var resolved = ResolveProjectPath(originalPath);
        if (resolved is null)
            return new ClaudeImportEntry(transcriptPath, null, null, transcript.WorkingDirectory,
                transcript.Messages.Count, size, "skipped", "项目目录不存在，无法挂到任何项目");

        var (projectPath, foldedFrom) = resolved.Value;
        var sessionId = SessionIdOf(transcriptPath);

        // 会话文件路径由项目 id 推导：先登记项目，再算文件路径。
        ProjectRecord project;
        try
        {
            project = options.DryRun ? new ProjectRecord("dry-run", projectPath)
                : metadata.GetOrCreateProject(projectPath);
        }
        catch (Exception error) when (error is IOException or DirectoryNotFoundException or ArgumentException)
        {
            return new ClaudeImportEntry(transcriptPath, sessionId, projectPath, transcript.WorkingDirectory,
                transcript.Messages.Count, size, "failed", "项目登记失败：" + error.Message);
        }
        var filePath = NativeSessionFactory.SessionFilePath(layout, project.Id, sessionId);

        if (SkipReason(metadata, sessionId, filePath, options.Force) is { } conflict)
            return new ClaudeImportEntry(transcriptPath, sessionId, projectPath, transcript.WorkingDirectory,
                transcript.Messages.Count, size, "skipped", conflict);

        if (!options.DryRun)
        {
            var name = BuildName(transcript);
            var opened = transcript.UpdatedUtc ?? transcript.StartedUtc ?? File.GetLastWriteTimeUtc(transcriptPath);
            try
            {
                WriteSessionFile(filePath, transcript, projectPath, transcriptPath);
                SessionAdoption.WriteMarker(layout, project.Id, projectPath);
                if (metadata.AdoptSession(sessionId, project.Id, filePath))
                {
                    metadata.SetSessionName(sessionId, name);
                    metadata.SetSessionLastOpened(sessionId, opened);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                return new ClaudeImportEntry(transcriptPath, sessionId, projectPath, transcript.WorkingDirectory,
                    transcript.Messages.Count, size, "failed", "写入失败：" + error.Message);
            }
        }

        return new ClaudeImportEntry(transcriptPath, sessionId, projectPath, transcript.WorkingDirectory,
            transcript.Messages.Count, size, "imported",
            foldedFrom is null ? null : "原项目目录不存在，已折叠到上级：" + foldedFrom);
    }

    /// <summary>
    /// 幂等判据。已登记的会话一律不动（--force 也不覆盖元数据，避免把用户改名/打开的记录顶掉）；
    /// 只有“文件在、索引里没有”这种上次写入中断的残留才允许 --force 重写。
    /// </summary>
    private static string? SkipReason(DesktopMetadataStore metadata, string sessionId, string filePath,
        bool force)
    {
        if (SessionExists(metadata, sessionId))
            return "同一会话 id 已登记；如需重导请先删除该会话";
        if (File.Exists(filePath) && !force)
            return "会话文件已存在；如需重导用 --force";
        return null;
    }

    private static bool SessionExists(DesktopMetadataStore metadata, string sessionId)
    {
        try { metadata.GetSession(sessionId); return true; }
        catch (KeyNotFoundException) { return false; }
    }

    /// <summary>
    /// 会话文件按 FUMO 自己的持久化形态写出：与 <c>NativeCSharpChatSession.PersistHistory</c> 同形，
    /// 额外带一个 <c>importedFrom</c> 溯源块（解析侧只读已知字段，未知字段安全忽略）。
    /// </summary>
    private static void WriteSessionFile(string filePath, ClaudeTranscript transcript, string projectPath,
        string transcriptPath)
    {
        AtomicJsonFile.Write(filePath, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WritePropertyName("messages");
            JsonSerializer.Serialize(writer, transcript.Messages);
            writer.WriteNull("modelId");
            writer.WriteString("thinkingLevel", "off");
            writer.WriteString("executionMode", "manual");
            writer.WriteStartObject("importedFrom");
            writer.WriteString("format", "claude-code-jsonl");
            writer.WriteString("transcript", transcriptPath);
            writer.WriteString("project", projectPath);
            if (transcript.WorkingDirectory is { Length: > 0 } cwd) writer.WriteString("workingDirectory", cwd);
            writer.WriteString("importedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static string BuildName(ClaudeTranscript transcript)
    {
        var text = transcript.Title ?? transcript.FirstUserText ?? "Claude Code 会话";
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length > MaxNameChars) single = single[..MaxNameChars] + "…";
        return NamePrefix + single;
    }

    /// <summary>路径是否在给定目录之下（含目录本身）。</summary>
    private static bool IsUnder(string path, string scope)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope));
            return full.Equals(root, comparison) ||
                   full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 上级目录也算相关（伞目录里开的会话服务于本仓），但盘根不算——
    /// 否则一次 <c>C:\</c> 下的会话就会把所有无关项目拖进来。
    /// </summary>
    private static bool IsAncestorOf(string path, string scope)
    {
        if (!IsUnder(scope, path)) return false;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Path.GetPathRoot(full) != full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 项目目录必须真实存在才能登记（元数据按绝对路径建行）。
    /// 已删除的 worktree/临时目录向上折叠到最近的现存祖先，并把原始路径留在报告里。
    /// </summary>
    private static (string Path, string? FoldedFrom)? ResolveProjectPath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var original = full;
        while (!Directory.Exists(full))
        {
            var parent = Directory.GetParent(full);
            if (parent is null) return null;
            full = Path.TrimEndingDirectorySeparator(parent.FullName);
            if (Path.GetPathRoot(full) == full) return null; // 折叠到盘根说明原本就不在可用范围内。
        }
        return (full, string.Equals(full, original, StringComparison.OrdinalIgnoreCase) ? null : original);
    }

    /// <summary>
    /// 会话 id 沿用 Claude 的转录文件名，便于对账；文件名不合规时退化为路径哈希。
    /// 桌面端会话 id 只要求字母数字与 <c>._-</c>，GUID 正好满足。
    /// </summary>
    private static string SessionIdOf(string transcriptPath)
    {
        var name = Path.GetFileNameWithoutExtension(transcriptPath);
        if (name.Length is >= 8 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return name;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(transcriptPath)));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
