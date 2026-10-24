using System.Runtime.InteropServices;

namespace Live2D.Core;

/// <summary>
/// Live2DCubismCore.dll (SDK 5-r.4, windows/x86_64) 的 P/Invoke 层。
/// 二进制从 VendorPaths.NativeCoreDll 经 DllImportResolver 加载，不进仓库。
/// 对齐要求：moc 64B、model 16B，均用 NativeMemory.AlignedAlloc 满足。
/// </summary>
public static unsafe class CubismCore
{
    public const string LibName = "Live2DCubismCore";
    public const int AlignofMoc = 64;
    public const int AlignofModel = 16;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec2 { public float X, Y; }

    static CubismCore()
    {
        NativeLibrary.SetDllImportResolver(typeof(CubismCore).Assembly, (name, asm, path) =>
        {
            if (name == LibName && File.Exists(VendorPaths.NativeCoreDll))
                return NativeLibrary.Load(VendorPaths.NativeCoreDll);
            return IntPtr.Zero;
        });
    }

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern uint csmGetVersion();
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern uint csmGetLatestMocVersion();
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern uint csmGetMocVersion(IntPtr address, uint size);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern int csmHasMocConsistency(IntPtr address, uint size);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmReviveMocInPlace(IntPtr address, uint size);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern uint csmGetSizeofModel(IntPtr moc);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmInitializeModelInPlace(IntPtr moc, IntPtr address, uint size);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern void csmUpdateModel(IntPtr model);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern void csmReadCanvasInfo(IntPtr model, out Vec2 sizeInPixels, out Vec2 originInPixels, out float pixelsPerUnit);

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern int csmGetParameterCount(IntPtr model);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetParameterIds(IntPtr model);          // const char**
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetParameterMinimumValues(IntPtr model); // float*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetParameterMaximumValues(IntPtr model); // float*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetParameterDefaultValues(IntPtr model); // float*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetParameterValues(IntPtr model);        // float* (可写)
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetParameterRepeats(IntPtr model);       // int*

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern int csmGetPartCount(IntPtr model);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetPartIds(IntPtr model);                // const char**
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetPartOpacities(IntPtr model);          // float* (可写)
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetPartParentPartIndices(IntPtr model);  // int*

    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern int csmGetDrawableCount(IntPtr model);
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableIds(IntPtr model);            // const char**
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableConstantFlags(IntPtr model);  // byte*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableDynamicFlags(IntPtr model);   // byte*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableTextureIndices(IntPtr model); // int*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableDrawOrders(IntPtr model);     // int*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableRenderOrders(IntPtr model);   // int*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableOpacities(IntPtr model);      // float*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableMaskCounts(IntPtr model);     // int*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableMasks(IntPtr model);          // int**
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableVertexCounts(IntPtr model);   // int*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableVertexPositions(IntPtr model);// csmVector2**
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableVertexUvs(IntPtr model);      // csmVector2**
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableIndexCounts(IntPtr model);    // int*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableIndices(IntPtr model);        // ushort**
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr csmGetDrawableParentPartIndices(IntPtr model); // int*
    [DllImport(LibName, CallingConvention = CallingConvention.StdCall)] public static extern void csmResetDrawableDynamicFlags(IntPtr model);

    // 便捷读取辅助
    public static string[] ReadStringTable(IntPtr table, int count)
    {
        var result = new string[count];
        var ptr = (IntPtr*)table;
        for (int i = 0; i < count; i++)
            result[i] = Marshal.PtrToStringAnsi(ptr[i]) ?? string.Empty;
        return result;
    }

    public static float* Ptr(IntPtr p) => (float*)p;
    public static int* IPtr(IntPtr p) => (int*)p;
    public static IntPtr* PPtr(IntPtr p) => (IntPtr*)p;
    public static Vec2* V2Ptr(IntPtr p) => (Vec2*)p;
    public static ushort* U16Ptr(IntPtr p) => (ushort*)p;
    public static byte* BPtr(IntPtr p) => (byte*)p;
}
