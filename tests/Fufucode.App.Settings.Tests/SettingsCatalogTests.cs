using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Fufucode.App.Features.SettingsExperience;

namespace Fufucode.App.Settings.Tests;

internal static class SettingsCatalogTests
{
    public static void Run(Action<bool, string> check)
    {
        var items = SettingsNavCatalog.All;
        check(items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == items.Count,
            "设置导航 id 唯一");
        check(items.All(item => !string.IsNullOrWhiteSpace(item.Title) &&
                                !string.IsNullOrWhiteSpace(item.SearchTerms) &&
                                !string.IsNullOrWhiteSpace(item.Icon)),
            "设置导航条目具备标题、搜索词和图标");
        check(SettingsNavCatalog.Find(SettingsNavCatalog.DefaultCategoryId) is not null,
            "默认设置分类可以解析");
        check(items.All(item => SettingsNavCatalog.TierOrder.Contains(item.Tier)),
            "每个设置条目属于有效层级");

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fufucode.sln"))) root = root.Parent;
        var icons = Path.Combine(root?.FullName ?? throw new InvalidOperationException("找不到仓库根"),
            "src", "Fufucode.App", "Styles", "Icons.axaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = XDocument.Load(icons).Descendants().Attributes(xaml + "Key")
            .Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        var missing = items.Where(item => !keys.Contains("Icon." + item.Icon)).Select(item => item.Id).ToArray();
        check(missing.Length == 0, "设置导航图标均存在：" + string.Join(", ", missing));
    }
}
