using System;
using System.Runtime.InteropServices;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 读取系统「减少动画」偏好（Windows：设置 → 辅助功能 → 视觉效果 → 动画效果），
/// 让页面过渡与导航指示条等动效可以即时降级为瞬时切换。Windows 上每次访问都实时读
/// （一次廉价 P/Invoke），读取失败按「动画开启」处理。
/// </summary>
internal static class MotionPreference
{
    /// <summary>用户已在系统层要求减少动画时为 true。</summary>
    public static bool ReducedMotion
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                // 与 WinUI UISettings.AnimationsEnabled 同一信号源；pvParam 为 BOOL。
                var enabled = 1;
                if (SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0))
                {
                    return enabled == 0;
                }
            }
            catch (Exception)
            {
                // 读不到系统偏好时保持动画开启。
            }

            return false;
        }
    }

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);
}
