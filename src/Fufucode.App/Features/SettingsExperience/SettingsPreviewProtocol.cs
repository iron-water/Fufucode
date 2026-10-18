using System.Collections.Generic;

namespace Fufucode.App.Features.SettingsExperience;

/// <summary>
/// 「文件预览」页签的内嵌预览能力协议（D3 就绪件）。
/// 只声明协议与状态，不接线具体预览实现——外壳接线由主控 M 统一收编。
/// </summary>
public sealed record SettingsPreviewCapability(
    string PageId,
    string Title,
    IReadOnlyList<string> AcceptedKinds,
    string EntryPoint,
    bool Wired,
    string Status);

public static class SettingsPreviewProtocol
{
    /// <summary>与 <c>WorkbenchLayoutState.KnownPages</c> 中的预览页 id 对齐。</summary>
    public const string PreviewPageId = "preview";

    public static SettingsPreviewCapability Current { get; } = new(
        PreviewPageId,
        "文件预览",
        ["text", "markdown", "image", "diff"],
        "工作面板（文件/变更/产物直开）",
        Wired: true,
        Status: "内嵌就绪：已收归工作面板默认呈现能力，支持文件、变更与聊天产物原位预览并返回列表");

    public static string Summary => $"{Current.Title}（{Current.PageId}）：{Current.Status}";
}
