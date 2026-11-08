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
    private readonly List<IDisposable> _graphDeliveryCoordinators = new();
    private GraphDevelopmentHost InitializeGraphDevelopment(GraphProjectService service, GraphWorkspaceViewModel model)
    {
        // These references are intentionally process-local. Deserialized receipt JSON grants no authority.
        var liveResults = new Dictionary<string, GraphCandidateValidation>(StringComparer.Ordinal);
        var integratedResults = new Dictionary<string, GraphIntegratedValidation>(StringComparer.Ordinal);
        var validationPolicy = ManagedValidationPolicy.Strict;
        try { validationPolicy = Task.Run(() => GraphDevelopmentHost.CreateRepositoryValidationPolicyAsync(service.ReadSnapshot().ProjectPath)).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException)
        { System.Diagnostics.Trace.TraceWarning("Graph validation remains strict: " + error.Message); }
        var host = new GraphDevelopmentHost(GraphLayout, service,
            permission => AuthorizeGraphValidationAsync(permission),
            permission =>
            {
                service.RequireDeliveryAuthorization(permission.TicketId);
                return Task.FromResult(true); // Internal integration is within the current mandate, not publication.
            }, validationPolicy,
            publicationGate: permission => service.ReadOptions().AutoPublish && (permission.TargetReference is "refs/heads/main" or "refs/heads/master")
                ? Task.FromResult(true)
                : ApproveGraphDevelopmentAsync("批准发布到用户主分支", JsonSerializer.Serialize(permission)));
        service.RecoveryProbe = ticket =>
        {
            var inspection = Task.Run(() => GraphRecoveryInspector.InspectAsync(GraphLayout, service, host, ticket)).GetAwaiter().GetResult();
            service.RecordHostInspection(ticket, inspection);
        };
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
        model.CompleteInternalRequested += async (ticket, criteria, continuation, reason, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!integratedResults.TryGetValue(ticket, out var verified))
                throw new InvalidOperationException("需要本进程完成集成后的真实验证，历史 JSON 不能作为完成凭据。");
            await host.CompleteInternalWorkAsync(verified, criteria, continuation, reason, token);
            integratedResults.Remove(ticket);
            if (service.ReadOptions().AutoPublish)
            {
                try
                {
                    await host.PublishIntegratedAsync(host.GetIntegrationJournalPath(verified), token);
                    return "内部工作已完成并安全发布；不代表产品或项目验收通过。";
                }
                catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
                {
                    return "内部工作已完成，集成成果已保留；发布待处理：" + error.Message;
                }
            }
            return "内部工作已依据宿主验证与集成版本完成；不代表产品或项目验收通过。";
        };
        var coordinator = new GraphDeliveryCoordinator(service, host);
        _graphDeliveryCoordinators.Add(coordinator);
        Closed += (_, _) => coordinator.Dispose();
        return host;
    }

    private Task<bool> AuthorizeGraphValidationAsync(ManagedValidationPermission permission)
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
        var command = Quote(permission.Executable) + " " + string.Join(" ", System.Linq.Enumerable.Select(permission.Arguments, Quote));
        var policy = new Fufucode.Core.Security.SecurityPolicyStore(GraphLayout.Root).Current;
        var decision = Fufucode.Core.Security.SecurityPolicyEvaluator.EvaluateCommand(command, policy);
        return decision.Action switch
        {
            Fufucode.Core.Security.SecurityAction.Allow => Task.FromResult(true),
            Fufucode.Core.Security.SecurityAction.Deny => Task.FromResult(false),
            _ => ApproveGraphDevelopmentAsync("批准受保护的验证命令", JsonSerializer.Serialize(permission))
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
