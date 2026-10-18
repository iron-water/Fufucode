using System.Text;

namespace Fufucode.Core.RemoteControl;

/// <summary>
/// 配对 URL 的最小二维码编码器（字节模式、纠错等级 L、版本 1-6，ISO/IEC 18004）。
/// 自包含、无第三方依赖：只生成模块矩阵与 SVG，供桌面端展示供手机扫码。
/// 版本 1-6 的字节模式最多容纳 134 字节，足以覆盖局域网配对 URL；
/// 超出范围或输入为空时返回 null，由调用方降级为纯文本地址，不产生不可扫的错误图案。
/// </summary>
public static class RemoteControlQr
{
    /// <summary>支持的最大版本；版本 1-6 的对齐图案坐标只有 6 与 边长-7 两组。</summary>
    private const int MaxVersion = 6;

    /// <summary>版本 1-6 在纠错等级 L 下的数据码字数（不含纠错码字）。</summary>
    private static readonly int[] DataCodewords = [19, 34, 55, 80, 108, 136];

    /// <summary>每个纠错块的纠错码字数（等级 L）。</summary>
    private static readonly int[] EcCodewordsPerBlock = [7, 10, 15, 20, 26, 18];

    /// <summary>纠错块数量（等级 L）。</summary>
    private static readonly int[] BlockCount = [1, 1, 1, 1, 1, 2];

    /// <summary>版本的总码字数（数据 + 纠错，用于模块放置）。</summary>
    private static readonly int[] TotalCodewords = [26, 44, 70, 100, 134, 172];

    /// <summary>把文本编码为二维码模块矩阵（true = 深色模块）。无法编码时返回 null。</summary>
    public static bool[,]? Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var payload = Encoding.UTF8.GetBytes(text);
        var version = SelectVersion(payload.Length);
        if (version == 0) return null;

        var data = BuildDataCodewords(payload, version);
        var all = AddErrorCorrection(data, version);
        var matrix = new QrMatrix(version * 4 + 17);
        matrix.DrawFunctionPatterns(version);
        PlaceDataAndChooseMask(matrix, all);
        return matrix.Modules;
    }

    /// <summary>把文本渲染为可直接展示的 SVG；无法编码时返回 null。</summary>
    public static string? ToSvg(string text, int moduleSize = 4, int quietZone = 4)
    {
        var modules = Encode(text);
        if (modules is null) return null;
        var count = modules.GetLength(0);
        var size = (count + quietZone * 2) * moduleSize;
        var builder = new StringBuilder();
        builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(size)
            .Append("\" height=\"").Append(size).Append("\" viewBox=\"0 0 ").Append(size).Append(' ').Append(size)
            .Append("\" aria-label=\"配对二维码\">");
        builder.Append("<rect width=\"").Append(size).Append("\" height=\"").Append(size)
            .Append("\" fill=\"#ffffff\"/>");
        builder.Append("<path fill=\"#000000\" d=\"");
        for (var y = 0; y < count; y++)
        {
            var run = 0;
            for (var x = 0; x <= count; x++)
            {
                if (x < count && modules[y, x])
                {
                    run++;
                    continue;
                }

                if (run > 0)
                {
                    var left = (x - run + quietZone) * moduleSize;
                    var top = (y + quietZone) * moduleSize;
                    builder.Append('M').Append(left).Append(' ').Append(top)
                        .Append('h').Append(run * moduleSize).Append('v').Append(moduleSize)
                        .Append('h').Append(-run * moduleSize).Append('z');
                    run = 0;
                }
            }
        }

        builder.Append("\"/></svg>");
        return builder.ToString();
    }

    /// <summary>把矩阵渲染为一行一行的 '0'/'1' 文本（测试与诊断用）。</summary>
    public static string? ToText(string text)
    {
        var modules = Encode(text);
        if (modules is null) return null;
        var builder = new StringBuilder();
        for (var y = 0; y < modules.GetLength(0); y++)
        {
            if (y > 0) builder.Append('\n');
            for (var x = 0; x < modules.GetLength(1); x++) builder.Append(modules[y, x] ? '1' : '0');
        }

        return builder.ToString();
    }

    private static int SelectVersion(int byteCount)
    {
        for (var version = 1; version <= MaxVersion; version++)
        {
            // 字节模式：4 位模式指示符 + 8 位长度 + 数据。
            if (4 + 8 + byteCount * 8 <= DataCodewords[version - 1] * 8) return version;
        }

        return 0;
    }

    private static byte[] BuildDataCodewords(byte[] payload, int version)
    {
        var capacity = DataCodewords[version - 1];
        var bits = new List<bool>(capacity * 8);
        AppendBits(bits, 0b0100, 4); // 字节模式指示符
        AppendBits(bits, payload.Length, 8);
        foreach (var value in payload) AppendBits(bits, value, 8);

        var terminator = Math.Min(4, capacity * 8 - bits.Count);
        AppendBits(bits, 0, terminator);
        while (bits.Count % 8 != 0) bits.Add(false);

        var dataLength = bits.Count / 8;
        var codewords = new byte[capacity];
        for (var i = 0; i < dataLength; i++)
        {
            var value = 0;
            for (var b = 0; b < 8; b++) value = (value << 1) | (bits[i * 8 + b] ? 1 : 0);
            codewords[i] = (byte)value;
        }

        // 填充字节在 0xEC / 0x11 之间交替（ISO/IEC 18004 §8.4.9）。
        for (var i = dataLength; i < capacity; i++)
            codewords[i] = (byte)((i - dataLength) % 2 == 0 ? 0xEC : 0x11);

        return codewords;
    }

    private static void AppendBits(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    private static byte[] AddErrorCorrection(byte[] data, int version)
    {
        var total = TotalCodewords[version - 1];
        var blockCount = BlockCount[version - 1];
        var ecPerBlock = EcCodewordsPerBlock[version - 1];
        var shortLength = data.Length / blockCount;
        var shortBlocks = blockCount - data.Length % blockCount;

        var dataBlocks = new List<byte[]>(blockCount);
        var ecBlocks = new List<byte[]>(blockCount);
        var offset = 0;
        for (var block = 0; block < blockCount; block++)
        {
            var length = shortLength + (block >= shortBlocks ? 1 : 0);
            var slice = data.AsSpan(offset, length).ToArray();
            offset += length;
            dataBlocks.Add(slice);
            ecBlocks.Add(ComputeEc(slice, ecPerBlock));
        }

        // 交错：先逐列取数据码字，再逐列取纠错码字。
        var result = new byte[total];
        var index = 0;
        var maxData = dataBlocks.Max(block => block.Length);
        for (var i = 0; i < maxData; i++)
        {
            foreach (var block in dataBlocks)
            {
                if (i < block.Length) result[index++] = block[i];
            }
        }

        for (var i = 0; i < ecPerBlock; i++)
        {
            foreach (var block in ecBlocks) result[index++] = block[i];
        }

        return result;
    }

    private static readonly byte[] GfExp = BuildExpTable();
    private static readonly byte[] GfLog = BuildLogTable();

    private static byte[] BuildExpTable()
    {
        var table = new byte[512];
        var value = 1;
        for (var i = 0; i < 255; i++)
        {
            table[i] = (byte)value;
            value <<= 1;
            if ((value & 0x100) != 0) value ^= 0x11D; // x^8 + x^4 + x^3 + x^2 + 1
        }

        for (var i = 255; i < 512; i++) table[i] = table[i - 255];
        return table;
    }

    private static byte[] BuildLogTable()
    {
        var table = new byte[256];
        for (var i = 0; i < 255; i++) table[GfExp[i]] = (byte)i;
        return table;
    }

    private static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return GfExp[GfLog[a] + GfLog[b]];
    }

    private static byte[] ComputeEc(byte[] data, int ecLength)
    {
        var generator = GeneratorPolynomial(ecLength);
        var remainder = new byte[ecLength];
        foreach (var value in data)
        {
            var factor = (byte)(value ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, ecLength - 1);
            remainder[ecLength - 1] = 0;
            for (var i = 0; i < ecLength; i++) remainder[i] ^= Multiply(generator[i], factor);
        }

        return remainder;
    }

    /// <summary>生成纠错多项式的低次系数（最高次项系数恒为 1，不需要返回）。</summary>
    private static byte[] GeneratorPolynomial(int degree)
    {
        var polynomial = new byte[] { 1 };
        for (var i = 0; i < degree; i++)
        {
            var next = new byte[polynomial.Length + 1];
            for (var j = 0; j < polynomial.Length; j++)
            {
                next[j] ^= polynomial[j];
                next[j + 1] ^= Multiply(polynomial[j], GfExp[i]);
            }

            polynomial = next;
        }

        return polynomial.Skip(1).ToArray();
    }

    private sealed class QrMatrix(int size)
    {
        public bool[,] Modules { get; } = new bool[size, size];

        private bool[,] Reserved { get; } = new bool[size, size];

        public int Size => Modules.GetLength(0);

        public void DrawFunctionPatterns(int version)
        {
            DrawFinder(0, 0);
            DrawFinder(Size - 7, 0);
            DrawFinder(0, Size - 7);
            DrawTiming();
            DrawAlignment(version);
            ReserveFormatAreas();
        }

        private void DrawFinder(int left, int top)
        {
            for (var y = -1; y <= 7; y++)
            {
                for (var x = -1; x <= 7; x++)
                {
                    var px = left + x;
                    var py = top + y;
                    if (px < 0 || py < 0 || px >= Size || py >= Size) continue;
                    var dark = x is >= 0 and <= 6 && y is >= 0 and <= 6 &&
                               (x is 0 or 6 || y is 0 or 6 || (x is >= 2 and <= 4 && y is >= 2 and <= 4));
                    Modules[py, px] = dark;
                    Reserved[py, px] = true;
                }
            }
        }

        private void DrawTiming()
        {
            for (var i = 8; i < Size - 8; i++)
            {
                Modules[6, i] = i % 2 == 0;
                Reserved[6, i] = true;
                Modules[i, 6] = i % 2 == 0;
                Reserved[i, 6] = true;
            }
        }

        /// <summary>版本 2-6 的对齐图案坐标只有 6 与 边长-7 两组，与定位图案重叠的位置跳过。</summary>
        private void DrawAlignment(int version)
        {
            if (version == 1) return;
            foreach (var cy in new[] { 6, Size - 7 })
            {
                foreach (var cx in new[] { 6, Size - 7 })
                {
                    if (Reserved[cy, cx]) continue;
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            Modules[cy + dy, cx + dx] = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                            Reserved[cy + dy, cx + dx] = true;
                        }
                    }
                }
            }
        }

        /// <summary>预留格式信息区域（内容随后由 <see cref="DrawFormatInfo"/> 填写）。</summary>
        private void ReserveFormatAreas()
        {
            for (var i = 0; i <= 8; i++)
            {
                Reserved[8, i] = true;
                Reserved[i, 8] = true;
            }

            for (var i = 0; i < 8; i++)
            {
                Reserved[8, Size - 1 - i] = true;
                Reserved[Size - 1 - i, 8] = true;
            }
        }

        /// <summary>写入两份格式信息副本与固定深色模块（ISO/IEC 18004 图 25）。</summary>
        public void DrawFormatInfo(int mask)
        {
            // 纠错等级 L = 0b01，加 3 位掩码；随后是 BCH(15,5) 校验与固定掩码 0x5412。
            var data = (0b01 << 3) | mask;
            var remainder = data;
            for (var i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            var bits = (data << 10 | remainder) ^ 0x5412;

            // 副本 1：左上角，沿第 8 列向下、第 8 行向左。
            for (var i = 0; i <= 5; i++) Modules[i, 8] = Bit(bits, i);
            Modules[7, 8] = Bit(bits, 6);
            Modules[8, 8] = Bit(bits, 7);
            Modules[8, 7] = Bit(bits, 8);
            for (var i = 9; i < 15; i++) Modules[8, 14 - i] = Bit(bits, i);

            // 副本 2：第 8 行右侧横条与第 8 列底部竖条。
            for (var i = 0; i < 8; i++) Modules[8, Size - 1 - i] = Bit(bits, i);
            for (var i = 8; i < 15; i++) Modules[Size - 15 + i, 8] = Bit(bits, i);

            Modules[Size - 8, 8] = true;
        }

        public bool IsReserved(int x, int y) => Reserved[y, x];

        public void Set(int x, int y, bool dark) => Modules[y, x] = dark;

        public bool Get(int x, int y) => Modules[y, x];

        public QrMatrix Clone()
        {
            var copy = new QrMatrix(Size);
            Array.Copy(Modules, copy.Modules, Modules.Length);
            Array.Copy(Reserved, copy.Reserved, Reserved.Length);
            return copy;
        }

        private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
    }

    private static void PlaceDataAndChooseMask(QrMatrix matrix, byte[] codewords)
    {
        var bits = new bool[codewords.Length * 8];
        for (var i = 0; i < codewords.Length; i++)
        {
            for (var b = 0; b < 8; b++) bits[i * 8 + b] = ((codewords[i] >> (7 - b)) & 1) != 0;
        }

        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        QrMatrix? best = null;
        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = matrix.Clone();
            PlaceData(candidate, bits, mask);
            candidate.DrawFormatInfo(mask);
            var penalty = Penalty(candidate);
            if (penalty >= bestPenalty) continue;
            bestPenalty = penalty;
            bestMask = mask;
            best = candidate;
        }

        Array.Copy(best!.Modules, matrix.Modules, best.Modules.Length);
        matrix.DrawFormatInfo(bestMask);
    }

    private static void PlaceData(QrMatrix matrix, bool[] bits, int mask)
    {
        var index = 0;
        for (var right = matrix.Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // 跳过垂直计时列
            var upward = ((right + 1) & 2) == 0;
            for (var step = 0; step < matrix.Size; step++)
            {
                var y = upward ? matrix.Size - 1 - step : step;
                for (var offset = 0; offset < 2; offset++)
                {
                    var x = right - offset;
                    if (matrix.IsReserved(x, y)) continue;
                    var dark = index < bits.Length && bits[index];
                    index++;
                    matrix.Set(x, y, dark ^ MaskBit(mask, x, y));
                }
            }
        }
    }

    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    private static int Penalty(QrMatrix matrix)
    {
        var size = matrix.Size;
        var score = 0;

        // 规则 1：连续同色模块（行与列），每段超过 5 个的部分每模块计 3 分。
        for (var y = 0; y < size; y++) score += LinePenalty(i => matrix.Get(i, y), size);
        for (var x = 0; x < size; x++) score += LinePenalty(i => matrix.Get(x, i), size);

        // 规则 2：2x2 同色块，每块计 3 分。
        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var value = matrix.Get(x, y);
                if (value == matrix.Get(x + 1, y) && value == matrix.Get(x, y + 1) &&
                    value == matrix.Get(x + 1, y + 1))
                    score += 3;
            }
        }

        // 规则 3：形如 1:1:3:1:1 且一侧有 4 个浅色模块的图案，每处计 40 分。
        int[] forward = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];
        int[] backward = [0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x + 11 <= size; x++)
                if (Matches(matrix, x, y, horizontal: true, forward) ||
                    Matches(matrix, x, y, horizontal: true, backward))
                    score += 40;
        }

        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y + 11 <= size; y++)
                if (Matches(matrix, x, y, horizontal: false, forward) ||
                    Matches(matrix, x, y, horizontal: false, backward))
                    score += 40;
        }

        // 规则 4：深色模块占比偏离 50%，以 5% 为步长计分。
        var dark = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (matrix.Get(x, y)) dark++;
            }
        }

        var total = size * size;
        var deviation = Math.Abs(dark * 100 - total * 50) / total; // 百分比偏差
        score += deviation / 5 * 10;
        return score;
    }

    private static int LinePenalty(Func<int, bool> read, int length)
    {
        var score = 0;
        var run = 1;
        var previous = read(0);
        for (var i = 1; i < length; i++)
        {
            var current = read(i);
            if (current == previous)
            {
                run++;
                continue;
            }

            if (run >= 5) score += run - 2;
            run = 1;
            previous = current;
        }

        if (run >= 5) score += run - 2;
        return score;
    }

    private static bool Matches(QrMatrix matrix, int x, int y, bool horizontal, int[] pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            var value = horizontal ? matrix.Get(x + i, y) : matrix.Get(x, y + i);
            if (value != (pattern[i] == 1)) return false;
        }

        return true;
    }
}
