using System;
using System.Globalization;

namespace Fufucode.App.Features.Storage;

/// <summary>占用大小的统一显示口径；界面与结果记录共用一份，避免两处数字格式不一致。</summary>
public static class StorageFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var text = unit == 0
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture);
        return text + " " + Units[unit];
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
