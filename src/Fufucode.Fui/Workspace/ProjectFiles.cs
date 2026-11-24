using System.Security.Cryptography;
using System.Text;
using Fufucode.Core;
using Fufucode.Core.Security;

namespace Fufucode.Fui;

internal sealed record FileAttachment(string Path, string Text, byte[] Digest);
internal sealed record FilePage(string Folder, IReadOnlyList<FuiChoice> Items);

// Browse one directory on demand, not the whole project on each keystroke. File contents are not
// sent merely by selecting/opening a file. Attachments are explicit, bounded and checked again at send.
internal sealed class ProjectFiles
{
    internal const int MaxFileBytes = 65536, MaxAttachments = 4;
    private const int MaxEntries = 1000;
    private static readonly HashSet<string> HiddenDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", ".hg", "bin", "obj", "node_modules", ".venv", "__pycache__" };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _root;
    private readonly SecurityPolicyEvaluator _policy;

    public ProjectFiles(string project, string dataRoot)
    {
        _root = AppDataLayout.NormalizeProjectPath(project);
        _policy = new(new SecurityPolicyStore(dataRoot), _root);
    }

    public string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    public FilePage List(string folder, CancellationToken ct)
    {
        var dir = Resolve(folder, directory: true);
        var allowed = _policy.ReadablePathsForListing();
        var children = new List<(FuiChoice Item, bool Directory)>();
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || !allowed(entry.FullName)) continue;
            var child = entry.Attributes.HasFlag(FileAttributes.Directory);
            if (child && HiddenDirectories.Contains(entry.Name)) continue;
            if (children.Count == MaxEntries) throw new IOException($"目录超过 {MaxEntries} 项；用 /attach 或 /preview 指定相对路径。");
            var rel = Relative(entry.FullName);
            children.Add((new(child ? rel + "/" : rel, entry.Name + (child ? "/" : ""), child ? "目录" : rel), child));
        }
        var choices = children.OrderByDescending(c => c.Directory).ThenBy(c => c.Item.Label, StringComparer.OrdinalIgnoreCase).Select(c => c.Item).ToList();
        var here = Relative(dir);
        if (here != ".") choices.Insert(0, new(Relative(Directory.GetParent(dir)!.FullName) + "/", "../", "上一级"));
        return new(here, choices);
    }

    public async Task<FileAttachment> ReadAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaxFileBytes) throw new IOException($"文件超过 {MaxFileBytes / 1024} KiB；未读取或发送。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        if (stream.ReadByte() != -1) throw new IOException("文件在读取时增长；请重新选择。");
        string text;
        try { text = Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new InvalidDataException("只能附加或预览 UTF-8 文本文件。"); }
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
            throw new InvalidDataException("含二进制或控制数据；只能读取文本文件。");
        var settings = _policy.Store.Current;
        if (settings.SensitiveDataProtectionEnabled && SecurityContentScanner.ContainsSecret(text, settings))
            throw new UnauthorizedAccessException("安全中心检测到敏感内容；文件未加入上下文。");
        return new(Relative(full), text, SHA256.HashData(bytes));
    }

    public async Task<string> ComposeAsync(string prompt, IReadOnlyList<FileAttachment> attached, CancellationToken ct)
    {
        if (attached.Count > MaxAttachments) throw new InvalidOperationException("上下文中的文件过多。");
        var body = new StringBuilder(prompt);
        foreach (var file in attached)
        {
            var current = await ReadAsync(file.Path, ct);
            if (!current.Digest.AsSpan().SequenceEqual(file.Digest)) throw new IOException($"{file.Path} 已更改；请先移除，再重新附加。");
            body.Append("\n\n[项目文件：").Append(current.Path).Append("]\n").Append(current.Text).Append("\n[文件结束]");
        }
        return body.ToString();
    }

    public async Task<string> DiffAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path, allowDeleted: true);
        var result = await DiffQuery.FileAsync(_root, Relative(full), maximumLines: 1000, cancellationToken: ct);
        if (result.Error is { } error) throw new IOException($"无法读取 Git 差异：{error}");
        var text = new StringBuilder(result.Path);
        if (result.IsBinary == true) text.Append(" · 二进制文件");
        if (result.OldMode != result.NewMode) text.Append($"\n模式 {result.OldMode} → {result.NewMode}");
        foreach (var h in result.Hunks)
        {
            text.Append('\n').Append(h.Header);
            foreach (var line in h.Lines) text.Append('\n').Append(line.Kind).Append(line.Text);
        }
        if (result.Truncated) text.Append("\n… 差异已截断");
        else if (result.Hunks.Count == 0 && result.IsBinary != true && result.OldMode == result.NewMode) text.Append(" · 没有文本差异");
        var settings = _policy.Store.Current;
        return settings.SensitiveDataProtectionEnabled ? SecurityContentScanner.Redact(text.ToString(), settings) : text.ToString();
    }

    private string Resolve(string path, bool directory = false, bool allowDeleted = false)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Any(char.IsControl) || path.Contains(':'))
            throw new ArgumentException("请输入项目内的相对路径。");
        var full = Path.GetFullPath(path, _root);
        var rel = Path.GetRelativePath(_root, full);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("路径不在项目内。");
        var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = _root;
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("不能读取 Git 内部目录。");
            current = Path.Combine(current, part);
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("不能读取链接或联接路径。"); }
            catch (FileNotFoundException) when (allowDeleted) { }
            catch (DirectoryNotFoundException) when (allowDeleted) { }
        }
        if (_policy.FileAction(full) != SecurityAction.Allow) throw new UnauthorizedAccessException("安全中心未允许直接读取此路径。");
        if (!directory && Directory.Exists(full)) throw new ArgumentException("请选择一个文件，而不是目录。");
        if (directory ? !Directory.Exists(full) : !allowDeleted && !File.Exists(full)) throw new FileNotFoundException("项目内未找到该路径。", path);
        return full;
    }
}
