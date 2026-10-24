using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Fufucode.App.Features.Plugins;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 本地插件控件的测试宿主。产品扩展页已不再承载该控件，自测自行放进可滚动的真实窗口，
/// 以保留绑定、可用性与窄窗可达性覆盖。
/// </summary>
internal sealed class LocalPluginTestSurface : IDisposable
{
    public LocalPluginTestSurface(LocalPluginViewModel viewModel, double width = 1100, double height = 800)
    {
        Section = new LocalPluginSectionView { MinHeight = 540 };
        Section.Attach(viewModel);
        Viewport = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                MaxWidth = 880,
                Margin = new Thickness(24, 16, 24, 32),
                Spacing = 16,
                Children = { Section },
            },
        };
        Window = new Window { Width = width, Height = height, Content = Viewport };
        Window.Show();
    }

    public Window Window { get; }

    public ScrollViewer Viewport { get; }

    public LocalPluginSectionView Section { get; }

    public void Dispose() => Window.Close();
}
