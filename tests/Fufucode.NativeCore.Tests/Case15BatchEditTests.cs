using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// 真实 ProductionToolDispatcher 集成测试（可直接迁入 tests/Fufucode.NativeCore.Tests）：
/// write_file 后一次 batch edit_file，覆盖 replace_all 多命中、Unicode/代理对替换、删除；
/// 校验磁盘内容、UTF-8/UTF-16 编码保持、结构守卫失败不写盘以及原子写无 .tmp-* 残留。
/// </summary>
internal static class Case15BatchEditTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = TestTemp.Create("15");
        var workspace = Path.Combine(root, "ws");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(dataRoot);
        var previousDataRoot = Environment.GetEnvironmentVariable("FUFUCODE_DATA_ROOT");
        Environment.SetEnvironmentVariable("FUFUCODE_DATA_ROOT", dataRoot);
        try
        {
            var dispatcher = new ProductionToolDispatcher(workspace);

            // 1) write_file 建文件，再一次 batch edit：多处 replace_all + Unicode + 删除。
            var utf8File = Path.Combine(workspace, "unicode.txt");
            const string content = "header\r\nfoo-α\r\nfoo-😀\r\nOLD中文OLD\r\nDEL keep DEL keep DEL\r\ntail😀\r\n";
            var write = await dispatcher.DispatchAsync(new PureToolCall(
                "fix15-write-" + Guid.NewGuid().ToString("N"), "write_file",
                JsonSerializer.Serialize(new { path = "unicode.txt", content })));
            checks.Check(write.Success, "write_file 成功: " + write.Content);
            var writeBom = HasUtf8Bom(await File.ReadAllBytesAsync(utf8File));

            var batchArgs = JsonSerializer.Serialize(new
            {
                path = "unicode.txt",
                edits = new object[]
                {
                    new { old_string = "foo", new_string = "بار🚀", replace_all = true },
                    new { old_string = "OLD", new_string = "新✅", replace_all = true },
                    new { old_string = "DEL", new_string = "", replace_all = true },
                },
            });
            var edit = await dispatcher.DispatchAsync(new PureToolCall(
                "fix15-edit-" + Guid.NewGuid().ToString("N"), "edit_file", batchArgs));
            checks.Check(edit.Success, "batch edit_file 成功: " + edit.Content);
            checks.Check(edit.Content.Contains("共替换 7 处", StringComparison.Ordinal),
                "batch edit_file 报告 7 处替换: " + edit.Content);

            var expected = content
                .Replace("foo", "بار🚀", StringComparison.Ordinal)
                .Replace("OLD", "新✅", StringComparison.Ordinal)
                .Replace("DEL", "", StringComparison.Ordinal);
            var bytes = await File.ReadAllBytesAsync(utf8File);
            var text = DecodeUtf8(bytes, out var bom);
            checks.Equal(expected, text, "UTF-8 文件内容与批量编辑预期一致");
            checks.Check(bom == writeBom, "UTF-8 BOM 状态在编辑后保持不变");
            checks.Check(!text.Contains("foo", StringComparison.Ordinal) &&
                         !text.Contains("OLD", StringComparison.Ordinal) &&
                         !text.Contains("DEL", StringComparison.Ordinal),
                "replace_all 无残留旧文本");
            checks.Check(text.Contains("بار🚀", StringComparison.Ordinal) &&
                         text.Contains("新✅", StringComparison.Ordinal) &&
                         text.Contains("tail😀", StringComparison.Ordinal),
                "Unicode / 代理对替换内容保留");
            checks.Check(!HasTempLeftovers(workspace), "成功 batch edit 后无 .tmp-* 残留");

            // 2) UTF-16 LE BOM 文件：编辑后编码与 BOM 保持，不写成 UTF-8。
            var utf16File = Path.Combine(workspace, "utf16.txt");
            const string utf16Content = "foo α 😀\r\nfoo 中文\r\n";
            await File.WriteAllTextAsync(utf16File, utf16Content, new UnicodeEncoding(false, true));
            var utf16Edit = await dispatcher.DispatchAsync(new PureToolCall(
                "fix15-utf16-" + Guid.NewGuid().ToString("N"), "edit_file",
                JsonSerializer.Serialize(new
                {
                    path = "utf16.txt",
                    old_string = "foo",
                    new_string = "ب🚀",
                    replace_all = true,
                })));
            checks.Check(utf16Edit.Success, "UTF-16 edit_file 成功: " + utf16Edit.Content);
            var utf16Bytes = await File.ReadAllBytesAsync(utf16File);
            checks.Check(utf16Bytes.Length >= 2 && utf16Bytes[0] == 0xFF && utf16Bytes[1] == 0xFE,
                "UTF-16 LE BOM 保持");
            var utf16Text = Encoding.Unicode.GetString(utf16Bytes, 2, utf16Bytes.Length - 2);
            checks.Equal(utf16Content.Replace("foo", "ب🚀", StringComparison.Ordinal), utf16Text,
                "UTF-16 内容与替换预期一致");
            checks.Check(!HasTempLeftovers(workspace), "UTF-16 编辑后无 .tmp-* 残留");

            // 3) 相邻区间：三个替换范围首尾相接（0-2、2-4、4-6），一次拼接 gap=0。
            var adjacentFile = Path.Combine(workspace, "adjacent.txt");
            const string adjacentContent = "abcdef";
            var adjacentWrite = await dispatcher.DispatchAsync(new PureToolCall(
                "fix15-adjacent-write-" + Guid.NewGuid().ToString("N"), "write_file",
                JsonSerializer.Serialize(new { path = "adjacent.txt", content = adjacentContent })));
            checks.Check(adjacentWrite.Success, "write_file 相邻区间文件成功: " + adjacentWrite.Content);
            var adjacentArgs = JsonSerializer.Serialize(new
            {
                path = "adjacent.txt",
                edits = new object[]
                {
                    new { old_string = "ab", new_string = "XY" },
                    new { old_string = "cd", new_string = "" },
                    new { old_string = "ef", new_string = "Z" },
                },
            });
            var adjacentEdit = await dispatcher.DispatchAsync(new PureToolCall(
                "fix15-adjacent-edit-" + Guid.NewGuid().ToString("N"), "edit_file", adjacentArgs));
            checks.Check(adjacentEdit.Success, "相邻区间 batch edit_file 成功: " + adjacentEdit.Content);
            checks.Check(adjacentEdit.Content.Contains("共替换 3 处", StringComparison.Ordinal),
                "相邻区间 batch edit_file 报告 3 处替换: " + adjacentEdit.Content);
            checks.Equal("XYZ", await File.ReadAllTextAsync(adjacentFile),
                "相邻区间 ab->XY、cd->删除、ef->Z 内容正确");
            checks.Check(!HasTempLeftovers(workspace), "相邻区间编辑后无 .tmp-* 残留");

            // 4) 结构守卫失败：不写盘、原字节不变、无临时文件。
            var jsonFile = Path.Combine(workspace, "config.json");
            const string jsonContent = "{\"a\":1}";
            var jsonWrite = await dispatcher.DispatchAsync(new PureToolCall(
                "fix15-json-write-" + Guid.NewGuid().ToString("N"), "write_file",
                JsonSerializer.Serialize(new { path = "config.json", content = jsonContent })));
            checks.Check(jsonWrite.Success, "write_file JSON 成功: " + jsonWrite.Content);
            var beforeBytes = await File.ReadAllBytesAsync(jsonFile);
            var jsonEdit = await dispatcher.DispatchAsync(new PureToolCall(
                "fix15-json-edit-" + Guid.NewGuid().ToString("N"), "edit_file",
                JsonSerializer.Serialize(new { path = "config.json", old_string = "}", new_string = "]" })));
            checks.Check(!jsonEdit.Success, "非法 JSON 编辑被结构守卫拒绝");
            checks.Check(jsonEdit.Content.Contains("不是合法 JSON", StringComparison.Ordinal),
                "结构守卫失败信息明确: " + jsonEdit.Content);
            var afterBytes = await File.ReadAllBytesAsync(jsonFile);
            checks.Check(SHA256.HashData(beforeBytes).AsSpan().SequenceEqual(SHA256.HashData(afterBytes)),
                "结构守卫拒绝后文件字节完全未变（未写盘）");
            checks.Check(!HasTempLeftovers(workspace), "结构守卫拒绝后无 .tmp-* 残留");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUFUCODE_DATA_ROOT", previousDataRoot);
            TestTemp.Delete(root);
        }
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string DecodeUtf8(byte[] bytes, out bool bom)
    {
        bom = HasUtf8Bom(bytes);
        var start = bom ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
    }

    private static bool HasTempLeftovers(string workspace) =>
        Directory.EnumerateFiles(workspace, "*.tmp-*", SearchOption.AllDirectories).Any();
}
