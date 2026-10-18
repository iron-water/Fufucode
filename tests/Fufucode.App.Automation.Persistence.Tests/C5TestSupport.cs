/// <summary>反例构造辅助：只改一个维度，其余保持合法，避免"因为语法坏才失败"的假绿。</summary>
internal static class C5TestSupport
{
    /// <summary>
    /// 把合法文档里指定顶层数组的条目整体换成 <paramref name="bulkEntries"/>（条目数可控），
    /// 用于构造"结构合法但条目数超限"的反例。
    /// </summary>
    public static string SpliceArray(string json, string arrayName, string bulkEntries, int expectedEntries)
    {
        var marker = "\"" + arrayName + "\":[";
        var open = json.IndexOf(marker, StringComparison.Ordinal);
        if (open < 0) throw new Exception("反例构造失败：找不到数组 " + arrayName);
        var contentStart = open + marker.Length;
        var depth = 0;
        var close = -1;
        for (var index = contentStart - 1; index < json.Length; index++)
        {
            if (json[index] == '[') depth++;
            else if (json[index] == ']')
            {
                depth--;
                if (depth == 0) { close = index; break; }
            }
        }
        if (close < 0) throw new Exception("反例构造失败：数组 " + arrayName + " 未闭合");
        return string.Concat(json.AsSpan(0, contentStart), bulkEntries, json.AsSpan(close));
    }
}
