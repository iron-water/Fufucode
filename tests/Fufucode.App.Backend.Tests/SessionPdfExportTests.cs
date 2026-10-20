using System;
using System.IO;
using System.Text;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Backend.Tests;

/// <summary>会话 PDF 导出：真实生成文件并校验 PDF 结构与中文内容可写入。</summary>
internal static class SessionPdfExportTests
{
    public static void Run(Checks checks)
    {
        var path = Path.Combine(Path.GetTempPath(), "fufucode-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using (var stream = File.Create(path))
            {
                SessionPdfExporter.Write(
                    stream,
                    "导出测试会话",
                    new[] { ("会话 ID", "session-test"), ("项目", "demo") },
                    new[]
                    {
                        new SessionPdfMessage("你", "请检查 Program.cs 的中文排版与分页。", true),
                        new SessionPdfMessage("Fufucode", "已检查。\n\n## 结论\n\n- 中文可用\n- 代码块可用\n\n```cs\nvar x = 1;\n```\n"),
                    });
            }

            var bytes = File.ReadAllBytes(path);
            checks.Check(bytes.Length > 1000, "PDF 导出产生非空文件");
            checks.Check(Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-", "PDF 文件头为 %PDF-");
            checks.Check(Encoding.ASCII.GetString(bytes).Contains("%%EOF", StringComparison.Ordinal), "PDF 包含 EOF 标记");

            checks.Check(SessionPdfExporter.PageWidth > SessionPdfExporter.PageHeight * 0.7, "PDF 页面宽度按 A4 竖版比例");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
