using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Fufucode.App.Features.Skins;

/// <summary>
/// 单套皮肤的呈现文案：<see cref="Summary"/> 是允许内联在页面上的一行短标签，
/// <see cref="HelpText"/> 是较长说明，只允许出现在 ToolTip / 悬浮气泡 / 折叠小帮助按钮里。
/// </summary>
public sealed record SkinCopy(SkinId Id, string Label, string Summary, string HelpText);

/// <summary>
/// 皮肤文案收敛入口：把“到处堆砌的长篇文字说明”压成 短标签 + 帮助气泡 两种角色。
/// 设置页/皮肤选择器请只内联 <see cref="SkinCopy.Summary"/>（或 <see cref="SkinCopy.Label"/>），
/// 长说明交给 <see cref="CreateHelpButton"/> 的 ToolTip 与自动化帮助文本，页面保持留白。
/// </summary>
public static class SkinPresentation
{
    /// <summary>内联短标签的字符预算；超过即视为“堆砌文字”，应由测试拦下。</summary>
    public const int InlineSummaryBudget = 20;

    /// <summary>全部皮肤的呈现文案，顺序与 <see cref="SkinCatalog.All"/> 一致。</summary>
    public static IReadOnlyList<SkinCopy> All { get; } =
        SkinCatalog.All
            .Select(definition => new SkinCopy(definition.Id, definition.DisplayName, definition.Summary, definition.Description))
            .ToArray();

    public static SkinCopy Copy(SkinId id)
    {
        var definition = SkinCatalog.Get(id);
        return new SkinCopy(definition.Id, definition.DisplayName, definition.Summary, definition.Description);
    }

    /// <summary>内联短标签（不含长说明）。</summary>
    public static string InlineSummary(SkinId id) => SkinCatalog.Get(id).Summary;

    /// <summary>长说明只走 ToolTip / 自动化帮助文本，不内联。</summary>
    public static string HelpText(SkinId id) => SkinCatalog.Get(id).Description;

    /// <summary>
    /// 折叠小帮助按钮：按钮自身只显示一个短标记，长说明挂在 ToolTip 与 HelpText 上，
    /// 键盘可聚焦、屏幕阅读器可读，且不向页面追加任何可见长文本。
    /// </summary>
    public static Button CreateHelpButton(SkinId id, double size = 22)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var copy = Copy(id);
        var help = new Button
        {
            Content = "?",
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            Padding = new Thickness(0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(size / 2),
        };
        help.Classes.Add("settingsHelp");
        help.Classes.Add("skinHelp");
        ToolTip.SetTip(help, copy.HelpText);
        AutomationProperties.SetHelpText(help, copy.HelpText);
        AutomationProperties.SetName(help, "关于" + copy.Label + "皮肤");
        return help;
    }
}
