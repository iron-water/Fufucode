using System;
using System.IO;
using System.Linq;

namespace Fufucode.App.Infrastructure.Browser;

public enum BrowserTargetKind
{
    /// <summary>http/https 网页。</summary>
    Web,

    /// <summary>本地文件（转成 file URI）。</summary>
    File,

    /// <summary>about:blank，空态。</summary>
    Blank,

    /// <summary>被安全白名单拒绝的输入，不导航。</summary>
    Rejected,
}

/// <summary>地址栏输入归一化结果。Rejected 时 Address 为空，界面只显示 Note。</summary>
public sealed record BrowserTarget(BrowserTargetKind Kind, string? Address, string? Note)
{
    public bool IsNavigable => Kind is not BrowserTargetKind.Rejected && Address is not null;

    public static BrowserTarget Reject(string note) => new(BrowserTargetKind.Rejected, null, note);
}

/// <summary>
/// 把地址栏的任意输入归一化成可导航目标。协议白名单只有 http/https/file/about，
/// 其余（脚本协议、外部应用协议）一律拒绝——不给第三方页面注入脚本，也不拉起外部程序。
/// </summary>
internal static class BrowserAddressResolver
{
    private const string BlankTarget = "about:blank";

    public static BrowserTarget Resolve(string? input, string? projectRoot = null)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return Blank();
        if (text.Equals(BlankTarget, StringComparison.OrdinalIgnoreCase)) return Blank();

        if (TryReadScheme(text, out var scheme))
        {
            if (scheme!.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return Uri.TryCreate(text, UriKind.Absolute, out var web) &&
                       (web.Scheme == Uri.UriSchemeHttp || web.Scheme == Uri.UriSchemeHttps)
                    ? new(BrowserTargetKind.Web, text, null)
                    : BrowserTarget.Reject("网址格式无效。");

            if (scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                return Uri.TryCreate(text, UriKind.Absolute, out var file) && file.Scheme == Uri.UriSchemeFile
                    ? ToFile(file.LocalPath, projectRoot)
                    : BrowserTarget.Reject("file 地址格式无效。");

            if (scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
                return BrowserTarget.Reject("仅支持 about:blank。");

            return BrowserTarget.Reject($"不支持 {scheme} 协议；仅支持 http、https 与本地文件。");
        }

        // 无协议：本地路径优先，其余按主机名补全 https。
        if (Path.IsPathRooted(text) || LooksLikeWindowsDrivePath(text) || File.Exists(text) ||
            (!string.IsNullOrEmpty(projectRoot) && File.Exists(Path.Combine(projectRoot, text))))
            return ToFile(text, projectRoot);

        var candidate = "https://" + text;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? new(BrowserTargetKind.Web, candidate, null)
            : BrowserTarget.Reject("无法识别为网址或本地文件。");
    }

    /// <summary>
    /// 读取 scheme 前缀。三类情况不算协议：Windows 盘符（C:\）、主机端口（localhost:3000）、无冒号。
    /// 这样 javascript:/data: 等无 "//" 的危险前缀也会被白名单拦下。
    /// </summary>
    private static bool TryReadScheme(string text, out string? scheme)
    {
        scheme = null;
        var colon = text.IndexOf(':');
        if (colon <= 0) return false;
        if (colon == 1 && char.IsLetter(text[0])) return false; // 盘符
        var rest = text[(colon + 1)..];
        var port = rest.TakeWhile(c => c is not ('/' or '?' or '#')).ToArray();
        if (port.Length > 0 && port.All(char.IsDigit)) return false; // 主机:端口
        var head = text[..colon];
        if (head.Length == 0 || !head.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.')) return false;
        scheme = head;
        return true;
    }

    private static BrowserTarget Blank() => new(BrowserTargetKind.Blank, BlankTarget, null);

    private static BrowserTarget ToFile(string path, string? projectRoot)
    {
        string full;
        try
        {
            if (Path.IsPathRooted(path)) full = Path.GetFullPath(path);
            else if (!string.IsNullOrEmpty(projectRoot) && File.Exists(Path.Combine(projectRoot, path)))
                full = Path.GetFullPath(Path.Combine(projectRoot, path));
            else if (File.Exists(path)) full = Path.GetFullPath(path);
            else return BrowserTarget.Reject("找不到该本地文件。");

            // 绝对路径同样校验存在性，避免把不存在的路径当成可导航目标。
            if (!File.Exists(full)) return BrowserTarget.Reject("找不到该本地文件。");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return BrowserTarget.Reject("本地路径无效：" + error.Message);
        }
        return new(BrowserTargetKind.File, new Uri(full).AbsoluteUri, null);
    }

    private static bool LooksLikeWindowsDrivePath(string text) =>
        text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':';
}
