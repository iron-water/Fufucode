using System;
using System.IO;

namespace Fufucode.App.Integration;

internal static class SessionDeepLink
{
    internal static string Create(string sessionId, string projectPath)
    {
        if (!Guid.TryParse(sessionId, out _)) throw new ArgumentException("Invalid session ID.");
        if (!Path.IsPathFullyQualified(projectPath)) throw new ArgumentException("Absolute project path required.");
        return $"{ProductLinks.SessionScheme}://session/{sessionId}?project={Uri.EscapeDataString(Path.GetFullPath(projectPath))}";
    }

    internal static bool TryParse(string value, out string? session, out string? project)
    {
        session = project = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != ProductLinks.SessionScheme ||
            uri.Host != "session" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Fragment.Length > 0 ||
            !Guid.TryParse(uri.AbsolutePath.Trim('/'), out _) ||
            !uri.Query.StartsWith("?project=", StringComparison.Ordinal) || uri.Query.Contains('&')) return false;
        var path = Uri.UnescapeDataString(uri.Query[9..]);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0') || path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        try { project = Path.GetFullPath(path); }
        catch (ArgumentException) { return false; }
        session = uri.AbsolutePath.Trim('/');
        return true;
    }
}
