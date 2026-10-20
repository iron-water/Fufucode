using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Fufucode.App.Views;

/// <summary>
/// 全屏图片预览：黑底、等比缩放，点任意处或按 Esc 关闭。
/// 消息图片与"工具查看了图片"共用同一入口，行为保持一致。
/// </summary>
internal static class ImageLightbox
{
    public static void Show(Visual anchor, IImage source)
    {
        var window = new Window
        {
            Background = Brushes.Black,
            Width = 900,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(16),
            },
        };

        if (TopLevel.GetTopLevel(anchor) is Window owner) window.Show(owner);
        else window.Show();

        window.PointerPressed += (_, _) => window.Close();
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) window.Close(); };
    }
}
