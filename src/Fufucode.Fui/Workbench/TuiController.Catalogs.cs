namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private FuiRuntimeCatalog? _runtimeCatalog;
    private FuiCapabilitySource? _capabilitySourceFilter;
    private FuiCapabilityKind? _capabilityKindFilter;

    private void Context()
    {
        var lines = _lines.ToArray();
        var sources = _lineSources.ToArray();
        var attachments = _attachments.ToArray();
        var plan = CurrentPlan;
        if (_active is not IFuiSessionInspector inspector)
        {
            var known = new FuiSessionInfo(
                _active.Project,
                _active.Id,
                _active.Model,
                null,
                0, 0, 0, 0, 0,
                null,
                ContextWindowUnknownReason: "当前会话没有可证明的上下文事实接口。",
                ExecutionMode: (_active as IFuiPlanSession)?.ExecutionMode);
            OpenPicker("context", FuiContextWorkbench.Title,
                FuiContextWorkbench.Build(known, attachments, plan, lines, sources, factsAvailable: false),
                options: new(
                    GroupHeader: FuiChoiceGroups.IsGroup,
                    PreserveOrder: true,
                    Footer: FuiContextWorkbench.Footer,
                    Kind: FuiOverlayKind.Context,
                    EmptyMessage: "当前会话没有可展示的上下文事实。"));
            return;
        }
        Begin("context", async ct =>
        {
            var info = await inspector.InspectAsync(ct);
            var choices = FuiContextWorkbench.Build(info, attachments, plan, lines, sources);
            return new Result(
                Choices: choices,
                Title: FuiContextWorkbench.Title,
                Footer: FuiContextWorkbench.Footer,
                PanelKind: FuiOverlayKind.Context);
        }, cancellable: true);
    }

    private void Extensions()
    {
        if (_active is not IFuiRuntimeCatalogSource source)
        {
            OpenEmptyPanel("extensions", "扩展 · 只读",
                FuiRuntimeCatalogWorkbench.EmptyExtensions, FuiOverlayKind.Extensions,
                FuiRuntimeCatalogWorkbench.ExtensionsFooter);
            return;
        }
        Begin("extensions", async ct =>
        {
            var catalog = await source.ReadRuntimeCatalogAsync(ct);
            return new Result(
                Choices: catalog.ExtensionChoices(),
                Title: "扩展 · 只读",
                Footer: FuiRuntimeCatalogWorkbench.ExtensionsFooter,
                PanelKind: FuiOverlayKind.Extensions);
        }, cancellable: true);
    }

    private void Mcp()
    {
        if (_active is not IFuiRuntimeCatalogSource source)
        {
            OpenEmptyPanel("mcp", "MCP · 只读",
                FuiRuntimeCatalogWorkbench.EmptyMcp, FuiOverlayKind.Mcp,
                FuiRuntimeCatalogWorkbench.McpFooter);
            return;
        }
        Begin("mcp", async ct =>
        {
            var catalog = await source.ReadRuntimeCatalogAsync(ct);
            var choices = catalog.McpChoices();
            var message = choices.Count != 0
                ? null
                : catalog.McpUnavailableReason ?? "当前会话没有可证明的 MCP 条目；未扫描配置，未联网。";
            return new Result(
                Choices: choices,
                Title: "MCP · 只读",
                Message: message,
                Footer: FuiRuntimeCatalogWorkbench.McpFooter,
                PanelKind: FuiOverlayKind.Mcp);
        }, cancellable: true);
    }

    private void Capabilities()
    {
        if (_active is not IFuiRuntimeCatalogSource source)
        {
            OpenEmptyPanel("capabilities", "能力目录",
                FuiRuntimeCatalogWorkbench.EmptyCapabilities, FuiOverlayKind.Capabilities,
                FuiRuntimeCatalogWorkbench.CapabilitiesFooter);
            return;
        }
        Begin("capabilities", async ct =>
        {
            var catalog = await source.ReadRuntimeCatalogAsync(ct);
            var choices = catalog.CapabilityChoices();
            return new Result(
                Choices: choices,
                Title: FuiRuntimeCatalogWorkbench.CapabilityTitle(null, null),
                Message: choices.Count == 0 ? FuiRuntimeCatalogWorkbench.EmptyCapabilities : null,
                Footer: FuiRuntimeCatalogWorkbench.CapabilitiesFooter,
                PanelKind: FuiOverlayKind.Capabilities,
                Runtime: catalog);
        }, cancellable: true);
    }

    private void Jobs()
    {
        var choices = FuiJobsWorkbench.Choices(_agentActivity);
        if (choices.Count == 0)
        {
            OpenEmptyPanel("jobs", FuiJobsWorkbench.Title,
                "尚无子代理作业事实。", FuiOverlayKind.Jobs, FuiJobsWorkbench.Footer);
            return;
        }
        _pickerAction = "jobs";
        _picker = new(FuiJobsWorkbench.Title, choices, options: new(
            Detail: choice => _agentActivity.Find(choice.Value) is { } job
                ? FuiJobsWorkbench.Detail(_agentActivity, job)
                : null,
            GroupHeader: FuiChoiceGroups.IsGroup,
            PreserveOrder: true,
            Footer: FuiJobsWorkbench.Footer,
            Kind: FuiOverlayKind.Jobs));
        Dirty = true;
    }

    private void CycleCapabilityFilter(bool source)
    {
        if (_pickerAction != "capabilities" || _runtimeCatalog is not { } catalog) return;
        var query = _picker?.View.Query ?? "";
        if (source)
            _capabilitySourceFilter = FuiRuntimeCatalogWorkbench.NextSource(_capabilitySourceFilter);
        else
            _capabilityKindFilter = FuiRuntimeCatalogWorkbench.NextKind(_capabilityKindFilter);
        _picker = new(
            FuiRuntimeCatalogWorkbench.CapabilityTitle(_capabilitySourceFilter, _capabilityKindFilter),
            catalog.CapabilityChoices(_capabilitySourceFilter, _capabilityKindFilter),
            query,
            options: new(
                GroupHeader: FuiChoiceGroups.IsGroup,
                PreserveOrder: true,
                Footer: FuiRuntimeCatalogWorkbench.CapabilitiesFooter,
                Kind: FuiOverlayKind.Capabilities,
                EmptyMessage: "当前来源/类型筛选没有可证明能力。"));
        Dirty = true;
    }

    private void ContextChoice(FuiChoice choice)
    {
        if (choice.Value.StartsWith(FuiContextWorkbench.AttachmentPrefix, StringComparison.Ordinal) &&
            int.TryParse(choice.Value.AsSpan(FuiContextWorkbench.AttachmentPrefix.Length),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var attachment) &&
            (uint)attachment < (uint)_attachments.Count)
        {
            var file = _attachments[attachment];
            IReadOnlyList<TuiImage>? images = file.MimeType is { } mime && file.EncodedBytes is { } bytes
                ? [new TuiImage(file.Path, mime, bytes, MaxRows: 8)]
                : null;
            Info(choice.Detail ?? choice.Label, images);
            return;
        }
        if (FuiContextWorkbench.TryLineIndex(choice, out var index))
        {
            if ((uint)index >= (uint)_lines.Count)
            {
                Error("所选项已不在当前对话中，未跳转。");
                return;
            }
            _scrollTarget = index;
            if (_lines[index].Kind == "thinking" && !_showThinking) SetSetting("show-thinking", "true", announce: false);
            if (_lines[index].Kind == "tool" && !_expandTools) SetSetting("expand-tools", "true", announce: false);
            _status = "已定位 · " + TerminalText.SingleLine(choice.Label);
            return;
        }
        Info(choice.Detail ?? choice.Label);
    }

    private void ShowJobDetail(FuiChoice choice)
    {
        if (_agentActivity.Find(choice.Value) is not { } job)
        {
            Error("该作业已不存在。");
            return;
        }
        Info(FuiJobsWorkbench.Detail(_agentActivity, job));
    }

    private void OpenEmptyPanel(string id, string title, string message, FuiOverlayKind kind, string footer)
    {
        _pickerAction = id;
        _picker = new FuiPicker(title, [], options: new(
            Footer: footer,
            Kind: kind,
            EmptyMessage: message));
        Dirty = true;
    }
}
