using System;
using System.Collections.Generic;

namespace Fufucode.App.Features.FilePreview;

public sealed record FilePreviewRequest(string ProjectRoot, string Path)
{
    public string FullPath => System.IO.Path.GetFullPath(Path);
}

public sealed record FilePreviewTreeNode(string Label, string? Value = null,
    IReadOnlyList<FilePreviewTreeNode>? Children = null)
{
    public bool HasChildren => Children is { Count: > 0 };
    public string DisplayValue => string.IsNullOrEmpty(Value) ? Label : $"{Label}: {Value}";
    public override string ToString() => DisplayValue;
}

public sealed record FilePreviewTableRow(IReadOnlyList<string> Cells);

public sealed record FilePreviewResult(
    FilePreviewKind Kind,
    string Path,
    long Size,
    string Format,
    string? Encoding,
    bool IsTruncated,
    string? Error,
    string? Text = null,
    IReadOnlyList<FilePreviewTreeNode>? Tree = null,
    IReadOnlyList<string>? Headers = null,
    IReadOnlyList<FilePreviewTableRow>? Rows = null,
    byte[]? Bytes = null)
{
    public bool IsError => !string.IsNullOrWhiteSpace(Error) || Kind == FilePreviewKind.Error;
    public string DisplayType => Kind switch
    {
        FilePreviewKind.Markdown => "Markdown",
        FilePreviewKind.Mermaid => "Mermaid",
        FilePreviewKind.Structured => "结构化数据",
        FilePreviewKind.Table => "表格",
        FilePreviewKind.Binary => "二进制",
        FilePreviewKind.Image => "图片",
        FilePreviewKind.Text => "文本/代码",
        _ => "无法预览",
    };

    public static FilePreviewResult Failure(string path, string error, FilePreviewKind kind = FilePreviewKind.Error) =>
        new(kind, path, 0, "失败", null, false, error);
}
