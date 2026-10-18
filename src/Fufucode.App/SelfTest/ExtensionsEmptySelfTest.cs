using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

public static class ExtensionsEmptySelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool result, string label)
        {
            if (result) pass("W5-R3 扩展：" + label);
            else fail("W5-R3 扩展：" + label);
        }

        var vm = new ExtensionsPageViewModel();
        Check(vm.IsMock && vm.EmptyState == ExtensionEmptyState.None &&
              vm.MockNotice.Contains("未连接真实后端") && !vm.HasCommandEmptyState,
            "mock 诚实未连接，不冒充未安装或已查询");
        vm.PreviewSnapshot(new ExtensionCatalogSnapshot());
        Check(vm.EmptyState == ExtensionEmptyState.NotInstalled &&
              vm.HasExtensionEmptyState && vm.HasCommandEmptyState,
            "成功查询空目录显示未安装");
        vm.ExtensionFilter = "搜索词";
        Check(vm.EmptyState == ExtensionEmptyState.NotInstalled,
            "目录原本为空时筛选不冒充无匹配");

        const string alpha = @"C:\R3\项目\alpha.ts";
        const string beta = @"C:\R3\global\beta.ts";
        var command = new CommandEntry("explain", "解释当前项目", "extension", alpha, null, "project");
        vm.ExtensionFilter = string.Empty;
        vm.PreviewSnapshot(new ExtensionCatalogSnapshot
        {
            DiscoveredExtensions = [new(alpha, "project", false), new(beta, "global", false)],
            Commands = [command],
        });
        vm.PreviewExtensionState([], [alpha], [], []);
        var sourceRows = vm.Extensions.ToArray();
        var sourceCollection = vm.Extensions;
        vm.ExtensionFilter = "  BETA  ";
        Check(vm.FilteredExtensions.Count == 1 && vm.FilteredExtensions[0].Path == beta &&
              ReferenceEquals(sourceRows.Single(row => row.Path == beta), vm.FilteredExtensions[0]),
            "扩展筛选忽略大小写和首尾空格，保留同一行对象");
        Check(ReferenceEquals(sourceCollection, vm.Extensions) && vm.Extensions.SequenceEqual(sourceRows) &&
              vm.Extensions.Single(row => row.Path == alpha).IsPendingEnabled && vm.PendingChangeCount == 1,
            "筛选未改写源集合或待启用状态");
        vm.ExtensionFilter = "绝无此扩展";
        Check(vm.EmptyState == ExtensionEmptyState.NoMatches && vm.FilteredExtensions.Count == 0 &&
              vm.Extensions.Count == 2 && vm.ExtensionEmptyDescription.Contains("调整或清空"),
            "已有扩展无匹配与未安装分开");
        vm.ClearExtensionFilterCommand.Execute(null);
        Check(vm.EmptyState == ExtensionEmptyState.None && vm.FilteredExtensions.SequenceEqual(sourceRows),
            "清空筛选恢复源行和顺序");

        vm.CommandFilter = "未匹配命令";
        Check(vm.HasCommandEmptyState && vm.CommandEmptyText.Contains("匹配") &&
              !vm.CommandEmptyText.Contains("没有已注册"),
            "命令无匹配不声称没有注册");
        vm.CommandFilter = string.Empty;
        Check(vm.Commands.Count == 1 && vm.Commands[0].Entry == command,
            "命令清空筛选恢复真实目录");

        var page = new ExtensionsPage { DataContext = vm };
        var window = new Window { Content = page, Width = 960, Height = 720 };
        window.Show();
        try
        {
            await SettleAsync();
            Check(!page.FindControl<StackPanel>("ExtensionEmptyStatePanel")!.IsVisible,
                "非空目录真实控件隐藏空态");
            vm.ExtensionFilter = "绝无此扩展";
            await SettleAsync();
            Check(page.FindControl<StackPanel>("ExtensionEmptyStatePanel")!.IsVisible &&
                  page.FindControl<TextBlock>("ExtensionEmptyDescription")!.Text!.Contains("匹配"),
                "无匹配绑定到真实空态控件");
            vm.PreviewSnapshot(new ExtensionCatalogSnapshot { QueryError = "测试查询失败" });
            await SettleAsync();
            Check(vm.EmptyState == ExtensionEmptyState.Failed && !vm.HasCommandEmptyState &&
                  !vm.ExtensionFilterHasNoMatches && vm.ExtensionEmptyDescription.Contains("测试查询失败"),
                "查询错误优先失败态，不伪造空成功或无命令");
            Check(page.FindControl<TextBlock>("ExtensionEmptyDescription")!.Text!.Contains("测试查询失败"),
                "失败原因绑定到真实页面");

            vm.PreviewSnapshot(new ExtensionCatalogSnapshot());
            vm.PreviewExtensionState([], [], [], []);
            await SettleAsync();
            Check(vm.EmptyState == ExtensionEmptyState.NotInstalled &&
                  page.FindControl<StackPanel>("ExtensionEmptyStatePanel")!.IsVisible,
                "失败后成功空查询恢复未安装态");
            vm.ExtensionFilter = string.Empty;
            vm.PreviewSnapshot(new ExtensionCatalogSnapshot
            {
                DiscoveredExtensions = [new(alpha, "project", false), new(beta, "global", false)],
                Commands = [command],
            });
            window.Width = 420;
            await SettleAsync();
            var headerActions = page.FindControl<WrapPanel>("ExtensionsHeaderActions")!;
            var list = page.FindControl<ItemsControl>("ExtensionsList")!;
            var actionRows = list.GetVisualDescendants().OfType<Grid>()
                .Where(grid => grid.Children.LastOrDefault() is WrapPanel).ToArray();
            Check(Grid.GetRow(headerActions) == 1 && Grid.GetColumn(headerActions) == 0 &&
                  actionRows.Length == 2 && actionRows.All(grid =>
                      Grid.GetRow(grid.Children.Last()) == 1 && grid.ColumnDefinitions.Count == 1),
                "420DIP 页头与扩展操作区上下重排");
            Check(list.Bounds.Width <= page.Bounds.Width &&
                  page.FindControl<TextBox>("ExtensionFilterBox")!.Bounds.Width <= page.Bounds.Width,
                "窄窗列表和筛选框宽度受页面约束");
            window.Width = 960;
            await SettleAsync();
            Check(Grid.GetRow(headerActions) == 0 && Grid.GetColumn(headerActions) == 2 &&
                  actionRows.All(grid => Grid.GetRow(grid.Children.Last()) == 0),
                "恢复宽窗时操作区回到右侧");
        }
        catch (Exception ex)
        {
            fail("W5-R3 扩展：无头控件验证异常：" + ex.GetBaseException().Message);
        }
        finally
        {
            window.Close();
        }

        async Task SettleAsync()
        {
            await Task.Delay(60);
            window.UpdateLayout();
        }
    }
}
