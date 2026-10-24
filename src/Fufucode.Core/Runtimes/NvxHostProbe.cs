using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Fufucode.Core.Runtimes;

/// <summary>
/// Windows Hypervisor Platform capability probe used by the optional NVX plugin.
/// The probe is read-only: it never enables Windows features and never starts a VM.
/// </summary>
public static class NvxHostProbe
{
    private const int WhvCapabilityCodeHypervisorPresent = 0;

    public static NvxHostStatus Probe()
    {
        if (!OperatingSystem.IsWindows())
            return new NvxHostStatus(false, false, "NVX 的 WHP 后端仅支持 Windows 宿主；Linux 后端尚未接入。");

        try
        {
            var capability = 0UL;
            var result = WHvGetCapability(
                WhvCapabilityCodeHypervisorPresent,
                ref capability,
                (uint)Marshal.SizeOf<ulong>(),
                out var written);
            if (result != 0)
            {
                var error = new Win32Exception(unchecked((int)result));
                return new NvxHostStatus(true, false, $"WHP 能力查询失败（0x{result:X8}）：{error.Message}");
            }

            var present = written >= sizeof(ulong) && capability != 0;
            return new NvxHostStatus(true, present,
                present ? "Windows Hypervisor Platform 可用。" :
                "WHP API 可加载，但未检测到可用的硬件虚拟化管理程序；请启用 WHP 并检查 BIOS 虚拟化设置。");
        }
        catch (DllNotFoundException)
        {
            return new NvxHostStatus(true, false, "未找到 WinHvPlatform.dll；Windows Hypervisor Platform 可能未安装。");
        }
        catch (EntryPointNotFoundException)
        {
            return new NvxHostStatus(true, false, "WinHvPlatform.dll 缺少 WHvGetCapability；当前系统不支持所需 WHP API。");
        }
        catch (Exception error)
        {
            return new NvxHostStatus(true, false, $"WHP 能力探测失败：{error.Message}");
        }
    }

    [DllImport("WinHvPlatform.dll", ExactSpelling = true)]
    private static extern int WHvGetCapability(
        int capabilityCode,
        ref ulong capabilityBuffer,
        uint capabilityBufferSizeInBytes,
        out uint writtenSizeInBytes);
}

public sealed record NvxHostStatus(bool IsWindows, bool HypervisorAvailable, string Diagnostic)
{
    public bool CanRunNvx => IsWindows && HypervisorAvailable;
}
