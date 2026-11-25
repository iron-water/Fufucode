using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Fufucode.App.Abstractions;
using SkiaSharp;

namespace Fufucode.Fui.Tests;

internal static class ImageRenderTests
{
    internal static async Task RunAsync()
    {
        TestDecoders();
        TestSvgSecurityAndDoctype();
        TestSixelColorAndAlpha();
        TestMarkdownImageParsingAndStreaming();
        await TestProjectTranscodingAndLayoutAsync();
        await TestClipboardPasteAndControllerAsync();
        TestLegacyNonBracketedPasteTerminal();
    }

    private static void TestDecoders()
    {
        // PNG + APNG
        var pngAlpha = MakeSkiaBytes(SKEncodedImageFormat.Png, 12, 8, alpha: true);
        var dPng = ImageDecoder.TryDecode(pngAlpha, "image/png");
        Check(dPng is { Width: 12, Height: 8, FormatLabel: "PNG" } && dPng.Rgba[3] == 0 && dPng.Rgba[(4 * 12 + 6) * 4 + 3] == 255,
            "PNG decodes dimensions and alpha");
        Check(ProjectImage.MimeType("anim.apng") == "image/png" && ProjectImage.Matches(pngAlpha, "image/png"),
            "APNG extension uses PNG container decoder");

        // JPEG + EXIF Orientation 6 (90 deg CW swaps 10x6 -> 6x10)
        var jpeg = MakeSkiaBytes(SKEncodedImageFormat.Jpeg, 10, 6, alpha: false);
        var dJpeg = ImageDecoder.TryDecode(jpeg, "image/jpeg");
        Check(dJpeg is { Width: 10, Height: 6, FormatLabel: "JPEG" }, "JPEG decodes cleanly");
        var exifJpeg = InjectExifOrientation(jpeg, 6);
        var dExif = ImageDecoder.TryDecode(exifJpeg, "image/jpeg");
        Check(dExif is { Width: 6, Height: 10, FormatLabel: "JPEG" }, "JPEG EXIF orientation 6 swaps width and height");

        // WebP
        var webp = MakeSkiaBytes(SKEncodedImageFormat.Webp, 10, 8, alpha: true);
        var dWebp = ImageDecoder.TryDecode(webp, "image/webp");
        Check(dWebp is { Width: 10, Height: 8, FormatLabel: "WEBP" }, "WebP decodes cleanly");

        // GIF
        var gif = MakeMinimalGif();
        var dGif = ImageDecoder.TryDecode(gif, "image/gif");
        Check(dGif is { Width: 2, Height: 2, FormatLabel: "GIF" }, "GIF decodes cleanly");

        // BMP + headerless DIB
        var bmp = MakeBmp24(4, 3);
        var dBmp = ImageDecoder.TryDecode(bmp, "image/bmp");
        Check(dBmp is { Width: 4, Height: 3, FormatLabel: "BMP" }, "BMP decodes cleanly");
        var dib = bmp[14..];
        var dDib = ImageDecoder.TryDecode(dib, "image/bmp");
        Check(dDib is { Width: 4, Height: 3, FormatLabel: "DIB" }, "Headerless DIB decodes via BMP wrapper");

        // ICO + CUR (PNG-inside-ICO and re-tagged CUR)
        var ico = MakeIcoOrCur(pngAlpha, isCur: false);
        var cur = MakeIcoOrCur(pngAlpha, isCur: true);
        var dIco = ImageDecoder.TryDecode(ico, "image/x-icon");
        var dCur = ImageDecoder.TryDecode(cur, "image/x-win-bitmap");
        Check(dIco is { Width: 12, Height: 8 }, "ICO decodes cleanly");
        Check(dCur is { Width: 12, Height: 8, FormatLabel: "CUR" }, "CUR decodes cleanly");

        // QOI (RGBA + RUN/INDEX/DIFF/LUMA)
        var qoi = MakeQoi(4, 3);
        var dQoi = ImageDecoder.TryDecode(qoi, "image/qoi");
        Check(dQoi is { Width: 4, Height: 3, FormatLabel: "QOI" } && dQoi.Rgba[0] == 240 && dQoi.Rgba[1] == 80 && dQoi.Rgba[2] == 40 && dQoi.Rgba[3] == 255,
            "QOI decodes RGBA pixels accurately");

        // TGA (uncompressed 24-bit and RLE 32-bit with alpha)
        var tgaRaw = MakeTga24(3, 2);
        var dTgaRaw = ImageDecoder.TryDecode(tgaRaw, "image/x-tga");
        Check(dTgaRaw is { Width: 3, Height: 2, FormatLabel: "TGA" } && dTgaRaw.Rgba[0] == 200 && dTgaRaw.Rgba[1] == 100 && dTgaRaw.Rgba[2] == 50,
            "Uncompressed TGA decodes RGB accurately");
        var tgaRle = MakeTgaRle32(4, 2);
        var dTgaRle = ImageDecoder.TryDecode(tgaRle, "image/x-tga");
        Check(dTgaRle is { Width: 4, Height: 2, FormatLabel: "TGA" } && dTgaRle.Rgba[3] == 128,
            "RLE 32-bit TGA decodes alpha accurately");

        // Netpbm: P1, P2, P3, P4, P5 (8 & 16-bit), P6, P7 PAM (RGBA)
        Check(ImageDecoder.TryDecode("P1\n2 2\n0 1 1 0\n"u8, "image/x-portable-bitmap") is { Width: 2, Height: 2, FormatLabel: "PBM" }, "P1 PBM");
        Check(ImageDecoder.TryDecode("P2\n2 2\n255\n0 128 200 255\n"u8, "image/x-portable-graymap") is { Width: 2, Height: 2, FormatLabel: "PGM" }, "P2 PGM");
        Check(ImageDecoder.TryDecode("P3\n2 1\n255\n255 0 0 0 255 0\n"u8, "image/x-portable-pixmap") is { Width: 2, Height: 1, FormatLabel: "PPM" } p3 && p3.Rgba[0] == 255 && p3.Rgba[5] == 255, "P3 PPM");
        Check(ImageDecoder.TryDecode([.. "P4\n2 1\n"u8, 0x80], "image/x-portable-bitmap") is { Width: 2, Height: 1, FormatLabel: "PBM" }, "P4 binary PBM");
        Check(ImageDecoder.TryDecode([.. "P5\n2 1\n255\n"u8, 10, 250], "image/x-portable-graymap") is { Width: 2, Height: 1, FormatLabel: "PGM" }, "P5 8-bit PGM");
        Check(ImageDecoder.TryDecode([.. "P5\n1 1\n65535\n"u8, 0x80, 0x00], "image/x-portable-graymap") is { Width: 1, Height: 1, FormatLabel: "PGM" } p516 && p516.Rgba[0] is >= 127 and <= 129, "P5 16-bit PGM");
        Check(ImageDecoder.TryDecode([.. "P6\n2 1\n255\n"u8, 10, 20, 30, 40, 50, 60], "image/x-portable-pixmap") is { Width: 2, Height: 1, FormatLabel: "PPM" }, "P6 binary PPM");
        var pam = (byte[])[.. "P7\nWIDTH 2\nHEIGHT 1\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n"u8, 255, 0, 0, 255, 0, 0, 255, 0];
        Check(ImageDecoder.TryDecode(pam, "image/x-portable-arbitrarymap") is { Width: 2, Height: 1, FormatLabel: "PAM" } dPam && dPam.Rgba[0] == 255 && dPam.Rgba[7] == 0, "P7 PAM RGBA");

        // Farbfeld
        var ff = MakeFarbfeld(2, 2);
        Check(ImageDecoder.TryDecode(ff, "image/x-farbfeld") is { Width: 2, Height: 2, FormatLabel: "FARBFELD" } dFf && dFf.Rgba[0] == 0xaa && dFf.Rgba[3] == 0xff,
            "Farbfeld 16-bit RGBA");

        // Radiance HDR (uncompressed + new-style RLE)
        var hdrRaw = MakeHdr(4, 2, rle: false);
        var hdrRle = MakeHdr(10, 2, rle: true);
        Check(ImageDecoder.TryDecode(hdrRaw, "image/vnd.radiance") is { Width: 4, Height: 2, FormatLabel: "HDR" } dHdrRaw && dHdrRaw.Rgba[0] > 150,
            "Radiance HDR uncompressed");
        Check(ImageDecoder.TryDecode(hdrRle, "image/vnd.radiance") is { Width: 10, Height: 2, FormatLabel: "HDR" } dHdrRle && dHdrRle.Rgba[0] > 150,
            "Radiance HDR RLE");

        // WBMP
        var wbmp = new byte[] { 0x00, 0x00, 0x04, 0x02, 0xf0, 0x00 };
        Check(ImageDecoder.TryDecode(wbmp, "image/vnd.wap.wbmp") is { Width: 4, Height: 2, FormatLabel: "WBMP" }, "WBMP via Skia");

        // Windows WIC: real uncompressed TIFF
        if (OperatingSystem.IsWindows())
        {
            var tiff = MakeMinimalTiff(4, 3);
            var dTiff = ImageDecoder.TryDecode(tiff, "image/tiff");
            Check(dTiff is { Width: 4, Height: 3, FormatLabel: "TIFF" } && dTiff.Rgba[0] == 220 && dTiff.Rgba[1] == 110 && dTiff.Rgba[2] == 55,
                "Windows WIC decodes TIFF with exact RGB channel order");
        }

        // Bomb dimension rejection (claims 30000x30000 in QOI/Farbfeld/PAM without allocating)
        var bombQoi = new byte[22];
        "qoif"u8.CopyTo(bombQoi);
        BinaryPrimitives.WriteInt32BigEndian(bombQoi.AsSpan(4), 30000);
        BinaryPrimitives.WriteInt32BigEndian(bombQoi.AsSpan(8), 30000);
        bombQoi[12] = 4; bombQoi[13] = 0;
        Check(ImageDecoder.TryDecode(bombQoi, "image/qoi") is null, "oversized QOI rejected safely");
    }

    private static void TestSvgSecurityAndDoctype()
    {
        var safeSvg = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg xmlns="http://www.w3.org/2000/svg" width="48" height="32" viewBox="0 0 48 32">
              <rect x="4" y="4" width="40" height="24" rx="4" fill="#38bdf8"/>
              <circle cx="24" cy="16" r="8" fill="#f43f5e"/>
            </svg>
            """);
        var decoded = ImageDecoder.TryDecode(safeSvg, "image/svg+xml", 120, 80);
        Check(decoded is { FormatLabel: "SVG", Width: > 0, Height: > 0 }, "SVG with standard DOCTYPE renders cleanly");

        // SVGZ (gzipped SVG)
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true)) gz.Write(safeSvg);
        var svgz = ms.ToArray();
        Check(ImageDecoder.TryDecode(svgz, "image/svg+xml", 120, 80) is { FormatLabel: "SVG", Width: > 0, Height: > 0 }, "SVGZ decodes cleanly");

        // Security rejections
        string[] unsafeSvgs =
        [
            """<!DOCTYPE svg [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]><svg xmlns="http://www.w3.org/2000/svg"><text>&xxe;</text></svg>""",
            """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script><rect width="10" height="10"/></svg>""",
            """<svg xmlns="http://www.w3.org/2000/svg"><foreignObject width="10" height="10"/></svg>""",
            """<svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10" onclick="alert(1)"/></svg>""",
            """<svg xmlns="http://www.w3.org/2000/svg"><image href="https://example.com/a.png" width="10" height="10"/></svg>""",
            """<svg xmlns="http://www.w3.org/2000/svg"><style>.a{background:url(http://evil.invalid/x)}</style><rect width="10" height="10"/></svg>"""
        ];
        foreach (var bad in unsafeSvgs)
            Check(ImageDecoder.TryDecode(Encoding.UTF8.GetBytes(bad), "image/svg+xml") is null, "unsafe SVG rejected: " + bad[..Math.Min(40, bad.Length)]);
    }

    private static void TestSixelColorAndAlpha()
    {
        var darkCell = new ImageCells(10, 20, Foreground: 0xF2F1EE, Background: 0x10141A);
        var lightCell = new ImageCells(10, 20, Foreground: 0x222222, Background: 0xF8F9FA);

        // 1. Exact palette with transparent margin and semi-transparent center
        var rgba = new byte[24 * 24 * 4];
        for (var y = 6; y < 18; y++)
            for (var x = 6; x < 18; x++)
            {
                var p = (y * 24 + x) * 4;
                rgba[p] = 255; rgba[p + 1] = 80; rgba[p + 2] = 40;
                rgba[p + 3] = x < 12 ? (byte)255 : (byte)128;
            }
        var darkStrips = SixelImage.EncodeColorStrips(rgba, 24, 24, darkCell);
        var lightStrips = SixelImage.EncodeColorStrips(rgba, 24, 24, lightCell);
        Check(darkStrips.Length == 2 && darkStrips[0].StartsWith("\x1bP0;1;0q", StringComparison.Ordinal) &&
              darkStrips[0].EndsWith("\x1b\\", StringComparison.Ordinal), "full-color SIXEL uses P2=1 transparent mode");
        Check(darkStrips[0] != lightStrips[0], "semi-transparent edges blend against terminal background color");

        // 2. Truecolor gradient (> 240 unique colors) hits median-cut + Floyd-Steinberg dithering
        var grad = new byte[40 * 30 * 4];
        for (var y = 0; y < 30; y++)
            for (var x = 0; x < 40; x++)
            {
                var p = (y * 40 + x) * 4;
                grad[p] = (byte)(x * 6); grad[p + 1] = (byte)(y * 8); grad[p + 2] = (byte)((x + y) * 3); grad[p + 3] = 255;
            }
        var gradStrips = SixelImage.EncodeColorStrips(grad, 40, 30, darkCell);
        Check(gradStrips.Length == 2 && gradStrips.All(s => s.StartsWith("\x1bP0;1;0q", StringComparison.Ordinal) && s.Length > 40),
            "median-cut + dithering encodes >240 color gradients cleanly");

        // 3. Fully transparent image produces empty strips
        var emptyStrips = SixelImage.EncodeColorStrips(new byte[16 * 16 * 4], 16, 16, darkCell);
        Check(emptyStrips.Length == 1 && emptyStrips[0] == "", "all-transparent image emits no SIXEL noise");
    }

    private static void TestMarkdownImageParsingAndStreaming()
    {
        var refs = MarkdownImages.Extract("""Look ![logo](<dir/my logo.png> "t") and <img alt="hero" src="hero.svg"/> and `![skip](no.png)`.""");
        Check(refs.Count == 2 && refs[0].Target == "dir/my logo.png" && refs[0].Alt == "logo" &&
              refs[1].Target == "hero.svg" && refs[1].Alt == "hero", "extracts Markdown + HTML images while ignoring inline code");

        var png = MakeSkiaBytes(SKEncodedImageFormat.Png, 4, 4, alpha: false);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
        Check(MarkdownImages.TryDecodeDataUri(dataUri, out var mime, out var decodedBytes) &&
              mime == "image/png" && decodedBytes.SequenceEqual(png), "decodes base64 data URI");

        var svgUri = "data:image/svg+xml," + Uri.EscapeDataString("""<svg xmlns="http://www.w3.org/2000/svg" width="8" height="8"><rect width="8" height="8" fill="red"/></svg>""");
        Check(MarkdownImages.TryDecodeDataUri(svgUri, out var svgMime, out var svgBytes) &&
              svgMime == "image/svg+xml" && ImageDecoder.LooksLikeSvg(svgBytes), "decodes URL-encoded SVG data URI");

        // Streaming incomplete data URI must collapse instead of flooding screen with base64
        var partialMd = "Rendering: ![chart](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA";
        var collapsedMd = MarkdownImages.CollapseIncompleteDataUri(partialMd);
        Check(collapsedMd == "Rendering: [图片加载中：image/png…]" && !collapsedMd.Contains("iVBOR", StringComparison.Ordinal),
            "incomplete streaming Markdown data URI collapses cleanly");

        var partialHtml = """See <img alt="x" src="data:image/webp;base64,UklGRh4AAABXRUJQ""";
        var collapsedHtml = MarkdownImages.CollapseIncompleteDataUri(partialHtml);
        Check(collapsedHtml == "See [图片加载中：image/webp…]", "incomplete streaming HTML data URI collapses cleanly");

        // Inline code containing partial data URI stays literal
        var codeData = "`![x](data:image/png;base64,AAAA`";
        Check(MarkdownImages.CollapseIncompleteDataUri(codeData) == codeData, "inline code data URI stays literal");
    }

    private static async Task TestProjectTranscodingAndLayoutAsync()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath(),
            "fui-img-render-" + Guid.NewGuid().ToString("N")));
        try
        {
            var project = root.CreateSubdirectory("project").FullName;
            var data = root.CreateSubdirectory("data").FullName;
            await File.WriteAllBytesAsync(Path.Combine(project, "diagram.svg"),
                Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="40" height="24"><rect width="40" height="24" fill="#22c55e"/></svg>"""));
            await File.WriteAllBytesAsync(Path.Combine(project, "photo.webp"), MakeSkiaBytes(SKEncodedImageFormat.Webp, 16, 12, alpha: true));
            await File.WriteAllBytesAsync(Path.Combine(project, "sprite.qoi"), MakeQoi(8, 8));
            await File.WriteAllBytesAsync(Path.Combine(project, "scene.hdr"), MakeHdr(8, 4, rle: true));
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "secret.png"), MakeSkiaBytes(SKEncodedImageFormat.Png, 4, 4, alpha: false));

            var files = new ProjectFiles(project, data);

            // WebP stays native on wire; SVG, QOI, HDR transcode to valid PNG
            var webpAtt = await files.ReadAttachmentAsync("photo.webp", default);
            var svgAtt = await files.ReadAttachmentAsync("diagram.svg", default);
            var qoiAtt = await files.ReadAttachmentAsync("sprite.qoi", default);
            var hdrAtt = await files.ReadAttachmentAsync("scene.hdr", default);
            Check(webpAtt.FormatLabel == "WEBP" && svgAtt.FormatLabel == "SVG" && qoiAtt.FormatLabel == "QOI" && hdrAtt.FormatLabel == "HDR",
                "ReadAttachmentAsync inspects dimensions and format labels for diverse formats");

            var prompt = await files.PrepareAsync("analyze", [webpAtt, svgAtt, qoiAtt, hdrAtt], default);
            Check(prompt.Images.Count == 4 &&
                  prompt.Images[0].MimeType == "image/webp" &&
                  prompt.Images[1].MimeType == "image/png" &&
                  prompt.Images[2].MimeType == "image/png" &&
                  prompt.Images[3].MimeType == "image/png" &&
                  ProjectImage.Matches(Convert.FromBase64String(prompt.Images[1].Data), "image/png") &&
                  ProjectImage.Matches(Convert.FromBase64String(prompt.Images[2].Data), "image/png") &&
                  ProjectImage.Matches(Convert.FromBase64String(prompt.Images[3].Data), "image/png"),
                "PrepareAsync sends WebP natively and transcodes SVG/QOI/HDR to valid PNG");

            // Path traversal blocked by TryReadAllowedImage
            Check(files.TryReadAllowedImage("../secret.png") is null, "TryReadAllowedImage blocks parent directory traversal");
            Check(files.TryReadAllowedImage("https://example.com/a.png") is null, "TryReadAllowedImage never fetches remote URLs");
            Check(files.TryReadAllowedImage("diagram.svg") is { MimeType: "image/svg+xml" }, "TryReadAllowedImage resolves allowed SVG");

            // TerminalLayout rendering: user attached image, AI Markdown/HTML/data-URI + LaTeX, tool image, and SIXEL-off fallback
            var cell = new ImageCells(10, 20, 0xF2F1EE, 0x10141A);
            var pngB64 = Convert.ToBase64String(MakeSkiaBytes(SKEncodedImageFormat.Png, 12, 12, alpha: true));
            var aiText = $"""
                Here is $E=mc^2$ and local ![svg](diagram.svg)
                <img alt="qoi" src="sprite.qoi">
                Inline ![dot](data:image/png;base64,{pngB64})
                Link [docs](https://example.com/docs) stays styled next to ![inline](diagram.svg)
                | name | value |
                | --- | --- |
                | docs | https://example.com/table |
                ```csharp
                public int Rich = 1;
                ```
                ```mermaid
                flowchart LR
                  A --> B
                ```
                Remote ![web](https://example.com/pic.png)
                Traversal ![no](../secret.png)
                ```md
                ![code](diagram.svg)
                ```
                """;
            var lines = new TuiLine[]
            {
                new("user", "Inspect\n[图片 × 1]",
                    Images: [new TuiImage("photo.webp", "image/webp", webpAtt.ImageBytes, MaxRows: 8)]),
                new("assistant", aiText),
                new("tool", "capture [完成] {}\n![shot](photo.webp)",
                    new ToolCallSnapshot("capture", "{}", ToolCallStatus.Completed, Output: "![shot](photo.webp)"))
            };
            var layout = new TerminalLayout();
            var view = new TuiView(project, "s1", "vision", "就绪", lines, "", 0, 0, null,
                ExpandTools: true, ShowThinking: true, ResolveImage: files.TryReadAllowedImage);

            var withSixel = layout.Arrange(view, 90, 48, cell);
            var imageRowCount = withSixel.Rows.Count(r => r.Images is { Count: > 0 });
            Check(imageRowCount >= 6, $"expected >= 6 SIXEL rows across user/AI/tool images + LaTeX, got {imageRowCount}");
            Check(withSixel.Rows.Any(r => r.Text.Contains("https://example.com/pic.png", StringComparison.Ordinal)), "remote image keeps safe text fallback");
            Check(withSixel.Rows.Any(r => r.Text.Contains("../secret.png", StringComparison.Ordinal)), "blocked traversal keeps text fallback");
            Check(withSixel.Rows.Any(r => r.Ink == Ink.Code && r.Text.Contains("![code](diagram.svg)", StringComparison.Ordinal)), "fenced code image stays literal");
            Check(withSixel.Rows.All(r => !r.Text.Contains(pngB64, StringComparison.Ordinal)), "base64 data URI never leaks into visible rows");
            Check(withSixel.Rows.Any(r => r.Spans?.Any(s => s.Link == "https://example.com/docs") == true),
                "rich image rows preserve link/OSC8 spans");
            Check(withSixel.Rows.Any(r => r.Spans?.Any(s => s.Ink == Ink.SyntaxKeyword) == true),
                "rich image rows preserve syntax-highlight spans");
            Check(withSixel.Rows.Any(r => r.Text.Contains("flowchart LR", StringComparison.Ordinal)),
                "rich image rows preserve Mermaid projection");
            Check(withSixel.Rows.Any(r => r.Text.Contains("│ name", StringComparison.Ordinal)),
                "rich image rows preserve Markdown table projection");

            // SIXEL-off terminal: collapses data URI without leaking base64, emits zero SIXEL strips
            var noSixel = layout.Arrange(view, 90, 48, cell: null);
            Check(noSixel.Rows.All(r => r.Images is null), "SIXEL-off produces zero SIXEL strips");
            Check(noSixel.Rows.All(r => !r.Text.Contains(pngB64, StringComparison.Ordinal)) &&
                  noSixel.Rows.Any(r => r.Text.Contains("[图片：dot (image/png)]", StringComparison.Ordinal)),
                "SIXEL-off collapses data URI into clean placeholder");

            // Collapsed tool hides tool image; expanded shows it
            var collapsedTool = layout.Arrange(view with { ExpandTools = false, Lines = [lines[2]] }, 90, 20, cell);
            var expandedTool = layout.Arrange(view with { ExpandTools = true, Lines = [lines[2]] }, 90, 20, cell);
            Check(collapsedTool.Rows.All(r => r.Images is null) && expandedTool.Rows.Any(r => r.Images is { Count: > 0 }),
                "tool image only renders when tool block is expanded");
        }
        finally { root.Delete(true); }
    }

    private static async Task TestClipboardPasteAndControllerAsync()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath(),
            "fui-clip-" + Guid.NewGuid().ToString("N")));
        try
        {
            var project = root.CreateSubdirectory("project").FullName;
            var data = root.CreateSubdirectory("data").FullName;

            var pngBytes = MakeSkiaBytes(SKEncodedImageFormat.Png, 16, 12, alpha: true);
            var fromPng = ClipboardImage.TryBuildFromPng(pngBytes);
            Check(fromPng is { IsClipboard: true, MimeType: "image/png", ImageWidth: 16, ImageHeight: 12 }, "ClipboardImage builds from PNG");

            var dibBytes = MakeBmp24(6, 4)[14..];
            var fromDib = ClipboardImage.TryBuildFromDib(dibBytes);
            Check(fromDib is { IsClipboard: true, MimeType: "image/png", ImageWidth: 6, ImageHeight: 4 } &&
                  fromDib.ImageBytes is not null && ProjectImage.Matches(fromDib.ImageBytes, "image/png"),
                "ClipboardImage converts CF_DIB to valid PNG");

            var files = new ProjectFiles(project, data);
            var prompt = await files.PrepareAsync("see clip", [fromDib!], default);
            Check(prompt.Images.Count == 1 && prompt.Images[0].MimeType == "image/png" &&
                  Convert.FromBase64String(prompt.Images[0].Data).SequenceEqual(fromDib!.ImageBytes!),
                "PrepareAsync sends clipboard PNG attachment without disk re-read");

            // Controller Alt+V and /paste attach clipboard image and render thumbnail TuiImage
            var fakeSession = new FakeMediaSession(project);
            var controller = new TuiController(fakeSession, default, data);
            ClipboardImage.OverrideForTests = () => ClipboardImage.TryBuildFromPng(pngBytes);
            try
            {
                controller.Key(new('v', ConsoleKey.V, false, true, false), 20, 0); // Alt+V
                await UntilAsync(controller, v => v.Status.Contains("附件 1"));
                var viewAfterPaste = controller.View();
                Check(viewAfterPaste.Lines.Any(l => l.Kind == "info" && l.Text.Contains("clipboard-") && l.Images is { Count: 1 }),
                    "Alt+V attaches clipboard image and appends SIXEL thumbnail line");
                Check(viewAfterPaste.Draft == "", "Alt+V does not insert literal 'v' into draft");

                // /context displays attached image thumbnail
                foreach (var ch in "/context") controller.Key(new(ch, 0, false, false, false), 20, 0);
                controller.Key(new('\r', ConsoleKey.Enter, false, false, false), 20, 0);
                controller.Update();
                Check(controller.View().Lines.Last().Images is { Count: 1 }, "/context renders attached clipboard image thumbnail");

                // Submit pure clipboard image turn
                controller.Key(new('\r', ConsoleKey.Enter, false, false, false), 20, 0);
                await UntilAsync(controller, _ => fakeSession.SentImages.Count == 1);
                Check(fakeSession.SentImages[0].MimeType == "image/png" &&
                      Convert.FromBase64String(fakeSession.SentImages[0].Data).SequenceEqual(pngBytes),
                    "submitting clipboard attachment sends exact PNG to IChatAttachments");
            }
            finally
            {
                ClipboardImage.OverrideForTests = null;
                await controller.FinishAsync();
            }
        }
        finally { root.Delete(true); }
    }

    private static void TestLegacyNonBracketedPasteTerminal()
    {
        // Simulate an older terminal that ignores CSI ?2004h and sends raw keystrokes without 200~/201~.
        var parser = new PasteKeys();
        long t = 0;
        foreach (var ch in "你好 legacy\ttext")
        {
            parser.Feed(new(ch, 0, false, false, false), t);
            t += 2;
        }
        parser.FlushExpired(t + 100);
        var keys = new List<ConsoleKeyInfo>();
        while (parser.Read() is { } k) keys.Add(k);
        Check(new string(keys.Select(k => k.KeyChar).ToArray()) == "你好 legacy\ttext",
            "legacy terminal raw Chinese/ASCII paste arrives intact without 200~/201~ wrappers");

        // Raw ESC followed by normal text recovers after 50ms timeout without swallowing following text.
        parser.Feed(new('\x1b', 0, false, false, false), 500);
        parser.FlushExpired(560);
        parser.Feed(new('a', ConsoleKey.A, false, false, false), 570);
        var esc = parser.Read();
        var a = parser.Read();
        Check(esc is { Key: ConsoleKey.Escape } && a is { KeyChar: 'a' },
            "legacy terminal lone ESC flushes cleanly without eating subsequent characters");
    }

    private static async Task UntilAsync(TuiController controller, Func<TuiView, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            controller.Update();
            if (predicate(controller.View())) return;
            await Task.Delay(15);
        }
        throw new TimeoutException("condition not met");
    }

    private sealed class FakeMediaSession(string project) : ITuiSession, IChatAttachments
    {
        private readonly Channel<TuiEvent> _events = Channel.CreateUnbounded<TuiEvent>();
        public List<ChatImage> SentImages { get; } = [];
        public string Project => project;
        public string Id => "clip-test";
        public string? Model => "vision";
        public IReadOnlyList<TuiLine> History => [];
        public ChannelReader<TuiEvent> Events => _events.Reader;
        public bool CanSendImages => true;
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task SendWithImagesAsync(string text, IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default)
        {
            SentImages.AddRange(images);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] MakeSkiaBytes(SKEncodedImageFormat format, int w, int h, bool alpha)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(alpha ? SKColors.Transparent : new SKColor(30, 60, 120));
            using var paint = new SKPaint { Color = new SKColor(240, 80, 50) };
            canvas.DrawRect(w / 4f, h / 4f, w / 2f, h / 2f, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] InjectExifOrientation(byte[] jpeg, ushort orientation)
    {
        // Insert minimal APP1 Exif segment right after SOI (FF D8) with Orientation tag (0x0112).
        var tiff = new byte[26];
        tiff[0] = (byte)'I'; tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8); // IFD0 offset
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1); // 1 entry
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112); // Orientation
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3); // SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1); // count
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(22), 0); // next IFD

        var payloadLen = 2 + 6 + tiff.Length;
        var output = new byte[jpeg.Length + 2 + payloadLen];
        output[0] = 0xff; output[1] = 0xd8;
        output[2] = 0xff; output[3] = 0xe1;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)payloadLen);
        "Exif\0\0"u8.CopyTo(output.AsSpan(6));
        tiff.CopyTo(output.AsSpan(12));
        jpeg.AsSpan(2).CopyTo(output.AsSpan(12 + tiff.Length));
        return output;
    }

    private static byte[] MakeMinimalGif() =>
    [
        (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
        2, 0, 2, 0, 0x80, 0, 0,
        255, 0, 0, 0, 255, 0,
        0x2c, 0, 0, 0, 0, 2, 0, 2, 0, 0,
        2, 2, 0x44, 0x01, 0, 0x3b
    ];

    private static byte[] MakeBmp24(int w, int h)
    {
        var stride = (w * 3 + 3) & ~3;
        var pixelBytes = stride * h;
        var bmp = new byte[54 + pixelBytes];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), h);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = 54 + y * stride + x * 3;
                bmp[p] = 40; bmp[p + 1] = 160; bmp[p + 2] = 240;
            }
        return bmp;
    }

    private static byte[] MakeIcoOrCur(byte[] png, bool isCur)
    {
        var ico = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), isCur ? (ushort)2 : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
        ico[6] = 12; ico[7] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        png.CopyTo(ico.AsSpan(22));
        return ico;
    }

    private static byte[] MakeQoi(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write("qoif"u8);
        Span<byte> hdr = stackalloc byte[10];
        BinaryPrimitives.WriteInt32BigEndian(hdr, w);
        BinaryPrimitives.WriteInt32BigEndian(hdr[4..], h);
        hdr[8] = 4; hdr[9] = 0;
        ms.Write(hdr);
        ms.Write([0xff, 240, 80, 40, 255]); // pixel 0
        var remaining = w * h - 1;
        while (remaining > 0)
        {
            var run = Math.Min(62, remaining);
            ms.WriteByte((byte)(0xc0 | (run - 1)));
            remaining -= run;
        }
        ms.Write([0, 0, 0, 0, 0, 0, 0, 1]);
        return ms.ToArray();
    }

    private static byte[] MakeTga24(int w, int h)
    {
        var data = new byte[18 + w * h * 3];
        data[2] = 2; // uncompressed truecolor
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)h);
        data[16] = 24; data[17] = 0x20; // top-left
        for (var i = 0; i < w * h; i++)
        {
            var p = 18 + i * 3;
            data[p] = 50; data[p + 1] = 100; data[p + 2] = 200; // BGR
        }
        return data;
    }

    private static byte[] MakeTgaRle32(int w, int h)
    {
        using var ms = new MemoryStream();
        var hdr = new byte[18];
        hdr[2] = 10; // RLE truecolor
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(12), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(14), (ushort)h);
        hdr[16] = 32; hdr[17] = 0x28; // top-left + 8-bit alpha
        ms.Write(hdr);
        ms.WriteByte((byte)(0x80 | (w * h - 1)));
        ms.Write([30, 180, 220, 128]); // BGRA
        return ms.ToArray();
    }

    private static byte[] MakeFarbfeld(int w, int h)
    {
        var data = new byte[16 + w * h * 8];
        "farbfeld"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), w);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), h);
        for (var i = 0; i < w * h; i++)
        {
            var p = 16 + i * 8;
            data[p] = 0xaa; data[p + 2] = 0x55; data[p + 4] = 0x22; data[p + 6] = 0xff;
        }
        return data;
    }

    private static byte[] MakeHdr(int w, int h, bool rle)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n"));
        for (var y = 0; y < h; y++)
        {
            if (rle)
            {
                ms.Write([2, 2, (byte)(w >> 8), (byte)(w & 0xff)]);
                byte[] channels = [200, 120, 60, 129];
                foreach (var val in channels)
                {
                    ms.WriteByte((byte)(128 + w));
                    ms.WriteByte(val);
                }
            }
            else
            {
                for (var x = 0; x < w; x++) ms.Write([200, 120, 60, 129]);
            }
        }
        return ms.ToArray();
    }

    private static byte[] MakeMinimalTiff(int w, int h)
    {
        var pixels = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) { pixels[i * 3] = 220; pixels[i * 3 + 1] = 110; pixels[i * 3 + 2] = 55; }
        var ifdOffset = 8;
        const int entryCount = 10;
        var bpsOffset = ifdOffset + 2 + entryCount * 12 + 4;
        var pixelOffset = bpsOffset + 6;
        var data = new byte[pixelOffset + pixels.Length];
        data[0] = (byte)'I'; data[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(ifdOffset), entryCount);
        var pos = ifdOffset + 2;
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 8), value);
            pos += 12;
        }
        Entry(256, 4, 1, (uint)w); // ImageWidth
        Entry(257, 4, 1, (uint)h); // ImageLength
        Entry(258, 3, 3, (uint)bpsOffset); // BitsPerSample
        Entry(259, 3, 1, 1); // Compression = None
        Entry(262, 3, 1, 2); // Photometric = RGB
        Entry(273, 4, 1, (uint)pixelOffset); // StripOffsets
        Entry(277, 3, 1, 3); // SamplesPerPixel
        Entry(278, 4, 1, (uint)h); // RowsPerStrip
        Entry(279, 4, 1, (uint)pixels.Length); // StripByteCounts
        Entry(284, 3, 1, 1); // PlanarConfiguration = Chunky
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bpsOffset), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bpsOffset + 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bpsOffset + 4), 8);
        pixels.CopyTo(data.AsSpan(pixelOffset));
        return data;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ImageRenderTests failed: " + message);
    }
}
