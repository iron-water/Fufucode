using System;
using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Fufucode.App.Views;

namespace Fufucode.App.Features.Skins.Aero;

/// <summary>
/// 把生产标题栏（主窗 CustomCaption、浮动辅助栏、对话框 caption）接到 Aero 呈现。
///
/// 识别规则（修复 E7-AERO-CAPTION-SCOPE-1）：
///   1) 优先按真实窗口按钮类型 <see cref="CaptionAssetButton"/>.Kind（minimize/maximize/restore/close）识别；
///   2) 其次按名称 CaptionMinimize/CaptionMaximize/CaptionClose；
///   3) 再次按明确的语义类 min/max/restore/close（挂接前由调用方设置）；
///   4) 最后兼容旧自动化名 最小化/最大化/还原/关闭。
///   其它按钮（工具条动作、图标按钮、隐藏的 LegacyCaptionActions）一律不识别、不改动、也不拦截挂接——
///   生产混合顶栏因此可以挂接，普通工具按钮保持原样。
///
/// 所有权（修复 E7-AERO-THEME-OWNERSHIP-1 / E7-AERO-CLASS-OWNERSHIP-1）：
///   挂接时快照每个被识别按钮的原 Theme 与原类集合、以及标题栏自身的 aeroCaption/inactive/glass，
///   释放时按快照精确复原（Theme 恢复引用本身，不强制置 null；他人在挂接前设置的类不会被删除）。
///   未挂接成功的挂接抛出前不做任何改动；挂接中途失败按快照回滚。
///
/// 重复挂接：同一标题栏上的多个租约按引用计数共享一份状态，只有最后一个释放才复原；
/// 不同标题栏（不同窗口）各自独立。
/// 不改 Click 处理器、拖拽逻辑、AutomationName 或 CaptionAssetButton 类型。
/// </summary>
public sealed class AeroCaptionAdapter : IDisposable
{
    /// <summary>本适配器可能写入按钮的类名；释放时只在这个集合内按快照复原。</summary>
    private static readonly string[] ManagedButtonClasses =
        ["aeroCaptionButton", "min", "max", "restore", "close", "single", "captionButton"];

    /// <summary>本适配器可能写入标题栏的类名；释放时只在这个集合内按快照复原。</summary>
    private static readonly string[] ManagedCaptionClasses = ["aeroCaption", "inactive", "glass"];

    private static readonly Dictionary<Border, SharedState> Shared = new(ReferenceEqualityComparer.Instance);

    private readonly SharedState _state;
    private bool _disposed;

    private AeroCaptionAdapter(SharedState state)
    {
        _state = state;
    }

    public Border Caption => _state.Caption;

    public IReadOnlyList<Button> Buttons => _state.Buttons;

    /// <summary>当前是否有租约挂在这个标题栏上（测试与诊断用）。</summary>
    public static bool IsAttached(Border caption)
    {
        ArgumentNullException.ThrowIfNull(caption);
        return Shared.TryGetValue(caption, out var state) && state.RefCount > 0;
    }

    /// <summary>
    /// 挂接：先识别窗口按钮（未识别到的普通按钮保持原样），快照原状态，再逐项写入类与 Theme。
    /// 同一标题栏重复挂接按引用计数共享状态；任一环节抛错时按快照回滚，不留半挂层。
    /// </summary>
    public static AeroCaptionAdapter Attach(Border caption)
    {
        ArgumentNullException.ThrowIfNull(caption);

        // 重复挂接：复用已有状态，不重新识别、不重复快照。
        if (Shared.TryGetValue(caption, out var existing) && existing.RefCount > 0)
        {
            existing.RefCount++;
            return new AeroCaptionAdapter(existing);
        }

        var allButtons = new List<Button>();
        CollectButtons(caption, allButtons);
        if (allButtons.Count == 0)
            throw new InvalidOperationException("标题栏内没有按钮：Aero caption 适配器拒绝挂接空标题栏。");

        var state = new SharedState(caption, SnapshotCaptionClasses(caption), RefCount: 1);
        foreach (var button in allButtons)
        {
            var kind = Classify(button);
            if (kind is null) continue; // 普通工具按钮：不识别、不改动
            state.Bindings.Add(new ButtonBinding(button, kind, button.Theme, SnapshotClasses(button)));
        }

        try
        {
            ApplyCaptionClasses(state, addAeroCaption: true);
            foreach (var binding in state.Bindings)
            {
                var added = new HashSet<string>(StringComparer.Ordinal);
                // 生产 CaptionAssetButton 构造函数自带 captionButton 类，FufucodeTheme 的
                // `Button.captionButton` 样式模板优先级高于控件自身的 Theme，会盖掉 AeroCaptionButtonTheme 的模板。
                // 换肤期间临时移除该类（只影响外观，不影响 Click/自动化/尺寸），释放时按快照恢复。
                binding.Button.Classes.Remove("captionButton");
                AddClass(binding.Button, "aeroCaptionButton", added);
                AddClass(binding.Button, binding.Kind, added);
                binding.Theme = caption.FindResource("AeroCaptionButtonTheme") as ControlTheme
                    ?? throw new InvalidOperationException("AeroCaptionButtonTheme 未解析：caption 字典尚未挂载。");
                binding.Button.Theme = binding.Theme;
            }

            foreach (var binding in state.Bindings) state.Buttons.Add(binding.Button);
            Shared[caption] = state;
            return new AeroCaptionAdapter(state);
        }
        catch
        {
            RestoreCaptionClasses(state);
            foreach (var binding in state.Bindings)
            {
                RestoreClasses(binding);
                binding.Button.Theme = binding.OriginalTheme;
            }

            throw;
        }
    }

    /// <summary>按窗口状态同步 active/inactive、glass 与 max/restore；只改类，不碰 Kind 或 Click。</summary>
    public void SyncFrom(Window window, bool? hasBlur = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_disposed) throw new ObjectDisposedException(nameof(AeroCaptionAdapter));

        SetCaptionClass("inactive", !window.IsActive);
        if (hasBlur is { } blur) SetCaptionClass("glass", blur);

        var maximized = window is Window { WindowState: WindowState.Maximized };
        foreach (var binding in _state.Bindings)
        {
            if (binding.Kind != "max") continue;
            SetClass(binding.Button, "max", !maximized);
            SetClass(binding.Button, "restore", maximized);
        }
    }

    /// <summary>对话框等单键 caption：给 close 按钮加 single 类（两侧下圆角）。</summary>
    public void MarkSingle()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AeroCaptionAdapter));
        foreach (var binding in _state.Bindings)
            if (binding.Kind == "close")
                AddClass(binding.Button, "single", new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>幂等释放：引用计数减到 0 时按快照精确复原；未到 0 时保持 Aero 呈现。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _state.RefCount--;
        if (_state.RefCount > 0) return;

        Shared.Remove(_state.Caption);
        RestoreCaptionClasses(_state);
        foreach (var binding in _state.Bindings)
        {
            RestoreClasses(binding);
            binding.Button.Theme = binding.OriginalTheme;
        }

        _state.Buttons.Clear();
        _state.Bindings.Clear();
    }

    private static string[] SnapshotClasses(Control control) =>
        SnapshotOf(control.Classes, ManagedButtonClasses);

    private static string[] SnapshotCaptionClasses(Control caption) =>
        SnapshotOf(caption.Classes, ManagedCaptionClasses);

    private static string[] SnapshotOf(Classes classes, string[] managed)
    {
        var present = new List<string>();
        foreach (var name in managed)
            if (classes.Contains(name)) present.Add(name);
        return present.ToArray();
    }

    private static void RestoreClasses(ButtonBinding binding)
    {
        var original = binding.OriginalClasses;
        foreach (var name in ManagedButtonClasses)
        {
            var had = Array.IndexOf(original, name) >= 0;
            var has = binding.Button.Classes.Contains(name);
            if (had && !has) binding.Button.Classes.Add(name);
            else if (!had && has) binding.Button.Classes.Remove(name);
        }
    }

    private static void ApplyCaptionClasses(SharedState state, bool addAeroCaption)
    {
        if (addAeroCaption) state.Caption.Classes.Add("aeroCaption");
    }

    private static void RestoreCaptionClasses(SharedState state)
    {
        var original = state.OriginalCaptionClasses;
        foreach (var name in ManagedCaptionClasses)
        {
            var had = Array.IndexOf(original, name) >= 0;
            var has = state.Caption.Classes.Contains(name);
            if (had && !has) state.Caption.Classes.Add(name);
            else if (!had && has) state.Caption.Classes.Remove(name);
        }
    }

    private void SetCaptionClass(string name, bool value)
    {
        if (value) _state.Caption.Classes.Add(name);
        else _state.Caption.Classes.Remove(name);
    }

    private static void AddClass(Control control, string name, HashSet<string> added)
    {
        if (control.Classes.Contains(name)) return;
        control.Classes.Add(name);
        added.Add(name);
    }

    private static void SetClass(Control control, string name, bool value)
    {
        if (value) control.Classes.Add(name);
        else control.Classes.Remove(name);
    }

    /// <summary>
    /// 窗口按钮识别：真实类型 Kind 优先；其次名称；再其次明确语义类；最后兼容旧自动化名。
    /// 返回 null 表示普通按钮（不拦截、不改动）。
    /// </summary>
    private static string? Classify(Button button)
    {
        if (button is CaptionAssetButton captionButton)
        {
            var byKind = captionButton.Kind switch
            {
                "minimize" => "min",
                "maximize" or "restore" => "max",
                "close" => "close",
                _ => null,
            };
            if (byKind is not null) return byKind;
        }

        if (button.Name == "CaptionMinimize") return "min";
        if (button.Name == "CaptionMaximize") return "max";
        if (button.Name == "CaptionClose") return "close";

        if (button.Classes.Contains("min")) return "min";
        if (button.Classes.Contains("max") || button.Classes.Contains("restore")) return "max";
        if (button.Classes.Contains("close")) return "close";

        return AutomationProperties.GetName(button) switch
        {
            "最小化" => "min",
            "最大化" or "还原" => "max",
            "关闭" => "close",
            _ => null,
        };
    }

    private static void CollectButtons(Control root, List<Button> destination)
    {
        if (root is Button button)
        {
            destination.Add(button);
            return;
        }

        foreach (var child in root.GetLogicalChildren())
            if (child is Control control)
                CollectButtons(control, destination);
    }

    private sealed class SharedState
    {
        public SharedState(Border caption, string[] originalCaptionClasses, int RefCount)
        {
            Caption = caption;
            OriginalCaptionClasses = originalCaptionClasses;
            this.RefCount = RefCount;
        }

        public Border Caption { get; }

        public string[] OriginalCaptionClasses { get; }

        public int RefCount { get; set; }

        public List<ButtonBinding> Bindings { get; } = new();

        public List<Button> Buttons { get; } = new();
    }

    private sealed class ButtonBinding
    {
        public ButtonBinding(Button button, string kind, ControlTheme? originalTheme, string[] originalClasses)
        {
            Button = button;
            Kind = kind;
            OriginalTheme = originalTheme;
            OriginalClasses = originalClasses;
        }

        public Button Button { get; }

        public string Kind { get; }

        public ControlTheme? OriginalTheme { get; }

        public string[] OriginalClasses { get; }

        public ControlTheme? Theme { get; set; }
    }
}
