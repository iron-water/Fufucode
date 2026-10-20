using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Fufucode.App.Features.Skins;

namespace Fufucode.App.Views;

/// <summary>Owned windows are separate resource scopes. Explicitly inherit the owner's skin
/// while keeping confirmation text, commands and cancellation behavior unchanged.</summary>
internal static class SkinDialog
{
    internal static async Task<T> ShowAsync<T>(Window dialog, Window owner)
    {
        var skin = owner.TryFindResource("AppSkinId", owner.ActualThemeVariant, out var value) && value is SkinId id ? id : SkinId.Fluent;
        foreach (var resource in owner.Resources) dialog.Resources[resource.Key] = resource.Value;
        dialog.FontFamily = owner.FontFamily;
        using var lease = SkinLease.Apply(dialog, SkinCatalog.Resolve(SkinCatalog.Get(skin).StorageKey), owner.RequestedThemeVariant);
        // 代码构建的 Window 默认没有主题底色，深色下会显示成纯黑。
        // 所有经这里展示的对话框统一使用应用对话框底色；显式设过背景的保持原样。
        if (!dialog.IsSet(Window.BackgroundProperty))
            dialog.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppDialogBackground"));
        if (skin != SkinId.Fluent)
        {
            dialog.WindowDecorations = WindowDecorations.BorderOnly;
            var title = new Border
            {
                Background = Brushes.Transparent,
                Child = new TextBlock { Text = dialog.Title, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis }
            };
            title.PointerPressed += (_, e) => { if(e.GetCurrentPoint(dialog).Properties.IsLeftButtonPressed) dialog.BeginMoveDrag(e); };
            var close = new CaptionAssetButton { Kind = "close", UseSkinGlyph = true };
            Avalonia.Automation.AutomationProperties.SetName(close, "关闭" + dialog.Title);
            close.Click += (_, _) => dialog.Close();
            var captionGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 32 };
            captionGrid.Children.Add(title); Grid.SetColumn(close, 1); captionGrid.Children.Add(close);
            var bar = new Border { Child = captionGrid, Height = 32 }; bar.Classes.Add("chromeBar");
            var content = dialog.Content;
            dialog.Content = null;
            var body = new ContentControl { Content = content };
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            root.Children.Add(bar); Grid.SetRow(body, 1); root.Children.Add(body);
            dialog.Content = root;
            lease.AttachCaption(bar);
            if (dialog.SizeToContent == SizeToContent.Manual && !double.IsNaN(dialog.Height)) dialog.Height += 32;
        }
        return await dialog.ShowDialog<T>(owner);
    }

    internal static async Task ShowAsync(Window dialog, Window owner) => await ShowAsync<object?>(dialog, owner);
}
