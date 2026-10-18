using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.ComputerUse;

/// <summary>
/// Win32 鼠标/键盘输入模拟：统一走 SendInput。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32InputSimulator : IComputerInput
{
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;

    public bool Move(int x, int y) =>
        SendMouse(MouseMove | MouseVirtualDesk, 0, x, y, 0);

    public bool Click(int x, int y, MouseButton button)
    {
        if (!Move(x, y)) return false;
        return button switch
        {
            MouseButton.Right => SendMouse(MouseRightDown, 0, x, y, 0) && SendMouse(MouseRightUp, 0, x, y, 0),
            MouseButton.Middle => SendMouse(MouseMiddleDown, 0, x, y, 0) && SendMouse(MouseMiddleUp, 0, x, y, 0),
            _ => SendMouse(MouseLeftDown, 0, x, y, 0) && SendMouse(MouseLeftUp, 0, x, y, 0),
        };
    }

    public bool DoubleClick(int x, int y) =>
        Click(x, y, MouseButton.Left) && Click(x, y, MouseButton.Left);

    public bool Scroll(int delta) =>
        SendMouse(MouseWheel, (uint)delta, 0, 0, 0);

    public bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (!SendKey(0, c, KeyUnicode) || !SendKey(0, c, KeyUnicode | KeyUp))
                return false;
        }
        return true;
    }

    public bool PressKey(string key)
    {
        if (!TryResolveVk(key, out var vk)) return false;
        return SendKey(vk, '\0', 0) && SendKey(vk, '\0', KeyUp);
    }

    public bool Hotkey(IReadOnlyList<string> keys)
    {
        if (keys is null || keys.Count == 0) return false;
        var vks = new List<ushort>(keys.Count);
        foreach (var key in keys)
        {
            if (!TryResolveVk(key, out var vk)) return false;
            vks.Add(vk);
        }

        for (var i = 0; i < vks.Count; i++)
        {
            if (!SendKey(vks[i], '\0', 0))
            {
                for (var j = i - 1; j >= 0; j--) SendKey(vks[j], '\0', KeyUp);
                return false;
            }
        }
        for (var i = vks.Count - 1; i >= 0; i--)
            SendKey(vks[i], '\0', KeyUp);
        return true;
    }

    private static bool SendMouse(uint flags, uint mouseData, int dx, int dy, uint time)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = mouseData,
                    DwFlags = flags,
                    Time = time,
                }
            }
        };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    private static bool SendKey(ushort vk, char scan, uint flags)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    WVk = vk,
                    WScan = scan,
                    DwFlags = flags,
                }
            }
        };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    private static bool TryResolveVk(string key, out ushort vk)
    {
        vk = key.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            "win" or "windows" => 0x5B,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "tab" => 0x09,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" => 0x2D,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) =>
                (ushort)char.ToUpperInvariant(key[0]),
            _ => 0,
        };
        return vk != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] Input[] pInputs, int cbSize);
}
