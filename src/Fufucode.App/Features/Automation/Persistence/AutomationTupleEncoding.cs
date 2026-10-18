using System.Collections.Generic;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 无歧义的字段串编码：每个字段写成 <c>字符数 + U+241F + 字段本身</c>，按顺序拼接。
/// 字段内容里的任何字符（含分隔符本身、逗号、U+2224、换行）都不可能被误读成字段边界，
/// 因此「不同字段值」必然得到「不同编码」，反过来也能逐字段精确比较。
/// 用途：scope 哈希输入（C5-PERSIST-SCOPE-1）与内容指纹（C5-PERSIST-FINGERPRINT-1）。
/// 这里不禁用任何合法用户文本，只改变编码方式。
/// </summary>
internal static class AutomationTupleEncoding
{
    private const char Mark = '\u241F';

    public static string Encode(params string?[] fields)
    {
        var total = 0;
        foreach (var field in fields) total += (field?.Length ?? 0) + 8;
        var builder = new System.Text.StringBuilder(total);
        foreach (var field in fields)
        {
            var value = field ?? string.Empty;
            builder.Append(value.Length).Append(Mark).Append(value).Append(Mark);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 序列编码：先写字段个数再逐元素做长度前缀编码，因此 <c>["a,b"]</c> 与 <c>["a","b"]</c>
    /// （元素数不同、元素边界不同）必然得到不同结果，顺序变化同样变化。
    /// </summary>
    public static string EncodeSequence(IReadOnlyList<string> values)
    {
        var parts = new string[values.Count + 1];
        parts[0] = values.Count.ToString();
        for (var index = 0; index < values.Count; index++) parts[index + 1] = values[index];
        return Encode(parts);
    }
}
