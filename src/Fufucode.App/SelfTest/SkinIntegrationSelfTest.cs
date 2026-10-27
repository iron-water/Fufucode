using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Features.Skins;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

internal static class SkinIntegrationSelfTest
{
    /// <summary>皮肤已改为插件市场提供；自测在隔离环境显式登记，验证换肤链路本身。</summary>
    private static void EnsureSkinPluginsRegistered()
    {
        foreach (var id in new[] { SkinId.Metro, SkinId.Material3, SkinId.Aero, SkinId.Luna, SkinId.LiquidGlass })
        {
            var definition = SkinCatalog.Get(id);
            Fufucode.Core.Plugins.PluginSkinRegistry.Register("selftest." + definition.StorageKey,
                new Fufucode.Core.Plugins.PluginSkinContribution(definition.StorageKey, definition.DisplayName,
                    definition.Summary, definition.Description, definition.TokenDictionaryUri, definition.ScopedStylesUri));
        }
    }

    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        EnsureSkinPluginsRegistered();
        void Check(bool ok, string name) { if (ok) pass("Skins: " + name); else fail("Skins: " + name); }
        string? StoredSkin(WorkbenchAppearance appearance) => JsonNode.Parse(appearance.Serialize())?["skin"]?.GetValue<string>();
        var root = Path.Combine(Path.GetTempPath(), "fufucode-skins-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Check(StoredSkin(WorkbenchAppearance.Parse("{\"version\":1,\"surface\":\"flat\"}")) == "fluent", "legacy material config gets independent Fluent default");
        Check(StoredSkin(WorkbenchAppearance.Parse("{\"version\":1,\"skin\":\"future-skin\"}")) == "future-skin", "unknown skin identity survives parse/serialization");
        var store = new WorkbenchAppearanceStore(Path.Combine(root, "config"));
        store.Replace("{\"version\":1,\"skin\":\"metro\",\"surface\":\"classic\",\"future\":7}", false);
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        store.AttachProject(project);
        store.Replace("{\"version\":1,\"skin\":\"material3\"}", true);
        Check(StoredSkin(store.Current) == "material3" && store.Current.Surface == "classic", "project skin inherits global material independently");
        store.Save(store.Current with {FontSize=15}, true);
        Check(JsonNode.Parse(store.ScopeJson(true))?["surface"] is null && JsonNode.Parse(store.ScopeJson(false))?["future"]?.GetValue<int>() == 7, "ordinary skin-scope edits preserve sparse overlay and unknown fields");
        store.ClearProjectOverrides();
        Check(StoredSkin(store.Current) == "metro", "clearing project override restores global skin");
        var previousRoot = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(root, "app");
        var window = new MainWindow {Width=1100, Height=800};
        window.Show();
        try
        {
            async Task Ready()
            {
                await Task.Delay(80);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            Color? Resource(Window target, string key) => target.TryFindResource(key, target.ActualThemeVariant, out var value) ? value switch { ISolidColorBrush brush => brush.Color, Color color => color, _ => null } : null;
            var c = window.CustomizationForTesting;
            void Select(string skin, string theme="light") {
                var json=JsonNode.Parse(c.Current.Serialize())!.AsObject();
                json["skin"]=skin; json["theme"]=theme;
                c.Json=json.ToJsonString(); c.ApplyJson();
            }
            await Ready();
            var baseline=Resource(window,"AppWindowBackground");
            var baseStyles=window.Styles.Count;
            var baseDictionaries=window.Resources.MergedDictionaries.Count;
            Check(!window.FindControl<Border>("CustomStatusBar")!.IsVisible, "fresh main status strip stays hidden");
            Select("metro"); await Ready();
            var chat=window.GetVisualDescendants().OfType<ChatPage>().First();
            Check(chat.FindControl<Border>("ComposerCard")!.CornerRadius==new CornerRadius(0), "Metro composer follows square skin rather than Fluent radius preference");
            await ((Fufucode.App.Mock.MockChatSession)((MainViewModel)window.DataContext!).Session).SendAsync("皮肤头像渲染夹具"); await Ready();
            var userAvatar=chat.GetVisualDescendants().OfType<Border>().First(border=>AutomationProperties.GetAutomationId(border)=="MSG-USER-AVATAR");
            var originalMessage = userAvatar.DataContext;
            Check(userAvatar.CornerRadius==new CornerRadius(0), "Metro actual message avatar uses square skin shape");
            Check(window.FindControl<CaptionAssetButton>("CaptionClose")!.UseSkinGlyph && window.FindControl<CaptionAssetButton>("CaptionClose")!.Content is PathIcon,
                "Metro caption uses vector glyph rather than Fluent state bitmap");
            var fluentGeometry=Application.Current!.FindResource("Icon.Folder");
            var metroGeometry=window.FindResource("Icon.Folder");
            Check(!ReferenceEquals(fluentGeometry,metroGeometry), "Metro scoped icon geometry differs from default Fluent");
            Check(window.TryFindResource("SystemAccentColor",window.ActualThemeVariant,out var nativeMetroAccent) && nativeMetroAccent is Color metroAccent && metroAccent==Color.Parse("#2D89EF"),
                "native control accent color follows Metro instead of directly assigned Fluent palette");
            // Main's 800px responsive layout hides the left region. Test actual input on a visible 224px region.
            window.Width=1100;
            foreach(var pageId in new[]{"files","changes","browser","library","notifications"}) window.MoveDockPage(pageId,DockZone.Left);
            await Ready();
            var dock=window.FindControl<DockRegionView>("LeftDock")!;
            var scrolling=dock.TabScrollForTesting;
            Check(scrolling.Extent.Width>scrolling.Viewport.Width, "real narrow auxiliary region has overflowing tabs");
            scrolling.Offset=new Vector(0,0);
            var wheelPoint=scrolling.TranslatePoint(new Point(20,20),window)!.Value;
            window.MouseWheel(wheelPoint,new Vector(0,-1)); await Ready();
            Check(scrolling.Offset.X>0 && scrolling.Offset.Y==0, "ordinary vertical wheel scrolls actual dock tabs horizontally");
            var beforeOffset=scrolling.Offset.X;
            dock.Render(window.DockLayoutForTesting); await Ready();
            Check(scrolling.Offset.X==beforeOffset, "unrelated dock refresh does not snap browsing back to selected tab");
            window.OpenDockPage("files",DockZone.Left); await Ready();
            var closeButton=dock.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="dock-close-files");
            // DESK-1：关闭按钮从 24 粗大笨重改为 16 轻量圆形，图标仍是 10。
            Check(closeButton.Bounds.Width==16 && closeButton.Content is PathIcon {Width:10,Height:10}, "close icon fits inside separate 16px hit area");
            var titleButton=dock.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="dock-tab-files");
            Check(Math.Abs(titleButton.Bounds.Center.Y-closeButton.Bounds.Center.Y)<1, "tab and close button share vertical center");
            var rightPoint=titleButton.TranslatePoint(new Point(20,titleButton.Bounds.Height/2),window)!.Value;
            window.MouseDown(rightPoint,MouseButton.Right); window.MouseUp(rightPoint,MouseButton.Right); await Ready();
            Check(titleButton.ContextMenu!.IsOpen && titleButton.ContextMenu.Items.OfType<MenuItem>().Any(item=>item.Tag as string=="dock-float"),
                "right click on actual tab opens page-level menu rather than region menu");
            titleButton.ContextMenu.Close(); await Ready();
            Check(closeButton.IsHitTestVisible && closeButton.Opacity==1, "selected tab close target is visible and hittable");
            window.DockLayoutForTesting.PinnedPages.Add("files"); dock.Render(window.DockLayoutForTesting); await Ready();
            Check(!closeButton.IsEnabled, "pinned tab close button stays disabled");
            window.DockLayoutForTesting.PinnedPages.Remove("files"); dock.Render(window.DockLayoutForTesting); await Ready();
            window.Width=1100; await Ready();
            Check(Resource(window,"AppWindowBackground")==Colors.White && Resource(window,"AppAccent")==Color.Parse("#2D89EF"), "main window uses Metro tokens rather than old local accent overrides");
            await window.NavigateForTestingAsync(AppPage.Settings); await Ready();
            var vm=(MainViewModel)window.DataContext!;
            vm.Settings.SelectedCategory=vm.Settings.Categories.Single(item=>item.Id=="appearance"); await Ready();
            var selector=window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(combo=>combo.ItemsSource is string[] labels && labels.SequenceEqual(new[]{"Fluent","Metro","Material 3","Aero","Luna","Liquid Glass"}));
            // WorkBuddy 皮肤已归档（手机遥控配套设计规范，非桌面皮肤），不再出现于目录。
            Check(selector is not null && selector.SelectedIndex==1, "real appearance settings exposes current skin selector");
            var row=selector!.GetVisualAncestors().OfType<Border>().First(b=>b.Classes.Contains("settingsRow"));
            var description=row.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Classes.Contains("settingsRowDesc"));
            Check(description.IsVisible && !string.IsNullOrEmpty(description.Text),
                "setting explanation is inline text (Win11): visible, secondary, single source of truth");
            var radiusRow=window.GetVisualDescendants().OfType<Border>().First(border=>border.Classes.Contains("settingsRow") && border.GetVisualDescendants().OfType<TextBlock>().Any(text=>text.Text=="圆角"));
            Check(!radiusRow.IsEnabled && !radiusRow.IsVisible, "optional skin hides ineffective Fluent-only corner setting");
            Check(window.GetVisualDescendants().OfType<SemanticIcon>().Any(icon=>icon.IsEffectivelyVisible && icon.Data is not null && icon.GetVisualChildren().Any()),
                "semantic settings navigation icons instantiate actual PathIcon template");
            vm.Settings.SearchText="皮肤"; await Ready();
            Check(vm.Settings.FilteredCategories.Any(item=>item.Id=="appearance"), "skin search reaches the appearance category");
            vm.Settings.SearchText=""; vm.Settings.SelectedCategory=vm.Settings.Categories.Single(item=>item.Id=="appearance"); await Ready();
            selector=window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(combo=>combo.ItemsSource is string[] labels && labels.SequenceEqual(new[]{"Fluent","Metro","Material 3","Aero","Luna","Liquid Glass"}));
            if(selector is not null) {selector.SelectedIndex=2; await Ready();}
            else { Select("material3"); await Ready(); }
            Check(StoredSkin(c.Current)=="material3" && Resource(window,"AppAccent")==Color.Parse("#6750A4"), "setting selection applies and persists Material 3");
            Check(window.FindControl<Border>("CustomCaption")!.BorderThickness==new Thickness(0), "Material chrome drops Fluent caption separator");
            Check(!ReferenceEquals(window.FindResource("Icon.Folder"),metroGeometry), "Material glyph resources replace Metro without rebuilding page");
            Check(window.TryFindResource("SystemAccentColor",window.ActualThemeVariant,out var nativeMaterialAccent) && nativeMaterialAccent is Color materialAccent && materialAccent==Color.Parse("#6750A4"),
                "Material native control accent uses its fixed palette");
            var navIcon=window.GetVisualDescendants().OfType<SemanticIcon>().First(icon=>icon.IsEffectivelyVisible);
            Check(navIcon.Monochrome && navIcon.Foreground is ISolidColorBrush navBrush && navBrush.Color==Resource(window,"AppTextSecondary"),
                "Material navigation icons drop fixed Fluent category colors");
            var dialog = new Window {Title="皮肤确认测试",Width=360,Height=150,CanResize=false,Content=new TextBlock {Text="保留危险操作确认正文"}};
            var dialogResult=SkinDialog.ShowAsync<bool>(dialog,window); await Ready();
            Check(dialog.IsVisible && dialog.WindowDecorations==WindowDecorations.BorderOnly && Resource(dialog,"AppWindowBackground")==Resource(window,"AppWindowBackground")
                && dialog.GetVisualDescendants().OfType<CaptionAssetButton>().Single().UseSkinGlyph,
                "actual owned confirmation inherits skin and custom caption");
            dialog.Close(false); Check(!await dialogResult && dialog.Resources.MergedDictionaries.Count==0,
                "dialog cancellation result and lease cleanup survive skin wrapper");
            using(var settingsFrame=window.CaptureRenderedFrame()) settingsFrame?.Save(Path.Combine(root,"settings-material3.png"),PngBitmapEncoderOptions.Default);
            var savedPath=Path.Combine(Program.SelfTestDataRoot!,"ui.json");
            var savedBytes=File.ReadAllBytes(savedPath);
            using(var heldFile=new FileStream(savedPath,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                if(selector is not null) selector.SelectedIndex=1; // Metro
                await Ready();
                Check(StoredSkin(c.Current)=="material3" && Resource(window,"AppAccent")==Color.Parse("#6750A4"), "failed settings save keeps both stored and rendered skin");
                Check(selector is not null && selector.SelectedIndex==2 && c.Status!="已保存并应用", "failed settings save restores selector and exposes error");
            }
            Check(File.ReadAllBytes(savedPath).SequenceEqual(savedBytes), "failed save preserves original config bytes");
            c.Undo(); await Ready();
            Check(StoredSkin(c.Current)=="metro" && Resource(window,"AppAccent")==Color.Parse("#2D89EF"), "undo after failed save restores previous successful skin");
            c.Redo(); await Ready();
            Check(StoredSkin(c.Current)=="material3" && Resource(window,"AppAccent")==Color.Parse("#6750A4"), "redo restores the accepted skin in controls and storage");
            await window.NavigateForTestingAsync(AppPage.Chat); await Ready();
            Check(chat.FindControl<Border>("ComposerCard")!.CornerRadius==new CornerRadius(16), "Material composer uses skin card shape after page return");
            // The earlier dock layout can put the first user message outside the virtualized viewport.
            // Reveal it through the real scroller before inspecting its newly realized visual.
            chat.FindControl<ScrollViewer>("TimelineScroller")!.Offset=new Vector(0,0); await Ready();
            userAvatar=chat.GetVisualDescendants().OfType<Border>().First(border=>AutomationProperties.GetAutomationId(border)=="MSG-USER-AVATAR");
            Check(userAvatar.CornerRadius==new CornerRadius(20) && ReferenceEquals(userAvatar.DataContext,originalMessage), "Material current avatar uses skin while preserving virtualized message model");
            window.MoveDockPage("browser",DockZone.Right); await Ready();
            var browser=(BrowserView)window.PageForTesting("browser");
            for(var index=0;index<8;index++) browser.ViewModel.NewTabCommand.Execute(null);
            await Ready();
            var browserScroll=browser.FindControl<ScrollViewer>("BrowserTabScroller")!;
            Check(browserScroll.Offset.X>0, "creating browser tabs reveals selected tab in overflowing strip");
            browserScroll.Offset=new Vector(0,0); await Ready();
            var browserPoint=browserScroll.TranslatePoint(new Point(20,12),window)!.Value;
            window.MouseWheel(browserPoint,new Vector(0,-1)); await Ready();
            Check(browserScroll.Offset.X>0, "browser tab strip also accepts ordinary horizontal browsing with wheel");
            window.MoveDockPage("files",DockZone.Left); await Ready();
            var tab=window.GetVisualDescendants().OfType<Button>().Single(button=>Avalonia.Automation.AutomationProperties.GetAutomationId(button)=="dock-tab-files");
            tab.ContextMenu!.Open(tab); await Ready();
            Check(tab.ContextMenu.IsOpen && tab.ContextMenu.CornerRadius==new CornerRadius(16), "actual dock context menu receives Material 3 popup styling");
            tab.ContextMenu.Close(); await Ready();
            var id=window.FloatDockPage("files"); await Ready();
            var floating=id is null ? null : window.FloatingDockWindowForTesting(id);
            Check(floating is not null && Resource(floating,"AppWindowBackground")==Color.Parse("#FEF7FF"), "new floating window receives current skin dictionaries");
            Check(floating?.Background is ISolidColorBrush surface && surface.Color==Color.Parse("#FEF7FF") && floating.WindowDecorations==WindowDecorations.BorderOnly,
                "floating window paints active skin and uses custom chrome");
            Check(floating is not null && floating.GetVisualDescendants().OfType<CaptionAssetButton>().Count(b=>b.UseSkinGlyph)==3,
                "floating caption controls use current skin glyphs");
            if(floating is not null) using(var floatFrame=floating.CaptureRenderedFrame()) floatFrame?.Save(Path.Combine(root,"floating-material3.png"),PngBitmapEncoderOptions.Default);
            Select("metro","dark"); await Ready();
            Check(floating is not null && floating.ActualThemeVariant==ThemeVariant.Dark && Resource(floating,"AppAccent")==Color.Parse("#4DB2FF"), "existing floating window hot switches skin and theme");
            Check(floating is not null && floating.TryFindResource("SystemAccentColor",floating.ActualThemeVariant,out var nativeFloatAccent) && nativeFloatAccent is Color floatAccent && floatAccent==Color.Parse("#4DB2FF"),
                "floating native controls also release direct Fluent color overrides");
            Select("aero"); await Ready();
            var aeroCaption=window.FindControl<Border>("CustomCaption")!;
            var aeroClose=window.FindControl<CaptionAssetButton>("CaptionClose")!;
            Check(StoredSkin(c.Current)=="aero" && Resource(window,"AppAccent")==Color.Parse("#2F7ED8"), "Aero public selection persists and applies its own palette");
            Check(aeroCaption.Classes.Contains("aeroCaption") && aeroClose.Classes.Contains("aeroCaptionButton") && aeroClose.Theme is not null,
                "Aero real main caption attaches actual glossy window button theme");
            Check(!aeroClose.Classes.Contains("captionButton"), "Fluent caption class cannot override Aero theme");
            Check(floating is not null && floating.GetVisualDescendants().OfType<CaptionAssetButton>().All(button=>button.Classes.Contains("aeroCaptionButton")),
                "existing floating caption hot switches all three actual buttons to Aero");
            window.WindowState=WindowState.Maximized; await Ready();
            Check(window.FindControl<CaptionAssetButton>("CaptionMaximize")!.Classes.Contains("restore"), "Aero main maximized state displays restore plate");
            window.WindowState=WindowState.Normal; await Ready();
            Check(window.FindControl<CaptionAssetButton>("CaptionMaximize")!.Classes.Contains("max") && !window.FindControl<CaptionAssetButton>("CaptionMaximize")!.Classes.Contains("restore"),
                "Aero main restored state displays maximize plate");
            if(floating is not null) {
                floating.WindowState=WindowState.Maximized; await Ready();
                Select("material3"); Select("aero"); await Ready();
                var max=floating.GetVisualDescendants().OfType<CaptionAssetButton>().Single(button=>button.Kind=="restore");
                floating.WindowState=WindowState.Normal; await Ready();
                Check(max.Classes.Contains("max") && !max.Classes.Contains("restore"), "attaching Aero while floating window is maximized still restores its button correctly");
            }
            var aeroDialog=new Window {Title="Aero confirmation probe",Width=360,Height=180,Content=new Button{Content="取消"}};
            var aeroResult=SkinDialog.ShowAsync<bool>(aeroDialog,window); await Ready();
            var dialogClose=aeroDialog.GetVisualDescendants().OfType<CaptionAssetButton>().Single();
            Check(dialogClose.Classes.Contains("aeroCaptionButton") && dialogClose.Classes.Contains("single") && dialogClose.Theme is not null,
                "Aero owned dialog created after lease application attaches single close plate");
            Check(Resource(aeroDialog,"AppAccent")==Resource(window,"AppAccent"), "Aero dialog has isolated owner-matching palette");
            aeroDialog.Close(false); Check(!await aeroResult, "Aero dialog cancel retains false result");
            Check(aeroDialog.Resources.MergedDictionaries.Count==0 && !dialogClose.Classes.Contains("aeroCaptionButton"), "Aero dialog close releases dictionaries and caption theme");
            var aeroStore=new WorkbenchAppearanceStore(Program.SelfTestDataRoot!);
            Check(StoredSkin(aeroStore.Current)=="aero", "Aero real settings persistence loads in fresh store");
            using(var frame=window.CaptureRenderedFrame()) {Check(frame is not null,"actual Aero main frame exists");frame?.Save(Path.Combine(root,"main-aero-light-1100x800.png"),PngBitmapEncoderOptions.Default);}
            Select("aero","dark"); await Ready();
            Check(Resource(window,"AppAccent")==Color.Parse("#4EA6EE") && floating is not null && Resource(floating,"AppAccent")==Color.Parse("#4EA6EE"), "Aero light-dark transition updates main and floating palettes");
            using(var frame=window.CaptureRenderedFrame()) {Check(frame is not null,"actual Aero dark frame exists");frame?.Save(Path.Combine(root,"main-aero-dark-1100x800.png"),PngBitmapEncoderOptions.Default);}
            // ===== LiquidGlass：玻璃包覆 chrome + iOS 令牌；headless 无 Skia 快照时按实色降级 =====
            Select("liquidglass"); await Ready();
            var lgCaption=window.FindControl<Border>("CustomCaption")!;
            Check(StoredSkin(c.Current)=="liquidglass" && lgCaption.Classes.Contains("liquidGlass") && lgCaption.Classes.Contains("liquidGlassCaption"),
                "LiquidGlass selection persists and wraps real caption border in glass");
            Check(lgCaption.Parent is Features.Skins.LiquidGlass.LiquidGlassSurface,
                "LiquidGlass caption host is reparented into actual LiquidGlassSurface");
            Check(Resource(window,"AppAccent")!=Color.Parse("#FF007AFF") && Resource(window,"AppAccent")==Resource(window,"SystemAccentColor"),
                "LiquidGlass follows injected accent (ApplyCustomization) rather than locking iOS blue");
            Check(Resource(window,"LiquidGlassAccent")==Color.Parse("#FF007AFF"),
                "LiquidGlass token dictionary merged and exposes its iOS probe key");
            Check(lgCaption.Background is null || lgCaption.Background is ISolidColorBrush {Color.A:0},
                "LiquidGlass caption background transparent so self-drawn glass shows through");
            var lgSidebar=window.FindControl<Border>("SidebarBorder");
            Check(lgSidebar is not null && lgSidebar.Classes.Contains("liquidGlass") && lgSidebar.Parent is Features.Skins.LiquidGlass.LiquidGlassSurface,
                "LiquidGlass dock host border also wrapped");
            var lgCornerCheck=chat.FindControl<Border>("ComposerCard")!.CornerRadius;
            // 输入区改版后圆角收敛为全局单值（composerCard 样式），皮肤不再单独给出 14。
            Check(lgCornerCheck==new CornerRadius(24), "LiquidGlass composer card uses iOS 24pt continuous radius");
            using(var frame=window.CaptureRenderedFrame()) {Check(frame is not null,"actual LiquidGlass main frame exists");frame?.Save(Path.Combine(root,"main-liquidglass-light-1100x800.png"),PngBitmapEncoderOptions.Default);}
            Select("liquidglass","dark"); await Ready();
            Check(Resource(window,"AppWindowBackground")==Color.Parse("#FF000000") && Resource(window,"LiquidGlassAccent")==Color.Parse("#FF0A84FF"),
                "LiquidGlass dark variant swaps to iOS dark palette");
            using(var frame=window.CaptureRenderedFrame()) {Check(frame is not null,"actual LiquidGlass dark frame exists");frame?.Save(Path.Combine(root,"main-liquidglass-dark-1100x800.png"),PngBitmapEncoderOptions.Default);}
            var lgDialog=new Window {Title="LiquidGlass confirmation probe",Width=360,Height=180,Content=new Button{Content="取消"}};
            var lgResult=SkinDialog.ShowAsync<bool>(lgDialog,window); await Ready();
            Check(lgDialog.GetVisualDescendants().OfType<Border>().Any(b=>b.Classes.Contains("liquidGlass")),
                "LiquidGlass owned dialog chromeBar wrapped through AttachCaption path");
            lgDialog.Close(false); Check(!await lgResult, "LiquidGlass dialog cancel retains false result");
            Check(lgDialog.Resources.MergedDictionaries.Count==0, "LiquidGlass dialog close releases dictionaries");
            Select("fluent"); await Ready();
            Check(!lgCaption.Classes.Contains("liquidGlass") && !(lgCaption.Parent is Features.Skins.LiquidGlass.LiquidGlassSurface),
                "leaving LiquidGlass restores caption border parent and classes");
            Check(!aeroCaption.Classes.Contains("aeroCaption") && aeroClose.Classes.Contains("captionButton") && aeroClose.Theme is null,
                "returning from Aero restores main caption classes and exact previous theme");
            Check(floating is not null && floating.GetVisualDescendants().OfType<CaptionAssetButton>().All(button=>!button.Classes.Contains("aeroCaptionButton")),
                "returning from Aero also releases floating caption adapters");
            c.Change(s=>s with {Accent="#087E66"}); await Ready();
            var retainedRadius=c.Current.Radius;
            Check(floating is not null && Resource(floating,"AppAccent")==Color.Parse("#087E66") && Resource(window,"AppAccent")==Color.Parse("#087E66"), "return to Fluent restores customized accent in both windows");
            Check(floating is not null && floating.Resources.MergedDictionaries.Count==0, "Fluent floating window releases optional skin dictionary");
            Check(!window.FindControl<CaptionAssetButton>("CaptionClose")!.UseSkinGlyph && window.FindControl<CaptionAssetButton>("CaptionClose")!.Content is Image,
                "returning to Fluent restores original caption artwork");
            Check(ReferenceEquals(window.FindResource("Icon.Folder"),fluentGeometry), "returning to Fluent restores original icon geometry");
            userAvatar=chat.GetVisualDescendants().OfType<Border>().First(border=>AutomationProperties.GetAutomationId(border)=="MSG-USER-AVATAR");
            Check(userAvatar.CornerRadius==new CornerRadius(12), "returning to Fluent restores current message avatar metric");
            Check(window.TryFindResource("SystemAccentColor",window.ActualThemeVariant,out var nativeFluentAccent) && nativeFluentAccent is Color restoredAccent && restoredAccent==Color.Parse("#087E66"),
                "returning to Fluent restores original customized native accent color");
            Check(!navIcon.Monochrome, "returning to Fluent restores category icon color mode");
            await window.NavigateForTestingAsync(AppPage.Settings); await Ready();
            vm.Settings.SelectedCategory=vm.Settings.Categories.Single(item=>item.Id=="appearance"); await Ready();
            var restoredRadiusRow=window.GetVisualDescendants().OfType<Border>().First(border=>border.Classes.Contains("settingsRow") && border.GetVisualDescendants().OfType<TextBlock>().Any(text=>text.Text=="圆角"));
            Check(restoredRadiusRow.IsEnabled && restoredRadiusRow.IsVisible && c.Current.Radius==retainedRadius, "return to Fluent reveals retained shape preference");
            await window.NavigateForTestingAsync(AppPage.Chat); await Ready();
            tab=floating?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button=>Avalonia.Automation.AutomationProperties.GetAutomationId(button)=="dock-tab-files") ?? tab;
            Select("metro"); await Ready();
            tab.ContextMenu!.Open(tab); await Ready();
            Check(tab.ContextMenu.IsOpen && tab.ContextMenu.CornerRadius==new CornerRadius(0), "same real dock menu switches to Metro after page reparent");
            tab.ContextMenu.Close(); await Ready();
            if(id is not null) {window.DockFloatingRegion(id); await Ready();}
            Check(ReferenceEquals(TopLevel.GetTopLevel(window.PageForTesting("files")),window), "redocking keeps original page on main window");
            Select("future-skin"); await Ready();
            c.Change(s=>s with {FontSize=14}); await Ready();
            Check(StoredSkin(c.Current)=="future-skin" && JsonNode.Parse(File.ReadAllText(Path.Combine(Program.SelfTestDataRoot!,"ui.json")))?["skin"]?.GetValue<string>()=="future-skin", "unknown skin survives unrelated setting save");
            Check(Resource(window,"AppWindowBackground")==baseline, "unknown skin visibly falls back to Fluent");
            for(var round=0;round<3;round++) {Select("metro"); Select("material3"); Select("aero"); Select("fluent");}
            await Ready();
            Check(window.Styles.Count==baseStyles && window.Resources.MergedDictionaries.Count==baseDictionaries, "main repeated switching restores resource/style counts");
            Check(!window.FindControl<Border>("CustomStatusBar")!.IsVisible, "skin changes never enable bottom status strip");
            Select("material3"); await Ready();
            // Terminal surface skinning is verified in the terminal plugin tests (the page is optional).
            c.Preset("reading"); await Ready();
            Check(StoredSkin(c.Current)=="material3" && c.Current.Composer=="reading", "reading preset keeps optional skin while changing layout preferences");
            var reopenedStore=new WorkbenchAppearanceStore(Program.SelfTestDataRoot!);
            Check(StoredSkin(reopenedStore.Current)=="material3", "fresh store loads the skin persisted by real UI actions");
            window.Width=800;window.Height=640; await Ready();
            using(var frame=window.CaptureRenderedFrame()) {
                Check(frame is not null, "actual current main skin frame exists");
                frame?.Save(Path.Combine(root,"main-material3-800x640.png"),PngBitmapEncoderOptions.Default);
            }
            var hiddenId=window.FloatDockPage("files"); await Ready();
            var hiddenWindow=hiddenId is null ? null : window.FloatingDockWindowForTesting(hiddenId);
            hiddenWindow?.Close(); await Ready();
            Check(hiddenId is not null && window.DockLayoutForTesting.FindFloating("files") is {Hidden:true}, "native-equivalent floating close persists hidden page before restart");
            window.Close(); await Ready();
            var reopened=new MainWindow {Width=800,Height=640};
            reopened.Show();
            try
            {
                await Task.Delay(80);
                Dispatcher.UIThread.RunJobs(); reopened.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Check(StoredSkin(reopened.CustomizationForTesting.Current)=="material3", "new main window reloads persisted skin after the prior window closes");
                Check(reopened.FindControl<CaptionAssetButton>("CaptionClose")!.UseSkinGlyph,
                    "fresh main applies optional caption mode during initial construction");
                var expected=reopened.ActualThemeVariant==ThemeVariant.Dark ? Color.Parse("#141218") : Color.Parse("#FEF7FF");
                Check(Resource(reopened,"AppWindowBackground")==expected, "reopened main renders its loaded skin using actual inherited theme");
                Check(!reopened.FindControl<Border>("CustomStatusBar")!.IsVisible, "reopened main retains hidden bottom status strip");
                var restoredFloat=reopened.DockLayoutForTesting.FindFloating("files");
                Check(restoredFloat is {Hidden:true} && restoredFloat.Id==hiddenId && reopened.FloatingDockWindowForTesting(hiddenId!) is {IsVisible:false},
                    "new main restores hidden floating ownership without opening or crashing");
                reopened.OpenDockPage("files",DockZone.Left); await Task.Delay(80); Dispatcher.UIThread.RunJobs(); reopened.UpdateLayout();
                Check(reopened.FloatingDockWindowForTesting(hiddenId!) is {IsVisible:true}, "restored hidden page reopens through normal page entry");
            }
            finally {reopened.Close();}
            Console.WriteLine("Skins evidence: " + root);
        }
        finally {window.Close(); Program.SelfTestDataRoot=previousRoot;}
    }
}
