using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fufucode.App.Features.SettingsExperience;

namespace Fufucode.App.Settings.Tests;

/// <summary>
/// 设置页纪律守卫：把 docs/settings-design-spec.md 第 10 节中可机械检查的条目固化。
/// 这里刻意读取源码文本而不是渲染控件，目的是让“样式漂移”在构建期就失败。
/// </summary>
internal static class SettingsStyleTests
{
    private static readonly HashSet<string> AllowedClasses = new(StringComparer.Ordinal)
    {
        "accent", "attention", "badge", "body", "bodyStrong", "breadcrumb", "caption",
        "clickable", "compactBar", "crumbLink", "errorCard", "first", "iconButton", "info",
        "innerWell", "last", "meta", "mono", "navAccent", "navFill", "settingsContentHost",
        "settingsControlRow", "settingsGroup", "settingsGroupDesc", "settingsGroupHeader", "settingsNav",
        "settingsNavHost", "settingsPageTitle", "settingsPanel", "settingsPathRow",
        "settingsRow", "settingsRowDesc", "settingsRowTitle", "subtitle", "subtle", "toolButton",
    };

    public static void Run(Action<bool, string> check)
    {
        var root = FindRepositoryRoot();
        var settingsXamlPath = Path.Combine(root, "src", "Fufucode.App", "Views", "SettingsPage.axaml");
        var sectionsCodePath = Path.Combine(root, "src", "Fufucode.App", "Views", "WorkbenchSectionsView.cs");
        var iconsPath = Path.Combine(root, "src", "Fufucode.App", "Styles", "Icons.axaml");

        check(File.Exists(settingsXamlPath), "设置页 XAML 存在");
        var xaml = File.ReadAllText(settingsXamlPath);
        var code = File.ReadAllText(sectionsCodePath);

        // 1. 样式必须集中在 WorkbenchStyles.axaml。
        check(!xaml.Contains("<UserControl.Styles>", StringComparison.Ordinal),
            "SettingsPage.axaml 不包含 UserControl.Styles");

        // 2. 禁止裸 hex。先去掉 XML 注释，避免注释里的示例误伤。
        var withoutComments = Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        check(!Regex.IsMatch(withoutComments, @"#[0-9A-Fa-f]{6,8}"),
            "SettingsPage.axaml 不含裸 hex 颜色");

        // 3. 禁止内联圆角数值；只允许 DynamicResource。
        check(!Regex.IsMatch(withoutComments, @"CornerRadius=""\s*\d"),
            "SettingsPage.axaml 不含内联圆角数值");

        // 4. 字号阶梯：唯一允许的数值字号是侧栏 14（规范 §3 的既有例外）。
        var fontSizes = Regex.Matches(withoutComments, @"FontSize=""\s*(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        check(fontSizes.All(size => size == 14) && fontSizes.Length <= 1,
            "SettingsPage.axaml 字号只使用样式阶梯（侧栏 14 为唯一例外）");

        // 5. 所有 App*/Icon.* 资源键必须真实存在。
        var definedKeys = CollectResourceKeys(root);
        var referencedKeys = Regex.Matches(xaml, @"\{DynamicResource\s+([A-Za-z0-9_.]+)\}")
            .Select(m => m.Groups[1].Value)
            .Where(key => key.StartsWith("App", StringComparison.Ordinal) ||
                          key.StartsWith("Icon.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var missingKeys = referencedKeys.Where(key => !definedKeys.Contains(key)).ToArray();
        check(missingKeys.Length == 0,
            "SettingsPage.axaml 的 App*/Icon.* 资源键全部有定义" +
            (missingKeys.Length == 0 ? "" : "（缺失：" + string.Join(", ", missingKeys) + "）"));

        // 6. 类名白名单：新增类必须先登记到规范与样式表，防止自创平行类。
        var usedClasses = Regex.Matches(xaml, @"Classes=""([^""]+)""")
            .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unknownClasses = usedClasses.Where(name => !AllowedClasses.Contains(name)).ToArray();
        check(unknownClasses.Length == 0,
            "SettingsPage.axaml 未使用白名单之外的类" +
            (unknownClasses.Length == 0 ? "" : "（新增：" + string.Join(", ", unknownClasses) + "）"));

        // 7. C# 构建页不得留下无意义的占位语句。
        check(!code.Contains("_ = settings;", StringComparison.Ordinal) &&
              !code.Contains("_ = vm;", StringComparison.Ordinal),
            "WorkbenchSectionsView 不包含占位丢弃语句");

        // 8. 页面构建必须经 SettingsLayout，不得直接手工创建 settingsRow 卡。
        check(!code.Contains("Classes.Add(\"settingsRow\")", StringComparison.Ordinal),
            "WorkbenchSectionsView 不手工拼装 settingsRow 卡");

        // 9. 导航目录：id 唯一、搜索词与图标齐备、图标键存在。
        var ids = SettingsNavCatalog.All.Select(item => item.Id).ToArray();
        check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "设置导航 id 唯一");
        check(SettingsNavCatalog.All.All(item =>
                !string.IsNullOrWhiteSpace(item.Title) &&
                !string.IsNullOrWhiteSpace(item.SearchTerms) &&
                !string.IsNullOrWhiteSpace(item.Icon)),
            "设置导航条目标题、搜索词与图标齐备");
        var iconKeys = CollectIconKeys(iconsPath);
        var missingIcons = SettingsNavCatalog.All
            .Where(item => !iconKeys.Contains(item.Icon))
            .Select(item => item.Id + ":" + item.Icon)
            .ToArray();
        check(missingIcons.Length == 0,
            "设置导航图标键全部存在" +
            (missingIcons.Length == 0 ? "" : "（缺失：" + string.Join(", ", missingIcons) + "）"));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fufucode.sln")))
                return directory.FullName;
        }
        throw new InvalidOperationException("找不到仓库根目录（Fufucode.sln）。");
    }

    private static HashSet<string> CollectResourceKeys(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateSourceFiles(Path.Combine(root, "src", "Fufucode.App"), "*.axaml"))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
                keys.Add(match.Groups[1].Value);

        // 运行期注入的语义键（ApplyCustomization / 皮肤适配器）。
        foreach (var file in EnumerateSourceFiles(Path.Combine(root, "src", "Fufucode.App"), "*.cs"))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"Resources\[""([^""]+)""\]\s*="))
                keys.Add(match.Groups[1].Value);
        return keys;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root, string pattern)
        => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> CollectIconKeys(string iconsPath)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(iconsPath)) return keys;
        foreach (Match match in Regex.Matches(File.ReadAllText(iconsPath), @"x:Key=""Icon\.([^""]+)"""))
            keys.Add(match.Groups[1].Value);
        return keys;
    }
}
