using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

/// <summary>真实计时器下的加载骨架与页面入场：扫光确实在移动、隐藏即停，页面切换中途处于上浮淡入。</summary>
internal static class NativeSkeletonSelfTest
{
    internal static async Task<int> RunAsync(MainWindow window)
    {
        var evidence = Path.GetFullPath("tmp/skeleton-evidence");
        Directory.CreateDirectory(evidence);
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }
        await Task.Delay(1000);
        window.WindowState = WindowState.Normal;
        window.Width = 1440; window.Height = 900;
        await Task.Delay(300);
        Check(!Motion.Reduced && !MemoryProfile.DisableVisualEffects, "skeleton regression runs with real animation timers");

        var show = typeof(MainWindow).GetMethod("ShowStartup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var hide = typeof(MainWindow).GetMethod("HideStartupOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        show.Invoke(window, new object[] { "正在启动会话…", true });
        await Task.Delay(200);
        var overlay = window.FindControl<Border>("StartupOverlay")!;
        var skeleton = overlay.GetVisualDescendants().OfType<SkeletonSurface>().Single();
        var bones = skeleton.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("skeletonBone") && b.IsEffectivelyVisible);
        Check(overlay.IsVisible && skeleton.IsEffectivelyVisible && !window.FindControl<Border>("StartupCard")!.IsEffectivelyVisible &&
            window.FindControl<ProgressBar>("StartupProgress")!.IsEffectivelyVisible && bones >= 12,
            $"loading shows the workbench skeleton and top progress instead of the card ({bones} bones)");
        Check(skeleton.IsSweepingForTesting, "skeleton highlight sweep is running while visible");

        // 同一位置在不同时刻的像素应不同：扫光确实在移动，而不是静态图。
        var frames = new List<byte[]>();
        for (var i = 0; i < 4; i++)
        {
            var path = Path.Combine(evidence, $"skeleton-{i}.png");
            Save(window, path);
            frames.Add(File.ReadAllBytes(path));
            await Task.Delay(260);
        }
        Check(frames.Distinct(new BytesComparer()).Count() >= 2, "skeleton frames change over time (highlight sweep moves)");

        hide.Invoke(window, null);
        await Task.Delay(500);
        Check(!overlay.IsVisible, "overlay fades away when loading ends");
        await Task.Delay(100);
        Check(!skeleton.IsSweepingForTesting, "sweep stops once the skeleton is hidden");

        show.Invoke(window, new object[] { "打开会话视图失败：测试", false });
        await Task.Delay(200);
        Check(window.FindControl<Border>("StartupCard")!.IsEffectivelyVisible && !skeleton.IsEffectivelyVisible &&
            !window.FindControl<ProgressBar>("StartupProgress")!.IsVisible, "failure state shows the card without skeleton or progress");
        Save(window, Path.Combine(evidence, "startup-failure.png"));
        hide.Invoke(window, null);
        await Task.Delay(500);

        var host = window.FindControl<TransitioningContentControl>("PageHost")!;
        var navigation = window.NavigateForTestingAsync(AppPage.Settings);
        double minOpacity = 1;
        for (var i = 0; i < 40 && !navigation.IsCompleted; i++)
        {
            minOpacity = Math.Min(minOpacity, host.Opacity);
            await Task.Delay(10);
        }
        await navigation;
        await Task.Delay(300);
        Check(minOpacity < .9 && Math.Abs(host.Opacity - 1) < .01, $"page switch runs an entrance and settles fully visible (min {minOpacity:0.00})");
        await window.NavigateForTestingAsync(AppPage.Chat);
        await Task.Delay(400);
        Check(Math.Abs(host.Opacity - 1) < .01, "returning page settles fully visible");

        await File.WriteAllLinesAsync(Path.Combine(evidence, "failures.txt"), failures);
        Console.WriteLine($"Native skeleton checks failures: {failures.Count}; evidence: {evidence}");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }
}
