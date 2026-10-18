using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>
    /// 新聊天后端选择：FUMO 原生内核（默认）始终可用；Pi/OMP 只在对应后端插件
    /// 实际安装且运行时可用时列出，不在 UI 里硬编码固定两项。
    /// </summary>
    private async Task<BackendId?> ChooseNewBackendAsync()
    {
        var choices = AvailableBackends();
        if (choices.Count == 1)
        {
            return choices[0].Backend;
        }

        var dialog = new Window
        {
            Title = "新聊天 · 选择后端", Width = 410, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var list = new ComboBox
        {
            ItemsSource = choices.Select(c => c.Name).ToArray(),
            SelectedIndex = 0,
        };
        var create = new Button { Content = "创建聊天" };
        var cancel = new Button { Content = "取消" };
        create.Click += (_, _) => dialog.Close((BackendId?)choices[list.SelectedIndex].Backend);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12, Children =
            {
                new TextBlock { Text = "聊天创建后固定使用所选后端。" }, list,
                new TextBlock { Text = "历史聊天按已保存的后端恢复。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { create, cancel } }
            }
        };
        return await Fufucode.App.Views.SkinDialog.ShowAsync<BackendId?>(dialog, this);
    }

    /// <summary>可用后端清单：FUMO 恒在首位，其它后端按运行时实际可用性追加。</summary>
    private IReadOnlyList<(BackendId Backend, string Name)> AvailableBackends()
    {
        var result = new List<(BackendId, string)> { (BackendId.NativeCSharp, "FUMO（默认 · 纯 C#）") };
        var manager = _host?.Manager;
        if (manager is not null)
        {
            if (manager.IsBackendAvailable(BackendId.Pi)) result.Add((BackendId.Pi, "Pi（后端插件）"));
            if (manager.IsBackendAvailable(BackendId.Omp)) result.Add((BackendId.Omp, "OMP（后端插件）"));
            if (manager.IsBackendAvailable(BackendId.Codex)) result.Add((BackendId.Codex, "Codex（后端插件）"));
            if (manager.IsBackendAvailable(BackendId.Dsh)) result.Add((BackendId.Dsh, "DSH（后端插件）"));
            if (manager.IsBackendAvailable(BackendId.Claude)) result.Add((BackendId.Claude, "Claude（后端插件）"));
        }
        else
        {
            if (_options.PiNodePath is not null || _options.PiCliPath is not null)
            {
                result.Add((BackendId.Pi, "Pi（后端插件）"));
            }
            if (_options.CodexExecutablePath is not null)
            {
                result.Add((BackendId.Codex, "Codex（后端插件）"));
            }
            if (_options.DshExecutablePath is not null)
            {
                result.Add((BackendId.Dsh, "DSH（后端插件）"));
            }
            if (_options.ClaudeExecutablePath is not null)
            {
                result.Add((BackendId.Claude, "Claude（后端插件）"));
            }
        }

        return result;
    }
}
