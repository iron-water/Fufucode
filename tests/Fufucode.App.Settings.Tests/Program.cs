using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fufucode.App.Features.SettingsExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

// S2-SETTINGS 定向测试（独立入口，未注册全量自测）：
//   dotnet run --project tests/Fufucode.App.Settings.Tests
// 覆盖：三级架构分类与层级解析、小功能升级/大功能降级的持久化存取与恢复、
//       相关设置跳转命令流转、跨层级搜索过滤。
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}

var root = Path.Combine(Path.GetTempPath(), "fufucode-settings-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    // ================= 1. 三级架构分类与层级正确解析 =================
    var basic = Ids(SettingsNavTier.Basic);
    var features = Ids(SettingsNavTier.Features);
    var other = Ids(SettingsNavTier.Other);
    Check(basic.SequenceEqual(new[] { "appearance", "about", "network", "shortcuts", "models", "storage", "memory" }),
        "基础类包含外观/关于/网络/键盘/模型/储存");
    // 插件市场（MKT 轨道）在目录中已是大功能一级条目，此处按实际目录核对，不再沿用旧四条目断言。
    Check(features.SequenceEqual(new[] { "marketplace", "browser", "terminal", "files", "automation" }),
        "大功能包含插件市场/浏览器/终端/文件管理/自动开发");
    // 性能监控（2026-10-19）在“其他”中加入 performance 条目。
    Check(other.SequenceEqual(new[] { "notifications", "background", "config", "performance", "modules", "functions" }),
        "其他包含通知/配置/用量/辅助工具等二级小功能");
    Check(SettingsNavCatalog.DefaultCategoryId == "appearance", "默认选中外观分类");
    Check(SettingsNavCatalog.Find("browser") is { Title: "浏览器", Icon: "Globe" },
        "浏览器分类的标题与图标契约保持不变");

    Check(SettingsNavCatalog.TierOrder.SequenceEqual(new[]
          { SettingsNavTier.Basic, SettingsNavTier.Features, SettingsNavTier.Other }),
        "一级分类顺序固定为基础类/大功能/其他");
    Check(basic.Concat(features).Concat(other).Count() == SettingsNavCatalog.All.Count,
        "每个目录条目恰属于一个原始层级");
    Check(typeof(SettingsNavDefinition).GetProperty("HasContent") is null,
        "目录不再声明“未接线占位”分类");

    // ================= 2. 升降级偏好持久化存取与恢复 =================
    Check(!SettingsNavPreference.CanMove("appearance"), "基础类条目不可升降级");
    Check(SettingsNavPreference.CanMove("notifications") && SettingsNavPreference.CanMove("browser"),
        "其他/大功能条目可升降级");

    var promoted = SettingsNavPreference.Empty.Toggle("notifications");
    Check(promoted.Promoted.SequenceEqual(new[] { "notifications" }) && promoted.Demoted.Count == 0,
        "小功能升级写入 Promoted");
    Check(promoted.EffectiveTier("notifications") == SettingsNavTier.Features, "升级后生效层级为大功能");

    var demoted = SettingsNavPreference.Empty.Toggle("browser");
    Check(demoted.Demoted.SequenceEqual(new[] { "browser" }) && demoted.Promoted.Count == 0,
        "大功能降级写入 Demoted");
    Check(demoted.EffectiveTier("browser") == SettingsNavTier.Other, "降级后生效层级为其他");
    Check(SettingsNavPreference.Empty.Toggle("appearance") == SettingsNavPreference.Empty,
        "基础类升降级是空操作");

    var illegal = new SettingsNavPreference
    {
        Promoted = ["browser", "nope", "notifications", "notifications"],
        Demoted = ["notifications", "browser", "browser"],
    }.Normalize();
    Check(illegal.Promoted.SequenceEqual(new[] { "notifications" }) && illegal.Demoted.SequenceEqual(new[] { "browser" }),
        "规范化丢弃跨层级与重复/未知条目");

    var storeRoot = Path.Combine(root, "store");
    var store = new SettingsNavPreferenceStore(storeRoot);
    Check(store.IsPersistent && store.Current == SettingsNavPreference.Empty, "首次加载为空偏好");
    store.Save(new SettingsNavPreference { Promoted = ["notifications", "performance"], Demoted = ["browser"] });
    var preferenceFile = Path.Combine(storeRoot, SettingsNavPreferenceStore.FileName);
    Check(File.Exists(preferenceFile), "偏好写入 settings-nav.json");
    Check(File.ReadAllText(preferenceFile).Contains("notifications", StringComparison.Ordinal),
        "落盘内容包含升级条目");
    var reloaded = new SettingsNavPreferenceStore(storeRoot);
    Check(reloaded.Current.Promoted.SequenceEqual(new[] { "notifications", "performance" }) &&
          reloaded.Current.Demoted.SequenceEqual(new[] { "browser" }),
        "重新加载恢复升降级偏好");

    Check(SettingsNavCatalog.All.Where(d => reloaded.Current.EffectiveTier(d.Id) == SettingsNavTier.Features).Select(i => i.Id)
              .SequenceEqual(new[] { "marketplace", "terminal", "files", "automation", "notifications", "performance" }),
        "升级的小功能并入大功能组");
    Check(SettingsNavCatalog.All.Where(d => reloaded.Current.EffectiveTier(d.Id) == SettingsNavTier.Other).Select(i => i.Id)
              .SequenceEqual(new[] { "browser", "background", "config", "modules", "functions" }),
        "降级的大功能收纳进其他组");

    // VM 层：命令写盘后新实例恢复同一层级。
    // 结构：无搜索时侧栏只列一级条目；「其他」二级功能在 OtherHubItems 里，
    // 升级到一级侧栏后才进 Categories；降级回 Other 后从 Categories 消失。
    var vmRoot = Path.Combine(root, "vm");
    var vm = new SettingsPageViewModel();
    vm.AttachNavPreference(vmRoot);
    var notifications = vm.OtherHubItems.Single(c => c.Id == "notifications");
    Check(notifications.Tier == SettingsNavTier.Other && notifications.CanMoveTier &&
          notifications.TierActionToFeatures, "VM 侧其他条目默认可升级");
    vm.ToggleNavTierCommand.Execute(notifications);
    Check(vm.Categories.Single(c => c.Id == "notifications").Tier == SettingsNavTier.Features,
        "升级命令立即把条目移入大功能");
    Check(!vm.OtherHubItems.Any(c => c.Id == "notifications"), "升级后不再列在「其他」里");
    Check(File.Exists(Path.Combine(vmRoot, SettingsNavPreferenceStore.FileName)), "升级命令落盘偏好");
    var restored = new SettingsPageViewModel();
    restored.AttachNavPreference(vmRoot);
    Check(restored.Categories.Single(c => c.Id == "notifications").Tier == SettingsNavTier.Features,
        "新实例从配置恢复升级偏好");
    restored.ToggleNavTierCommand.Execute(restored.Categories.Single(c => c.Id == "notifications"));
    Check(!restored.Categories.Any(c => c.Id == "notifications") &&
          restored.OtherHubItems.Single(c => c.Id == "notifications").Tier == SettingsNavTier.Other,
        "再次触发收纳回「其他」");

    var demoteVm = new SettingsPageViewModel();
    demoteVm.AttachNavPreference(Path.Combine(root, "vm-demote"));
    demoteVm.ToggleNavTierCommand.Execute(demoteVm.Categories.Single(c => c.Id == "automation"));
    Check(!demoteVm.Categories.Any(c => c.Id == "automation") &&
          demoteVm.OtherHubItems.Single(c => c.Id == "automation").Tier == SettingsNavTier.Other,
        "大功能降级命令把条目收纳进其他");
    demoteVm.ToggleNavTierCommand.Execute(demoteVm.Categories.Single(c => c.Id == "appearance"));
    Check(demoteVm.Categories.Single(c => c.Id == "appearance").Tier == SettingsNavTier.Basic,
        "基础类条目忽略升降级命令");

    // ================= 3. 相关设置跳转命令流转 =================
    Check(SettingsRelatedSettings.For(null).Count == 0, "未知页面没有相关设置");
    var appearanceLinks = SettingsRelatedSettings.For("appearance");
    Check(appearanceLinks.Count > 0 && appearanceLinks.Any(l => l.Id == "functions"),
        "外观页列出会话与消息作为相关设置");
    Check(SettingsRelatedSettings.For("functions").Any(l => l.Id == "appearance"),
        "相关设置关系是双向的");
    Check(SettingsNavCatalog.All.All(definition =>
              SettingsRelatedSettings.For(definition.Id).All(link => SettingsNavCatalog.Find(link.Id) is not null)),
        "所有相关设置目标都能解析到目录条目");

    var nav = new SettingsPageViewModel();
    nav.AttachNavPreference(Path.Combine(root, "nav"));
    nav.SelectCategory("appearance");
    Check(nav.SelectedCategory?.Id == "appearance" && nav.HasRelatedSettings &&
          nav.RelatedLinks.Select(l => l.Id).SequenceEqual(appearanceLinks.Select(l => l.Id)),
        "VM 的相关设置卡片跟随当前分类");
    nav.OpenRelatedCommand.Execute(nav.RelatedLinks.Single(l => l.Id == "modules"));
    Check(nav.SelectedCategory?.Id == "modules" && nav.CurrentPageId.Length == 0,
        "相关设置跳转到目标分类首页");
    nav.OpenRelatedCommand.Execute(new SettingsRelatedLink("appearance.arrange", "前端功能排布", "子页"));
    Check(nav.SelectedCategory?.Id == "appearance" && nav.CurrentPageId == "appearance.arrange",
        "相关设置可下钻到二级页");
    nav.SearchText = "代理";
    nav.OpenRelatedCommand.Execute(new SettingsRelatedLink("about", "关于", "跨搜索跳转"));
    Check(nav.SearchText.Length == 0 && nav.SelectedCategory?.Id == "about",
        "搜索状态下跳转会先清空关键词保证目标可见");

    // ================= 4. 跨层级搜索过滤 =================
    static string[] Matches(string term) => SettingsNavCatalog.All
        .Where(item => SettingsNavLayout.Matches(item, term)).Select(item => item.Id).ToArray();
    Check(Matches("代理").SequenceEqual(new[] { "network" }),
        "搜索命中基础类条目");
    Check(Matches("shell").SequenceEqual(new[] { "terminal" }), "搜索命中大功能条目");
    Check(Matches("trace").SequenceEqual(new[] { "performance" }), "搜索命中其他类小功能");
    Check(Matches("不存在的关键词").Length == 0, "无命中时不输出任何条目");

    var searchVm = new SettingsPageViewModel();
    searchVm.AttachNavPreference(Path.Combine(root, "search"));
    // 无搜索：一级侧栏只列基础类 + 大功能 + 「其他」占位；二级功能不平铺。
    Check(searchVm.FilteredCategories.Any(c => c.Id == SettingsPageViewModel.OtherHubId),
        "无搜索时侧栏有「其他」占位项");
    Check(!searchVm.FilteredCategories.Any(c => c.Id == "notifications"),
        "无搜索时「其他」的二级功能不进侧栏");
    Check(searchVm.OtherHubItems.Any(c => c.Id == "notifications"),
        "「其他」内容区列出二级功能入口");
    Check(searchVm.FilteredCategories.Count ==
          SettingsNavCatalog.All.Count(d => SettingsNavPreference.Empty.EffectiveTier(d.Id) != SettingsNavTier.Other) + 1,
        "侧栏总数 = 一级条目 + 占位项");
    Check(searchVm.Categories.Any(c => c.Id == "appearance") &&
          searchVm.OtherHubItems.Any(c => c.Id == "functions"),
        "侧栏一级包含外观，「其他」包含会话与消息");
    searchVm.SelectCategory("terminal");
    searchVm.NavigateTo("marketplace");
    Check(searchVm.SelectedCategory?.Id == "marketplace" && searchVm.CurrentPageId.Length == 0,
        "功能状态页可跳到一级分类");
    searchVm.NavigateTo("functions.floating");
    Check(searchVm.SelectedCategory?.Id == "functions" && searchVm.CurrentPageId == "functions.floating",
        "统一跳转会先选父分类再下钻子页");
    searchVm.SearchText = "通知";
    Check(searchVm.HasSearchResults && searchVm.FilteredCategories.Any(c => c.Id == "notifications") &&
          searchVm.FilteredCategories.All(c => c.Id != "appearance"),
        "跨层级搜索把 Other 条目平铺出来");
    Check(searchVm.SearchSummary.Contains("1", StringComparison.Ordinal), "搜索摘要反映命中数量");
    searchVm.SearchText = "zzz-无命中";
    Check(!searchVm.HasSearchResults && searchVm.FilteredCategories.Count == 0, "无命中时侧栏为空");
    searchVm.SearchText = string.Empty;
    Check(!searchVm.FilteredCategories.Any(c => c.Id == "notifications") &&
          searchVm.FilteredCategories.Any(c => c.Id == SettingsPageViewModel.OtherHubId),
        "清空搜索恢复收纳态");

    // ================= 4.1 显示分级与布局契约 =================
    Check(WorkbenchActionDefaults.Placement("session.rename").Position == UiActionPosition.SessionIsland &&
          WorkbenchActionDefaults.Placement("session.fork").Position == UiActionPosition.SessionIsland &&
          WorkbenchActionDefaults.Placement("session.archive").Position == UiActionPosition.MenuLeft,
        "即时会话动作由动态岛承载，归档仍留在会话管理菜单");
    Check(WorkbenchActionDefaults.Placement("status.duration").Position == UiActionPosition.StatusRight &&
          WorkbenchActionDefaults.Placement("status.branch").Position == UiActionPosition.StatusRight,
        "状态栏默认包含用时与分支指标");
    Check(WorkbenchActionDefaults.ExplicitIcon("terminal.paste") == "ClipboardPaste" &&
          WorkbenchActionDefaults.ExplicitIcon("browser.focus-address") == "Edit",
        "动作图标显式映射覆盖终端与浏览器细分动作");

    var pluginLayout = new WorkbenchLayoutState();
    pluginLayout.Pages[DockZone.Right] = ["changes", "plugin.page"];
    pluginLayout.Selected[DockZone.Right] = "plugin.page";
    var pluginLayoutText = pluginLayout.Serialize();
    Check(WorkbenchLayoutState.Restore(pluginLayoutText, ["overview", "files", "changes", "plugin.page"]).Find("plugin.page") == DockZone.Right,
        "动态插件页在已知页面集合内可恢复停靠位置");
    Check(WorkbenchAppearance.Parse(new WorkbenchAppearance { Layout = pluginLayout }.Serialize())
              .Layout!.Find("plugin.page") == DockZone.Right,
        "ui.json 解析不会丢弃插件页布局");

    var editStore = new WorkbenchAppearanceStore(Path.Combine(root, "layout-edit"));
    var editVm = new WorkbenchCustomizationViewModel(editStore);
    editVm.EditLayout(l => { l.Move("files", DockZone.Right); });
    Check(editStore.Current.Layout?.Find("files") == DockZone.Right,
        "设置页布局编辑写入真实 ui.json 状态");

    // ================= 4.2 Fluent 暗色三档底色 =================
    var appearance = new WorkbenchAppearance();
    Check(appearance.BaseBackground == "#1C1C1C" && appearance.BaseBar == "#2C2C2C" &&
          appearance.BaseHighlight == "#3A3A3A" && appearance.Emphasis == "#FFEE58",
        "Fluent 暗色默认三档底色与模型输出强调色");
    Check(WorkbenchAppearance.BaseBackgroundChoices.Count == 3 &&
          WorkbenchAppearance.BaseBarChoices.Count == 3 &&
          WorkbenchAppearance.BaseHighlightChoices.Count == 3,
        "每档底色只提供三个备选");
    Check(WorkbenchAppearance.BaseBackgroundLabels.Count == WorkbenchAppearance.BaseBackgroundChoices.Count &&
          WorkbenchAppearance.BaseBarLabels.Count == WorkbenchAppearance.BaseBarChoices.Count &&
          WorkbenchAppearance.BaseHighlightLabels.Count == WorkbenchAppearance.BaseHighlightChoices.Count,
        "底色备选与标签一一对应");
    Check(new WorkbenchAppearance { BaseBar = "#FF00FF" }.Normalize().BaseBar == "#2C2C2C",
        "非法底色回退到默认，不接受任意第四种底色");
    Check(appearance.Accent != appearance.Emphasis,
        "主题色与模型输出强调色是两个独立字段");

    // ================= 5. 设置页纪律守卫（规范 §10） =================
    // 放在能力门禁之前：门禁测试依赖本机插件目录，属于既有环境用例；
    // 纪律守卫是纯源码检查，必须每次都能独立跑完。
    Fufucode.App.Settings.Tests.SettingsStyleTests.Run(Check);

    // ================= 6. 插件能力门禁（MKT-1） =================
    passed += Fufucode.App.Settings.Tests.PluginGatingTests.Run(Check);

    // ================= 7. 设置 → 运行时页面/工具三层接线 =================
    passed += Fufucode.App.Settings.Tests.WiringIntegrationTests.Run(Check);

    Console.WriteLine($"\n全部通过：{passed} 项断言");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, true); }
    catch (IOException) { /* 临时目录清理失败不影响测试结论 */ }
}

static IReadOnlyList<string> Ids(SettingsNavTier tier) =>
    SettingsNavCatalog.All.Where(item => item.Tier == tier).Select(item => item.Id).ToArray();
