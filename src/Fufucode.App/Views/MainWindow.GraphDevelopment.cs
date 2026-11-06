using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Fufucode.App.Features.Graph;
using Fufucode.App.Integration;
using Fufucode.Core.Automation;
using Fufucode.Plugins.AutoDev.Graph;

namespace Fufucode.App;

public partial class MainWindow
{
    private void InitializeGraphDevelopment(GraphProjectService service, GraphWorkspaceViewModel model)
    {
        // These references are intentionally process-local. Deserialized receipt JSON grants no authority.
        var liveResults = new Dictionary<string, GraphCandidateValidation>(StringComparer.Ordinal);
        var integratedResults = new Dictionary<string, GraphIntegratedValidation>(StringComparer.Ordinal);
        var host = new GraphDevelopmentHost(GraphLayout, service,
            permission => ApproveGraphDevelopmentAsync("批准候选验证", JsonSerializer.Serialize(new
            {
                permission.Executable, permission.Arguments, permission.WorkingDirectory,
                permission.ContractHash, Policy = "Strict", permission.Policy.Digest,
                permission.Policy.GeneratedDirectories, permission.Policy.GeneratedFiles, permission.Policy.ProtectedFiles
            }, new JsonSerializerOptions { WriteIndented = true })),
            permission => ApproveGraphDevelopmentAsync("批准候选集成", JsonSerializer.Serialize(permission,
                new JsonSerializerOptions { WriteIndented = true })), ManagedValidationPolicy.Strict);
        model.ValidateCandidateRequested += async (ticket, manifest, executable, arguments, timeout, token) =>
        {
            liveResults.Remove(ticket);
            var result = await host.ValidateCandidateAsync(ticket, manifest, new GraphValidationSpec(executable, arguments, timeout), token);
            liveResults[ticket] = result;
            return $"候选验证：{result.Receipt.Status}\n退出码：{result.Receipt.ExitCode}\n{result.Receipt.Error}\n" +
                $"合约：{result.Receipt.ContractHash}\n策略：{result.Receipt.ValidationPolicyDigest}\n" +
                "此结果不代表产品验收；集成后仍需重新验证。";
        };
        model.IntegrateCandidateRequested += async (ticket, token) =>
        {
            if (!liveResults.TryGetValue(ticket, out var result) || !host.VerifyCurrent(result))
                throw new InvalidOperationException("需要本进程持有且仍有效的通过验证；请重新验证候选。");
            var integrated = await host.IntegrateCandidateAsync(result, token);
            liveResults.Remove(ticket);
            if (!integrated.RequiresPostIntegrationValidation)
                throw new InvalidOperationException("Integration did not provide a post-integration verification requirement.");
            var verified = await host.ValidateIntegratedAsync(result,
                new GraphValidationSpec(result.Contract.Executable, result.Contract.Arguments, result.Contract.Timeout), token);
            integratedResults[ticket] = verified;
            return $"候选已集成；目标验证：{verified.Receipt.Status}，退出码：{verified.Receipt.ExitCode}。\n" +
                (host.VerifyIntegratedCurrent(verified)
                    ? "宿主回执仍有效；尚需明确验收覆盖范围，未自动进行产品验收。"
                    : "目标验证未形成有效通过依据，工作未标记完成。") + "\n" + verified.Receipt.Error;
        };
        model.CompleteInternalRequested += (ticket, criteria, continuation, reason, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!integratedResults.TryGetValue(ticket, out var verified))
                throw new InvalidOperationException("需要本进程完成集成后的真实验证，历史 JSON 不能作为完成凭据。");
            host.CompleteInternalWork(verified, criteria, continuation, reason);
            integratedResults.Remove(ticket);
            return Task.FromResult("内部工作已依据宿主验证完成；不代表产品或项目验收通过。");
        };
    }

    private async Task<bool> ApproveGraphDevelopmentAsync(string title, string fullRequest)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ApproveGraphDevelopmentAsync(title, fullRequest));
        if (_closed || !IsVisible) return false;
        var approve = new Button { Content = "批准此次操作" };
        var reject = new Button { Content = "拒绝", IsCancel = true };
        var dialog = new Window
        {
            Title = title, Width = 760, Height = 600, MinWidth = 480, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16),
                Children =
                {
                    new TextBox { Text = fullRequest, IsReadOnly = true, AcceptsReturn = true,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
                        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                        Children = { reject, approve } }
                }
            }
        };
        Grid.SetRow(((Grid)dialog.Content).Children[1], 1);
        approve.Click += (_, _) => dialog.Close(true);
        reject.Click += (_, _) => dialog.Close(false);
        // Closing either owner or dialog never counts as approval; failure to show also denies.
        EventHandler ownerClosed = (_, _) => dialog.Close(false);
        Closed += ownerClosed;
        try { return await dialog.ShowDialog<bool>(this); }
        catch (InvalidOperationException) { return false; }
        finally { Closed -= ownerClosed; }
    }
}
