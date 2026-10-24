using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Media;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Fufucode.App.Infrastructure;

/// <summary>一段同色文本。Color 为 null 表示主题没有为该作用域指定颜色，沿用正文色。</summary>
public sealed record SyntaxSpan(string Text, Color? Color, bool Italic);

/// <summary>
/// 逐行语法高亮，服务于变更面板的 diff 视图。
///
/// 只做"一行文本 → 若干同色片段"，不产出控件：diff 的每一行要自带底色、行号和词级强调，
/// 复用整块代码视图（<see cref="Fufucode.App.Views.HighlightedCodeView"/>）反而拆不开。
///
/// 引擎是 LiveMarkdown 已经带进来的 TextMateSharp，不需要新依赖；语法按扩展名解析，
/// 未收录的扩展名退化为单色。着色线程安全，调用方在后台线程算好再交给 UI。
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>超过这个行数就不着色：整块 re-tokenize 的成本会盖过阅读收益。</summary>
    public const int MaximumHighlightLines = 4000;

    private static readonly object Gate = new();
    private static readonly Dictionary<ThemeName, Registry> Registries = new();
    private static readonly Dictionary<string, IGrammar?> Grammars = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ScopeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string Scope, ThemeName Theme), Color?[]> Palettes = new();
    private static readonly Regex ExtensionPattern = new(@"^[A-Za-z0-9_+-]+$", RegexOptions.Compiled);
    /// <summary>
    /// TextMateSharp 把规则颜色放在非公开字段里（<c>ThemeTrieElementRule.foreground</c>），
    /// 没有公开读取入口；这里只读地探一次字段，拿不到就整体退化为单色。
    /// </summary>
    private static readonly FieldInfo? ForegroundField =
        typeof(ThemeTrieElementRule).GetField("foreground", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// 对整段文本逐行着色。返回的数组与行一一对应；无法着色时返回 null，
    /// 调用方按单色渲染即可，不要把它当成错误。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SyntaxSpan>>? Highlight(string text, string? extension, bool dark)
    {
        if (text.Length == 0 || extension is null) return null;
        var scope = ResolveScope(extension);
        if (scope is null) return null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > MaximumHighlightLines) return null;

        var theme = dark ? ThemeName.DarkPlus : ThemeName.LightPlus;
        try
        {
            lock (Gate)
            {
                var registry = RegistryFor(theme);
                if (!Grammars.TryGetValue(scope, out var grammar))
                    Grammars[scope] = grammar = Load(registry, scope);
                if (grammar is null) return null;
                return Tokenize(grammar, registry, theme, lines);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // 语法包缺失或 grammar 自身抛错时整体退化为单色；不影响 diff 内容本身。
            return null;
        }
    }

    private static Registry RegistryFor(ThemeName theme)
    {
        if (Registries.TryGetValue(theme, out var registry)) return registry;
        registry = new Registry(new RegistryOptions(theme));
        Registries[theme] = registry;
        return registry;
    }

    private static IGrammar? Load(Registry registry, string scope)
    {
        try { return registry.LoadGrammar(scope); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// 逐行 tokenize 并复用上一行的状态栈，跨行字符串、块注释才不会断色。
    /// 主题给的是调色板索引，按 scope 深度从深到浅取第一个有颜色的规则，与 TextMate 的层叠语义一致。
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<SyntaxSpan>> Tokenize(
        IGrammar grammar, Registry registry, ThemeName theme, string[] lines)
    {
        var palette = Palette(registry, theme);
        var result = new List<IReadOnlyList<SyntaxSpan>>(lines.Length);
        IStateStack? stack = null;
        foreach (var line in lines)
        {
            if (line.Length == 0) { result.Add([]); continue; }
            var state = grammar.TokenizeLine(new LineText(line), stack, TimeSpan.FromSeconds(5));
            stack = state.RuleStack;

            var spans = new List<SyntaxSpan>(state.Tokens.Length);
            var cursor = 0;
            foreach (var token in state.Tokens)
            {
                // TokenizeLine 覆盖整行；防御性处理程序不返回的尾部与越界。
                if (token.StartIndex > cursor && token.StartIndex <= line.Length)
                    spans.Add(new SyntaxSpan(line[cursor..token.StartIndex], null, false));
                var start = Math.Clamp(token.StartIndex, 0, line.Length);
                var end = Math.Clamp(token.EndIndex, start, line.Length);
                if (end > start)
                    spans.Add(new SyntaxSpan(line[start..end], ColorOf(registry, theme, palette, token.Scopes), false));
                cursor = Math.Max(cursor, end);
            }
            if (cursor < line.Length) spans.Add(new SyntaxSpan(line[cursor..], null, false));
            result.Add(spans);
        }
        return result;
    }

    private static Color? ColorOf(Registry registry, ThemeName theme, Color?[] palette, List<string> scopes)
    {
        if (ForegroundField is null) return null;
        var rules = registry.GetTheme().Match(scopes);
        if (rules is null) return null;
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            if (ForegroundField.GetValue(rules[i]) is not int index) continue;
            if (index >= 0 && index < palette.Length && palette[index] is { } color) return color;
        }
        return null;
    }

    /// <summary>
    /// 把主题调色板下标解析成颜色，按主题缓存一次。
    ///
    /// 下标必须以 <see cref="Theme.GetColor"/> 为准：规则的 foreground 是 1 基的，
    /// 直接索引 <c>GetColorMap()</c> 会整体错位一格（关键字会被涂成字符串的红色）。
    /// </summary>
    private static Color?[] Palette(Registry registry, ThemeName theme)
    {
        var key = ("default", theme);
        if (Palettes.TryGetValue(key, out var cached)) return cached;
        var themeObject = registry.GetTheme();
        var count = registry.GetColorMap().Count + 1;
        var colors = new Color?[count];
        for (var i = 0; i < count; i++)
        {
            var text = themeObject.GetColor(i);
            colors[i] = !string.IsNullOrEmpty(text) && Color.TryParse(text, out var color) ? color : null;
        }
        Palettes[key] = colors;
        return colors;
    }

    /// <summary>扩展名 → TextMate 作用域名；不在语法包里的扩展名返回 null（单色渲染）。</summary>
    private static string? ResolveScope(string extension)
    {
        var normalized = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (normalized.Length == 0 || !ExtensionPattern.IsMatch(normalized)) return null;
        if (ScopeCache.TryGetValue(normalized, out var known)) return known.Length == 0 ? null : known;

        string scope;
        lock (Gate)
        {
            try { scope = new RegistryOptions(ThemeName.DarkPlus).GetScopeByExtension("." + normalized) ?? ""; }
            catch (Exception error) when (error is not OutOfMemoryException) { scope = ""; }
            // 少数语法包自带的映射并不完整（Kotlin、TOML 等），按常见别名兜底。
            if (scope.Length == 0) scope = Alias(normalized) ?? "";
            ScopeCache[normalized] = scope;
        }
        return scope.Length == 0 ? null : scope;
    }

    private static string? Alias(string extension) => extension switch
    {
        "kt" or "kts" => "source.kotlin",
        "toml" => "source.toml",
        "mermaid" or "mmd" => "source.mermaid",
        "axaml" => "text.xml",
        "csproj" or "props" or "targets" => "text.xml",
        "mjs" or "cjs" => "source.js",
        "hcl" or "tf" => "source.hcl",
        "vue" => "text.html.vue",
        _ => null,
    };

    /// <summary>可着色的扩展名集合，供视图决定是否需要等待高亮。</summary>
    public static bool Supports(string? extension) =>
        extension is not null && ResolveScope(extension) is not null;
}
