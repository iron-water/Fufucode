using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Views;

/// <summary>
/// 把插件贡献的富内容控件嵌进消息流。
///
/// 宿主只负责三件事：把原文交给插件、提供"提交/填入"两个回调、插件缺失时降级显示原文。
/// 控件内部怎么画由插件决定，宿主不感知具体组件类型。
/// </summary>
public sealed class PluginContentView : ContentControl
{
    private string _renderedLanguage = string.Empty;
    private string _renderedPayload = string.Empty;

    public static readonly StyledProperty<string> LanguageProperty =
        AvaloniaProperty.Register<PluginContentView, string>(nameof(Language), string.Empty);

    public static readonly StyledProperty<string> PayloadProperty =
        AvaloniaProperty.Register<PluginContentView, string>(nameof(Payload), string.Empty);

    public string Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public string Payload
    {
        get => GetValue(PayloadProperty);
        set => SetValue(PayloadProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LanguageProperty || change.Property == PayloadProperty) Render();
    }

    private void Render()
    {
        var language = Language ?? string.Empty;
        var payload = Payload ?? string.Empty;
        if (language == _renderedLanguage && payload == _renderedPayload) return;
        _renderedLanguage = language;
        _renderedPayload = payload;
        if (language.Length == 0) return;

        var context = new PluginContentContext(
            language,
            payload,
            text => PluginContentBridge.SubmitAsync(text),
            text => PluginContentBridge.InsertDraft(text));

        Content = PluginContentRegistry.CreateView(language, context) ?? Fallback(payload);
    }

    /// <summary>插件未安装或创建失败：原文照常显示，便于复制与排错。</summary>
    private static Control Fallback(string payload) => new SelectableTextBlock
    {
        Text = payload,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
        FontSize = 12,
        Opacity = 0.65,
    };
}
