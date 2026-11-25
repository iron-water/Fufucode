using System.Runtime.InteropServices;

namespace Fufucode.Fui;

// Windows Imaging Component COM vtable decoder (in-memory only, zero temp files, bomb-checked before CopyPixels).
// Automatically picks up built-in Windows codecs (TIFF, JXR/WDP/HDP, DDS, ICO, BMP, PNG, JPEG, GIF)
// plus installed Windows codecs (JPEG XL, HEIC/HEIF, AVIF, WebP, Camera RAW).
internal static unsafe class WicImageDecoder
{
    private static readonly Guid ClsidWicImagingFactory = new(0xcacaf262, 0x9370, 0x4615, 0xa1, 0x3b, 0x9f, 0x55, 0x39, 0xda, 0x4c, 0x0a);
    private static readonly Guid IidWicImagingFactory = new(0xec5ec8a9, 0xc395, 0x4314, 0x9c, 0x77, 0x54, 0xd7, 0xa9, 0x35, 0xff, 0x70);
    private static readonly Guid PixelFormat32bppRgba = new(0xf5c7ad2d, 0x6a8d, 0x43dd, 0xa7, 0xa8, 0xa2, 0x99, 0x35, 0x26, 0x1a, 0xe9);
    private static readonly Guid PixelFormat32bppBgra = new(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0f);

    private const uint ClsctxInprocServer = 1;
    private const uint CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid, out nint ppv);

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, int maxDimension, long maxPixels,
        out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = height = 0;
        if (!OperatingSystem.IsWindows() || bytes.IsEmpty) return false;

        var hrInit = CoInitializeEx(nint.Zero, CoinitMultithreaded);
        var needUninit = hrInit is 0 or 1;
        if (hrInit < 0 && hrInit != RpcEChangedMode) return false;

        nint factory = 0, stream = 0, decoder = 0, frame = 0, converter = 0;
        try
        {
            var clsid = ClsidWicImagingFactory;
            var iid = IidWicImagingFactory;
            if (CoCreateInstance(ref clsid, nint.Zero, ClsctxInprocServer, ref iid, out factory) < 0 || factory == 0)
                return false;

            var createStream = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(factory, 14);
            if (createStream(factory, &stream) < 0 || stream == 0) return false;

            fixed (byte* src = bytes)
            {
                var initMem = (delegate* unmanaged[Stdcall]<nint, byte*, uint, int>)Vtbl(stream, 16);
                if (initMem(stream, src, (uint)bytes.Length) < 0) return false;

                var createDecoder = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, uint, nint*, int>)Vtbl(factory, 4);
                if (createDecoder(factory, stream, null, 0, &decoder) < 0 || decoder == 0) return false;

                uint frameCount = 0;
                var getFrameCount = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(decoder, 12);
                if (getFrameCount(decoder, &frameCount) < 0 || frameCount == 0) return false;

                var getFrame = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(decoder, 13);
                if (getFrame(decoder, 0, &frame) < 0 || frame == 0) return false;

                uint w = 0, h = 0;
                var getSize = (delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Vtbl(frame, 3);
                if (getSize(frame, &w, &h) < 0 || w == 0 || h == 0 ||
                    w > (uint)maxDimension || h > (uint)maxDimension || (long)w * h > maxPixels)
                    return false;

                var createConverter = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(factory, 10);
                if (createConverter(factory, &converter) < 0 || converter == 0) return false;

                var dstFmt = PixelFormat32bppRgba;
                var swappedBgra = false;
                var initConv = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, uint, nint, double, uint, int>)Vtbl(converter, 8);
                if (initConv(converter, frame, &dstFmt, 0, nint.Zero, 0.0, 0) < 0)
                {
                    dstFmt = PixelFormat32bppBgra;
                    if (initConv(converter, frame, &dstFmt, 0, nint.Zero, 0.0, 0) < 0) return false;
                    swappedBgra = true;
                }

                var stride = checked((uint)(w * 4));
                var totalBytes = checked((int)((long)stride * h));
                var output = new byte[totalBytes];
                fixed (byte* dst = output)
                {
                    var copyPixels = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, byte*, int>)Vtbl(converter, 7);
                    if (copyPixels(converter, nint.Zero, stride, (uint)totalBytes, dst) < 0) return false;
                }
                if (swappedBgra)
                {
                    for (var i = 0; i < totalBytes; i += 4)
                        (output[i], output[i + 2]) = (output[i + 2], output[i]);
                }

                rgba = output;
                width = (int)w;
                height = (int)h;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            Release(converter);
            Release(frame);
            Release(decoder);
            Release(stream);
            Release(factory);
            if (needUninit) CoUninitialize();
        }
    }

    private static void* Vtbl(nint comObj, int slot) => (*(void***)comObj)[slot];

    private static void Release(nint comObj)
    {
        if (comObj == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(comObj, 2);
        release(comObj);
    }
}
