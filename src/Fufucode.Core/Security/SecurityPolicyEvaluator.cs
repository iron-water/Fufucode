using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Security;

public sealed record SecurityDecision(SecurityAction Action, string Category, string Summary);

public sealed class SecurityPolicyEvaluator(SecurityPolicyStore store, string workspaceRoot)
{
    public SecurityPolicyStore Store => store;
    public Func<string, bool>? IsExternalTool { get; set; }
    public SecurityDecision Evaluate(PureToolCall call)
    {
        try { return Evaluate(call, store.Current); }
        catch (Exception ex) when (ex is SecurityPolicyException or JsonException or ArgumentException or IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        { return new(SecurityAction.Deny, "policy", "安全策略或工具参数无效，已拒绝执行。"); }
    }
    private SecurityDecision Evaluate(PureToolCall call, SecuritySettings s)
    {
        if (!s.Enabled) return new(SecurityAction.Allow, "policy", "安全中心已关闭。");
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        var args = doc.RootElement;
        if (args.ValueKind != JsonValueKind.Object) return new(SecurityAction.Deny, "arguments", "工具参数必须是对象。");
        var decisions = new List<SecurityDecision>();
        bool builtin = call.Name is "read_file" or "write_file" or "edit_file" or "list_dir" or "grep_search" or "glob_files" or "run_command" or "update_plan";
        if (IsExternalTool?.Invoke(call.Name) == true) builtin = false;
        if (call.Name is "read_file" or "write_file" or "edit_file" or "list_dir" or "grep_search" or "glob_files")
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
            // read_file permits path:line selectors; protect the underlying path as well.
            if (call.Name == "read_file") path = Regex.Replace(path, @":-?\d+(?:[-+,\d]*)$", "");
            decisions.Add(new(FileAction(path, call.Name is "write_file" or "edit_file" ? "write" : "read", s),
                "file", "文件访问规则"));
        }
        if (call.Name == "run_command")
        {
            var command = args.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            decisions.Add(EvaluateCommand(command, s));
        }
        if (!builtin) decisions.Add(new(SecurityAction.Ask, "external-tool", "插件、MCP、技能或子代理能力需要确认；同进程工具不受操作系统沙箱隔离。"));
        foreach (var value in StringValues(args))
        {
            foreach (Match m in Regex.Matches(value, @"https?://[^\s\""'<>]+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                if (Uri.TryCreate(m.Value, UriKind.Absolute, out var uri))
                    decisions.Add(new(NetworkAction(uri, s), "network", "HTTP 目标规则；shell 与插件内部连接不能隔离。"));
        }
        if (s.SensitiveDataProtectionEnabled && SecurityContentScanner.ContainsSecret(call.ArgumentsJson, s) &&
            (call.Name == "run_command" || !builtin))
            decisions.Add(new(SecurityAction.Deny, "secret-egress", "调用参数包含敏感凭据，已阻止向 shell 或外部工具传递。"));
        return decisions.FirstOrDefault(d => d.Action == SecurityAction.Deny)
            ?? decisions.FirstOrDefault(d => d.Action == SecurityAction.Ask)
            ?? new(SecurityAction.Allow, "tool", "安全检查通过。");
    }
    private static IEnumerable<string> StringValues(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) yield return e.GetString()!;
        else if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) foreach (var s in StringValues(p.Value)) yield return s;
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var p in e.EnumerateArray()) foreach (var s in StringValues(p)) yield return s;
    }
    public SecurityAction FileAction(string path, string operation = "read")
    {
        try { return FileAction(path, operation, store.Current); }
        catch { return SecurityAction.Deny; }
    }
    private SecurityAction FileAction(string path, string operation, SecuritySettings settings)
    {
        if (!settings.Enabled) return SecurityAction.Allow;
        var full = Path.GetFullPath(path, workspaceRoot);
        var real = RealPath(full);
        var dataRoot = RealPath(Path.GetDirectoryName(store.FilePath)!);
        bool IsPrivate(string candidate) => string.Equals(candidate, dataRoot, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(Path.TrimEndingDirectorySeparator(dataRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (IsPrivate(full) || IsPrivate(real)) return SecurityAction.Deny;
        // Both aliases and targets must pass; an alias cannot weaken a target rule.
        SecurityAction Match(string candidate)
        {
            var normalized = candidate.Replace('\\', '/');
            foreach (var rule in settings.FileRules)
                if (Glob(normalized, rule.Pattern) || Glob(Path.GetRelativePath(workspaceRoot, candidate).Replace('\\', '/'), rule.Pattern))
                    return operation switch { "write" => rule.Write, "delete" => rule.Delete, _ => rule.Read };
            return SecurityAction.Allow;
        }
        var literal = Match(full); var target = Match(real);
        if (literal == SecurityAction.Deny || target == SecurityAction.Deny) return SecurityAction.Deny;
        return literal == SecurityAction.Ask || target == SecurityAction.Ask ? SecurityAction.Ask : SecurityAction.Allow;
    }
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                current = info.ResolveLinkTarget(true)?.FullName ?? current;
            }
        }
        return current;
    }
    internal static bool Glob(string value, string pattern)
    {
        pattern = Environment.ExpandEnvironmentVariables(pattern);
        if (pattern == "~" || pattern.StartsWith("~/", StringComparison.Ordinal) || pattern.StartsWith("~\\\\", StringComparison.Ordinal))
            pattern = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + pattern[1..];
        pattern = pattern.Replace('\\', '/');
        if (pattern.EndsWith('/')) pattern += "**";
        var regex = Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        // A directory rule ending /** also protects the directory name itself.
        if (pattern.EndsWith("/**", StringComparison.Ordinal)) regex = regex[..^3] + "(?:/.*)?";
        return Regex.IsMatch(value, "^" + regex + "$", (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
    public static SecurityAction NetworkAction(Uri uri, SecuritySettings settings)
    {
        if (!settings.Enabled) return SecurityAction.Allow;
        if (uri.Scheme is not ("http" or "https")) return SecurityAction.Ask;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        foreach (var rule in settings.NetworkRules)
        {
            var domain = rule.Pattern.Trim().ToLowerInvariant().TrimEnd('.');
            var wildcard = domain.StartsWith("*.", StringComparison.Ordinal);
            if (wildcard) domain = domain[2..];
            if (Uri.TryCreate(domain, UriKind.Absolute, out var ruleUri)) domain = ruleUri.IdnHost;
            if (host == domain || (wildcard && host.EndsWith("." + domain, StringComparison.Ordinal))) return rule.Action;
        }
        var local = host == "localhost" || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        return uri.Scheme == "https" ? SecurityAction.Allow : local ? settings.LocalHttpAction : settings.RemoteHttpAction;
    }
    public static SecurityDecision EvaluateCommand(string command, SecuritySettings settings)
    {
        // Prefix rules apply to individual simple commands; composition and interpreter syntax always require review.
        var pieces = Regex.Split(command, @"[;&|\r\n]+|\$\(|`").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var compound = pieces.Length != 1 || Regex.IsMatch(command, @"[;&|<>`\r\n]|\$\(|\^|%|\$\{|\b(?:cmd|powershell|pwsh|sh|bash|python|node)\b", RegexOptions.IgnoreCase);
        SecurityDecision result = new(SecurityAction.Ask, "shell", "shell 可访问当前用户权限下的文件和网络，需要确认；不提供进程隔离。");
        foreach (var piece in pieces)
        {
            var text = piece.Trim().TrimStart('(', ')', '@');
            var tokens = Regex.Matches(text, "\"[^\"]*\"|'[^']*'|[^\\s]+").Select(m => m.Value.Trim('\"', '\'')).ToArray();
            foreach (var token in tokens)
            {
                var program = Path.GetFileNameWithoutExtension(token.Replace('\\', '/'));
                if (settings.BlockedPrograms.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), program, StringComparison.OrdinalIgnoreCase)))
                    return new(SecurityAction.Deny, "program", "调用包含已禁止的程序。");
            }
            var normalized = string.Join(' ', tokens);
            if (tokens.Length > 0)
            {
                var first = Path.GetFileName(tokens[0].Replace('\\', '/'));
                if (first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) first = first[..^4];
                normalized = first + (tokens.Length > 1 ? " " + string.Join(' ', tokens.Skip(1)) : "");
            }
            foreach (var rule in settings.CommandRules)
            {
                var prefix = rule.Pattern.Trim();
                if (prefix.EndsWith(" *", StringComparison.Ordinal)) prefix = prefix[..^2].TrimEnd();
                bool PrefixMatch(string candidate) => candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && candidate.Length > prefix.Length && char.IsWhiteSpace(candidate[prefix.Length]));
                if (!PrefixMatch(text) && !PrefixMatch(normalized)) continue;
                if (rule.Action == SecurityAction.Deny) return new(SecurityAction.Deny, "command", "命令命中禁止规则。");
                result = new(compound ? SecurityAction.Ask : rule.Action, "command", "命令前缀规则；复合命令需要确认。");
                break;
            }
        }
        if (settings.BehaviorReviewEnabled && Regex.IsMatch(command,
                @"(?i)\b(?:rm|del|rmdir|format|diskpart|sudo|reg|schtasks|curl|wget|invoke-expression|iex|remove-item|start-process)\b|(?:-rf|-recurse|--force|reset\s+--hard|push\s+--force)"))
            return result.Action == SecurityAction.Deny ? result : new(SecurityAction.Ask, "behavior", "本地行为规则发现删除、提权、下载或破坏性命令，需要人工确认。");
        return result;
    }
}
