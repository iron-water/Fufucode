using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Fufucode.App.Integration;

/// <summary>Windows 系统通知按钮上的审批决定。</summary>
internal enum ApprovalDecision
{
    Allow,
    AlwaysAllow,
    Deny,
}

/// <summary>一次按钮点按带来的审批动作；请求身份以 Core 的 ExtensionUiRequest.Id 为准。</summary>
internal sealed record ApprovalAction(string RequestId, string SessionId, ApprovalDecision Decision, string? ProjectPath);

/// <summary>
/// 审批通知的协议激活链接：<c>fufucode-approval://approval/&lt;requestId&gt;?session=&lt;guid&gt;&amp;decision=allow|always|deny</c>。
///
/// 系统通知的按钮只能通过协议激活回到本应用（未打包桌面应用没有 COM 激活器），因此链接必须
/// 自包含且严格校验：只有当前仍挂起的同一请求才会被应答，无法凭链接凭空放行任何工具。
/// </summary>
internal static class ApprovalLink
{
    /// <summary>
    /// 审批按钮专用 scheme：与安装器注册的会话链接 <c>fufucode:</c> 分开，
    /// 免得启动一个审阅构建就改写用户会话链接的处理器；本 scheme 由应用启动时自注册。
    /// </summary>
    internal const string Scheme = "fufucode-approval";
    private const string Host = "approval";

    /// <summary>能构造就返回链接，否则返回 null（mock/自测会话没有真实 Guid，不能生成按钮）。</summary>
    internal static string? TryCreate(string? requestId, string? sessionId, ApprovalDecision decision, string? projectPath = null)
    {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(sessionId)) return null;
        if (!IsSafeRequestId(requestId) || !Guid.TryParse(sessionId, out _)) return null;
        return Create(requestId, sessionId, decision, projectPath);
    }

    internal static string Create(string requestId, string sessionId, ApprovalDecision decision, string? projectPath = null)
    {
        if (!IsSafeRequestId(requestId)) throw new ArgumentException("审批请求 Id 无效。", nameof(requestId));
        if (!Guid.TryParse(sessionId, out _)) throw new ArgumentException("会话 Id 无效。", nameof(sessionId));
        var builder = new StringBuilder();
        builder.Append(Scheme).Append("://").Append(Host).Append('/').Append(requestId);
        builder.Append("?session=").Append(sessionId).Append("&decision=").Append(Token(decision));
        if (!string.IsNullOrWhiteSpace(projectPath) && Path.IsPathFullyQualified(projectPath))
            builder.Append("&project=").Append(Uri.EscapeDataString(Path.GetFullPath(projectPath)));
        return builder.ToString();
    }

    internal static bool TryParse(string? value, out ApprovalAction action)
    {
        action = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Scheme ||
            uri.Host != Host || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return false;

        // Uri 会把 ../ 规范化掉；先看原文的路径段，避免"看似别的路径、实际解析成合法 Id"的写法。
        var pathPart = value[..(value.Contains('?') ? value.IndexOf('?') : value.Length)];
        if (pathPart.Contains("..", StringComparison.Ordinal) || pathPart.Contains("./", StringComparison.Ordinal)) return false;
        var requestId = uri.AbsolutePath.Trim('/');
        if (!IsSafeRequestId(requestId) || requestId.Contains('/')) return false;

        string? session = null, decision = null, project = null;
        var query = uri.Query.StartsWith('?') ? uri.Query[1..] : uri.Query;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || separator == pair.Length - 1) return false;
            var key = pair[..separator];
            var text = Uri.UnescapeDataString(pair[(separator + 1)..]);
            switch (key)
            {
                case "session" when session is null: session = text; break;
                case "decision" when decision is null: decision = text; break;
                case "project" when project is null: project = text; break;
                default: return false;
            }
        }

        if (session is null || !Guid.TryParse(session, out _)) return false;
        if (decision is not ("allow" or "always" or "deny")) return false;
        if (project is not null && (!Path.IsPathFullyQualified(project) ||
            project.Contains('\0') || project.StartsWith("\\\\", StringComparison.Ordinal))) return false;

        action = new ApprovalAction(requestId, session,
            decision switch { "allow" => ApprovalDecision.Allow, "always" => ApprovalDecision.AlwaysAllow, _ => ApprovalDecision.Deny },
            project);
        return true;
    }

    /// <summary>命令行走廊只按 host 分流；其它 fufucode: 链接仍交给会话链接解析。</summary>
    internal static bool LooksLikeApprovalLink(string value) =>
        value.StartsWith(Scheme + "://" + Host + "/", StringComparison.OrdinalIgnoreCase);

    private static string Token(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Allow => "allow",
        ApprovalDecision.AlwaysAllow => "always",
        _ => "deny",
    };

    private static bool IsSafeRequestId(string? value) => value is { Length: > 0 and <= 96 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
