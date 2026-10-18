using System;
using System.Linq;

namespace Fufucode.Core.Updates;

/// <summary>
/// 版本比较（点分数字，忽略预发布后缀；按数值而非字典序）。
/// 更新发现与服务层共用同一实现，避免两处口径漂移。
/// </summary>
public static class UpdateVersion
{
    public static int Compare(string left, string right)
    {
        static int[] Parts(string value) => value.Split('.', '-', '+')
            .Select(part => int.TryParse(new string(part.TakeWhile(char.IsAsciiDigit).ToArray()), out var number) ? number : 0)
            .ToArray();
        var a = Parts(left);
        var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
