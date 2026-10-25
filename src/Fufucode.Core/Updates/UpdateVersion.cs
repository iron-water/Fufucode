using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.Core.Updates;

/// <summary>
/// 版本比较（SemVer 2.0 口径：先比点分数字主版本，再比预发布后缀）。
/// 更新发现与服务层共用同一实现，避免两处口径漂移。
///
/// 预发布排序按 SemVer §11：<c>1.4.0-test.1 &lt; 1.4.0-test.2 &lt; 1.4.0</c>。
/// 实验性测试渠道的两次构建只差 <c>-test.N</c>，若把后缀一律忽略就永远不会发现更新；
/// 正式渠道的版本不带后缀，比较结果与旧实现一致。
/// </summary>
public static class UpdateVersion
{
    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        var core = CompareCore(a.Core, b.Core);
        if (core != 0) return core;
        return ComparePreRelease(a.PreRelease, b.PreRelease);
    }

    /// <summary>是否为合法版本号：至少一个数字段，可选 <c>-预发布</c> 与 <c>+构建元数据</c>。</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var (core, _) = Parse(value);
        return core.Length > 0 && core.All(part => part >= 0);
    }

    private static (int[] Core, string[] PreRelease) Parse(string value)
    {
        var text = (value ?? string.Empty).Trim();
        // 构建元数据（+hash）不参与优先级比较。
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        var dash = text.IndexOf('-');
        var coreText = dash >= 0 ? text[..dash] : text;
        var preText = dash >= 0 ? text[(dash + 1)..] : string.Empty;
        var core = coreText.Split('.')
            .Select(part => int.TryParse(new string(part.TakeWhile(char.IsAsciiDigit).ToArray()), out var number)
                ? number
                : -1)
            .ToArray();
        var pre = preText.Length == 0
            ? Array.Empty<string>()
            : preText.Split('.', '-');
        return (core, pre);
    }

    private static int CompareCore(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? Math.Max(a[i], 0) : 0;
            var y = i < b.Length ? Math.Max(b[i], 0) : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int ComparePreRelease(string[] a, string[] b)
    {
        // 没有预发布后缀 = 正式版，优先级高于任何预发布。
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;
        if (b.Length == 0) return -1;

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            var x = a[i];
            var y = b[i];
            var xNumeric = int.TryParse(x, out var xNumber);
            var yNumeric = int.TryParse(y, out var yNumber);
            int result;
            if (xNumeric && yNumeric) result = xNumber.CompareTo(yNumber);
            else if (xNumeric) result = -1;                       // 数字标识符优先级低于字母标识符
            else if (yNumeric) result = 1;
            else result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
        }
        return 0;
    }

    /// <summary>排序辅助：从一串版本里取最大者；无法解析的版本被忽略。</summary>
    public static string? Max(IEnumerable<string> versions) =>
        versions.Where(IsValid)
            .Aggregate((string?)null, (best, next) => best is null || Compare(next, best) > 0 ? next : best);
}
