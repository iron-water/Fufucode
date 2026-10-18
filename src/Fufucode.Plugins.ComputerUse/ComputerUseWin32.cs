using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.ComputerUse;

/// <summary>
/// Win32 屏幕截图：GDI BitBlt 抓取桌面或指定矩形并编码为 PNG。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ScreenCapture : IComputerScreenCapture
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public Task<ScreenCaptureResult> CaptureAsync(ScreenRect bounds, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new ScreenCaptureResult(false, null, null, null, "非 Windows 平台不支持 Win32 截图。"));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Capture(bounds, cancellationToken), cancellationToken);
    }

    private static ScreenCaptureResult Capture(ScreenRect bounds, CancellationToken cancellationToken)
    {
        var left = bounds.Left ?? GetSystemMetrics(SmXVirtualScreen);
        var top = bounds.Top ?? GetSystemMetrics(SmYVirtualScreen);
        var width = bounds.Width ?? GetSystemMetrics(SmCxVirtualScreen);
        var height = bounds.Height ?? GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
            return new ScreenCaptureResult(false, null, null, null, "屏幕尺寸无效（可能没有交互式桌面会话）。");

        IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return Fail();
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return Fail();
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero) return Fail();
            previous = SelectObject(memoryDc, bitmap);
            if (previous == IntPtr.Zero) return Fail();

            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, Srccopy))
                return Fail();

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    BiSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    BiWidth = width,
                    BiHeight = -height,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0,
                }
            };
            var stride = width * 4;
            var bgra = new byte[stride * height];
            if (GetDIBits(memoryDc, bitmap, 0, (uint)height, bgra, ref info, 0) == 0)
                return Fail();

            var png = EncodePng(bgra, width, height);
            return new ScreenCaptureResult(true, png, width, height, null);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }

        static ScreenCaptureResult Fail() =>
            new(false, null, null, null, $"Win32 GDI 截图失败（错误码 {Marshal.GetLastWin32Error()}）。");
    }

    private static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WriteChunk(ms, "IHDR", BuildIhdr(width, height));
        WriteChunk(ms, "IDAT", CompressIdat(bgra, width, height));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var data = new byte[13];
        WriteBeInt32(data, 0, width);
        WriteBeInt32(data, 4, height);
        data[8] = 8;
        data[9] = 6;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static byte[] CompressIdat(byte[] bgra, int width, int height)
    {
        var rawStride = width * 4;
        var scanline = rawStride + 1;
        var uncompressed = new byte[scanline * height];
        for (var y = 0; y < height; y++)
        {
            var rawOffset = y * rawStride;
            var scanOffset = y * scanline;
            uncompressed[scanOffset] = 0;
            for (var x = 0; x < width; x++)
            {
                var src = rawOffset + x * 4;
                var dst = scanOffset + 1 + x * 4;
                uncompressed[dst] = bgra[src + 2];
                uncompressed[dst + 1] = bgra[src + 1];
                uncompressed[dst + 2] = bgra[src];
                uncompressed[dst + 3] = bgra[src + 3];
            }
        }

        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            deflate.Write(uncompressed, 0, uncompressed.Length);
        }
        var adler = Adler32(uncompressed);
        output.Write([ (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler ]);
        return output.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBeInt32(length, 0, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBeInt32(crcBytes, 0, (int)crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type) crc = Update(crc, b);
        foreach (var b in data) crc = Update(crc, b);
        return ~crc;

        static uint Update(uint current, byte b)
        {
            current ^= b;
            for (var i = 0; i < 8; i++)
                current = (current & 1) != 0 ? (current >> 1) ^ 0xEDB88320 : current >> 1;
            return current;
        }
    }

    private static void WriteBeInt32(Span<byte> buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int Srccopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr handle);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height,
        IntPtr sourceDc, int sourceX, int sourceY, int rop);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hDc, IntPtr bitmap, uint start, uint lines,
        [Out] byte[] bits, ref BitmapInfo info, uint usage);
}
