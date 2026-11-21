using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

public sealed class GraphPropertiesView : GraphContentView
{
    public GraphPropertiesView(GraphWorkspaceViewModel vm) : base(vm)
    {
        AutomationProperties.SetAutomationId(this, "GraphProperties");
        var content = GraphUi.Sections(
            GraphUi.Stack(GraphUi.Version(vm), GraphUi.Error(vm), GraphUi.Busy()),
            Project(vm), Module(vm), Relations(vm));
        Content = GraphUi.Scroll(content);
    }

    private static Control Project(GraphWorkspaceViewModel vm)
    {
        var title = GraphUi.Value("Snapshot.Title", "bodyStrong");
        var status = GraphUi.Value("ProjectStatus", "caption");
        var heading = GraphUi.Stack(title, status);
        heading.Spacing = 2;
        var purpose = GraphUi.Input("PurposeDraft", "GraphPurpose", true);
        GraphUi.Bind(purpose, IsEnabledProperty, "CanEdit");
        GraphUi.Bind(purpose, IsVisibleProperty, "!IsHistorical");
        var oldPurpose = GraphUi.Value("Snapshot.Purpose");
        GraphUi.Bind(oldPurpose, IsVisibleProperty, "IsHistorical");
        var actions = GraphUi.Actions(
            GraphUi.Primary("保存目的", () => vm.SavePurpose(), "GraphSavePurpose", true),
            GraphUi.AsyncButton("扫描", () => vm.ScanAsync(), "GraphScan"),
            GraphUi.Button("刷新", vm.Refresh, "GraphRefresh"),
            GraphUi.Button("新建模块", () => vm.BeginModule(), "GraphNewModule", true));
        var responsibilities = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphResponsibility>((item, _) => item == null ? null : GraphUi.Text(vm.ResponsibilityText(item)))
        };
        GraphUi.Bind(responsibilities, ItemsControl.ItemsSourceProperty, "OrganizationResponsibilities");
        var dependencies = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphWorkDependency>((item, _) => item == null ? null : GraphUi.Text(vm.DependencyText(item), "caption"))
        };
        GraphUi.Bind(dependencies, ItemsControl.ItemsSourceProperty, "WorkDependencies");
        var organization = new Expander { Header = "组织与工作依赖", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = GraphUi.Stack(GraphUi.Value("OrganizationVersion", "caption"), responsibilities, dependencies) };
        return GraphUi.Section(heading, GraphUi.Field("目的", purpose), oldPurpose, actions, organization);
    }

    private static Control Module(GraphWorkspaceViewModel vm)
    {
        var id = GraphUi.Value("SelectedModule.Id", "caption");
        id.Classes.Add("mono");
        var details = GraphUi.Stack(id, GraphUi.Value("SelectedModuleSummary"),
            GraphUi.Field("父模块", GraphUi.Value("SelectedModuleParent")),
            GraphUi.Field("源码", Mono(GraphUi.Value("SelectedModuleSources", "caption"))));
        GraphUi.Bind(details, IsVisibleProperty, "HasSelectedModule");

        var remove = GraphUi.Button("删除", () => { }, "GraphRemoveModule", true);
        remove.Classes.Add("graphDanger");
        remove.Flyout = ConfirmRemove(vm);
        var actions = GraphUi.Actions(
            GraphUi.Button("编辑", () => vm.BeginModule(vm.SelectedModuleId), "GraphEditModule", true),
            GraphUi.Button("新建子模块", () => vm.BeginModule(null, vm.SelectedModuleId), "GraphNewChild", true),
            GraphUi.Button("展开", vm.ExpandSelected, "GraphDrill"),
            GraphUi.Button("打开会话", () => vm.OpenChat(vm.SelectedModuleId, null), "GraphModuleChat"),
            remove);
        GraphUi.Bind(actions, IsVisibleProperty, "HasSelectedModule");

        var editor = GraphUi.Stack(
            GraphUi.Field("名称", GraphUi.Input("ModuleDraft.Name", "GraphModuleName")),
            GraphUi.Field("父模块", GraphUi.ModuleCombo("ModuleDraft.ParentId", true)),
            GraphUi.Field("摘要", GraphUi.Input("ModuleDraft.Summary", "GraphModuleSummary", true)),
            GraphUi.Field("源码路径", GraphUi.Input("ModuleDraft.SourcePaths", "GraphModulePaths", true)),
            GraphUi.Actions(GraphUi.Primary("保存模块", () => vm.SaveModule(), "GraphSaveModule", true),
                GraphUi.Button("取消", vm.CancelModuleEdit, "GraphCancelModule")));
        editor.Spacing = 12;
        GraphUi.Bind(editor, IsVisibleProperty, "IsModuleEditorOpen");
        GraphUi.Bind(editor, IsEnabledProperty, "CanEdit");
        return GraphUi.Section(GraphUi.Value("SelectionLabel", "bodyStrong"), details, actions, editor);
    }

    private static Flyout ConfirmRemove(GraphWorkspaceViewModel vm)
    {
        var flyout = new Flyout();
        var question = GraphUi.Text("", "body");
        GraphUi.Bind(question, TextBlock.TextProperty, "SelectionLabel", source: vm,
            converter: new Avalonia.Data.Converters.FuncValueConverter<string?, string>(name => $"删除「{name}」及其关系？"));
        var confirm = GraphUi.Button("删除", () =>
        {
            flyout.Hide();
            if (vm.SelectedModuleId is { } id) vm.RemoveModule(id);
        }, "GraphConfirmRemoveModule", true);
        confirm.Classes.Add("graphDanger");
        var body = GraphUi.Stack(question, confirm);
        body.Spacing = 12; body.MaxWidth = 280;
        flyout.Content = body;
        return flyout;
    }

    private static Control Relations(GraphWorkspaceViewModel vm)
    {
        var header = GraphUi.HeaderRow(GraphUi.Text("关系", "bodyStrong"),
            GraphUi.Command("Add", null, () => vm.BeginRelation(), "GraphNewRelation", "新增关系", "CanEdit"));
        var list = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphRelationRow>((row, _) => row == null ? null : RelationRow(vm, row))
        };
        GraphUi.Bind(list, ItemsControl.ItemsSourceProperty, "RelationRows");
        var kind = GraphUi.KindCombo("RelationDraft.Kind");
        AutomationProperties.SetAutomationId(kind, "GraphRelationKind");
        var editor = GraphUi.Stack(
            GraphUi.Field("起点", GraphUi.ModuleCombo("RelationDraft.SourceId")),
            GraphUi.Field("终点", GraphUi.ModuleCombo("RelationDraft.TargetId")),
            GraphUi.Field("关系", kind),
            GraphUi.Field("标签", GraphUi.Input("RelationDraft.Label", "GraphRelationLabel")),
            GraphUi.Actions(GraphUi.Primary("保存关系", () => vm.SaveRelation(), "GraphSaveRelation", true),
                GraphUi.Button("取消", vm.CancelRelationEdit, "GraphCancelRelation")));
        editor.Spacing = 12;
        GraphUi.Bind(editor, IsVisibleProperty, "IsRelationEditorOpen");
        GraphUi.Bind(editor, IsEnabledProperty, "CanEdit");
        return GraphUi.Section(header, list, editor);
    }

    private static Control RelationRow(GraphWorkspaceViewModel vm, GraphRelationRow row)
    {
        var endpoints = GraphUi.SingleLine(row.SourceName + " → " + row.TargetName, "body");
        var kind = GraphUi.SingleLine(GraphLabels.RelationKind(row.Relation.Kind) +
            (string.IsNullOrWhiteSpace(row.Relation.Label) ? "" : " · " + row.Relation.Label), "caption");
        var text = GraphUi.Stack(endpoints, kind);
        text.Spacing = 2; text.VerticalAlignment = VerticalAlignment.Center;
        var remove = GraphUi.Command("Dismiss", null, () => vm.RemoveRelation(row.Relation.Id),
            "GraphRemoveRelation-" + row.Relation.Id, "删除关系", "CanEdit", vm);
        remove.Classes.Add("graphDanger");
        var line = GraphUi.HeaderRow(text,
            GraphUi.Command("Edit", null, () => vm.BeginRelation(row.Relation.Id), "GraphEditRelation-" + row.Relation.Id, "编辑关系", "CanEdit", vm),
            remove);
        line.Margin = new Thickness(0, 2);
        return line;
    }

    private static TextBlock Mono(TextBlock block) { block.Classes.Add("mono"); return block; }
}
