using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    private Task ShowAuthenticationAsync()
    {
        var layout = _options.CreateAppDataLayout();
        var settings = _host?.Manager.Settings ?? new AppSettingsStore(layout);
        var vault = OperatingSystem.IsWindows() ? new WindowsPasswordVaultCredentialResolver() : null;
        var rows = new StackPanel { Spacing = 8 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var name = new TextBox { PlaceholderText = "环境变量名称", Text = settings.Current.Endpoint?.ApiKeyEnvironmentVariable ?? "" };
        var password = new TextBox { PasswordChar = '*', PlaceholderText = "API 密钥", MaxLength = 2048 };
        var save = new Button { Content = "保存到 Windows 凭据库", IsEnabled = vault is not null };
        var remove = new Button { Content = "移除选中的凭据", IsEnabled = vault is not null };
        var refresh = new Button { Content = "刷新" };
        var list = new ListBox { MinHeight = 100 };
        void Refresh()
        {
            rows.Children.Clear();
            try
            {
                var names = vault?.ListNames() ?? [];
                list.ItemsSource = names;
                // 只列出实际存在的凭据与当前端点引用的变量，不预置任何开发期变量名。
                var variables = names
                    .Concat(settings.Current.Endpoint is { } endpoint ? [endpoint.ApiKeyEnvironmentVariable] : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal);
                foreach (var variable in variables)
                {
                    var process = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable));
                    var user = OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User));
                    rows.Children.Add(new TextBlock { Text = variable + "：" +
                        (names.Contains(variable) ? "Windows 凭据库" : process ? "进程环境变量" : user ? "用户环境变量（重启应用后生效）" : "未配置"),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                }
                var path = Path.Combine(layout.AgentDirectory, "auth.json");
                if (File.Exists(path))
                {
                    using var auth = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var provider in auth.RootElement.EnumerateObject())
                        rows.Children.Add(new TextBlock { Text = "后端认证：" + provider.Name + "（由后端插件管理）", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                }
            }
            catch (Exception error) { status.Text = "读取失败：" + error.Message; }
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string value) name.Text = value; };
        save.Click += (_, _) =>
        {
            try
            {
                vault!.Save((name.Text ?? "").Trim(), password.Text ?? "");
                password.Text = "";
                status.Text = "已保存。仅在模型配置引用同名变量时使用；新会话生效。";
                Refresh();
            }
            catch (Exception error) { status.Text = "保存失败：" + error.Message; }
        };
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string selected) { status.Text = "请选择本应用的凭据。"; return; }
            var confirm = new Window { Title = "移除凭据", Width = 400, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var yes = new Button { Content = "移除" }; var no = new Button { Content = "取消", IsCancel = true };
            confirm.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = {
                new TextBlock { Text = selected, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { no, yes } } } };
            yes.Click += (_, _) => confirm.Close(true); no.Click += (_, _) => confirm.Close(false);
            if (!await Fufucode.App.Views.SkinDialog.ShowAsync<bool>(confirm, TopLevel.GetTopLevel(remove) as Window ?? this)) return;
            try { vault!.Delete(selected); status.Text = "已移除凭据库条目；未修改环境变量或 Pi auth.json。"; Refresh(); }
            catch (Exception error) { status.Text = "移除失败：" + error.Message; }
        };
        refresh.Click += (_, _) => Refresh();
        Refresh();
        return ShowPanelAsync("模型认证管理", new StackPanel { Spacing = 12, Children = {
            new TextBlock { Text = "认证来源", FontWeight = Avalonia.Media.FontWeight.SemiBold }, rows,
            list, name, password,
            new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { save, remove, refresh } }, status } });
    }
}
