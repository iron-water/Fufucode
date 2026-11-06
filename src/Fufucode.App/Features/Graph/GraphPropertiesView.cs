using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

public sealed class GraphPropertiesView : GraphContentView
{
    public GraphPropertiesView(GraphWorkspaceViewModel vm) : base(vm)
    {
        AutomationProperties.SetAutomationId(this, "GraphProperties");
        var purpose = GraphUi.Input("PurposeDraft", "GraphPurpose", true);
        GraphUi.Bind(purpose, IsEnabledProperty, "CanEdit");
        GraphUi.Bind(purpose, IsVisibleProperty, "!IsHistorical");
        var oldPurpose = GraphUi.Value("Snapshot.Purpose");
        GraphUi.Bind(oldPurpose, IsVisibleProperty, "IsHistorical");
        var projectActions = GraphUi.Actions(
            GraphUi.Button("保存目的", () => vm.SavePurpose(), "GraphSavePurpose", true),
            GraphUi.AsyncButton("扫描", () => vm.ScanAsync(), "GraphScan"),
            GraphUi.Button("刷新", vm.Refresh, "GraphRefresh"),
            GraphUi.Button("新建模块", () => vm.BeginModule(), "GraphNewModule", true));
        var moduleActions = GraphUi.Actions(
            GraphUi.Button("编辑", () => vm.BeginModule(vm.SelectedModuleId), "GraphEditModule", true),
            GraphUi.Button("新建子模块", () => vm.BeginModule(null, vm.SelectedModuleId), "GraphNewChild", true),
            GraphUi.Button("删除", () => { if (vm.SelectedModuleId is { } id) vm.RemoveModule(id); }, "GraphRemoveModule", true),
            GraphUi.Button("展开", vm.ExpandSelected, "GraphDrill"),
            GraphUi.Button("打开会话", () => vm.OpenChat(vm.SelectedModuleId, null), "GraphModuleChat"),
            GraphUi.Button("新建工单", vm.BeginTicket, "GraphModuleTicket", true));
        GraphUi.Bind(moduleActions, IsVisibleProperty, "HasSelectedModule");
        var moduleDetails = GraphUi.Stack(GraphUi.Value("SelectedModule.Id", "caption"),
            GraphUi.Value("SelectedModuleSummary"), GraphUi.Field("父模块", GraphUi.Value("SelectedModuleParent")),
            GraphUi.Field("源码", GraphUi.Value("SelectedModuleSources", "caption")));
        GraphUi.Bind(moduleDetails, IsVisibleProperty, "HasSelectedModule");
        var moduleEditor = GraphUi.Stack(
            GraphUi.Field("名称", GraphUi.Input("ModuleDraft.Name", "GraphModuleName")),
            GraphUi.Field("父模块", GraphUi.ModuleCombo("ModuleDraft.ParentId", true)),
            GraphUi.Field("摘要", GraphUi.Input("ModuleDraft.Summary", "GraphModuleSummary", true)),
            GraphUi.Field("源码路径", GraphUi.Input("ModuleDraft.SourcePaths", "GraphModulePaths", true)),
            GraphUi.Actions(GraphUi.Button("保存模块", () => vm.SaveModule(), "GraphSaveModule", true),
                GraphUi.Button("取消", vm.CancelModuleEdit, "GraphCancelModule")));
        GraphUi.Bind(moduleEditor, IsVisibleProperty, "IsModuleEditorOpen");
        GraphUi.Bind(moduleEditor, IsEnabledProperty, "CanEdit");
        var relationList = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphRelationRow>((row, _) => row == null ? null :
                GraphUi.Stack(GraphUi.Text(row.Text, "caption"), GraphUi.Actions(
                    GraphUi.Button("编辑关系", () => vm.BeginRelation(row.Relation.Id), edit: true),
                    GraphUi.Button("删除关系", () => vm.RemoveRelation(row.Relation.Id), edit: true))))
        };
        GraphUi.Bind(relationList, ItemsControl.ItemsSourceProperty, "RelationRows");
        var relationKind = GraphUi.KindCombo("RelationDraft.Kind");
        AutomationProperties.SetAutomationId(relationKind, "GraphRelationKind");
        var relationEditor = GraphUi.Stack(
            GraphUi.Field("起点", GraphUi.ModuleCombo("RelationDraft.SourceId")),
            GraphUi.Field("终点", GraphUi.ModuleCombo("RelationDraft.TargetId")),
            GraphUi.Field("关系", relationKind),
            GraphUi.Field("标签", GraphUi.Input("RelationDraft.Label", "GraphRelationLabel")),
            GraphUi.Actions(GraphUi.Button("保存关系", () => vm.SaveRelation(), "GraphSaveRelation", true),
                GraphUi.Button("取消", vm.CancelRelationEdit, "GraphCancelRelation")));
        GraphUi.Bind(relationEditor, IsVisibleProperty, "IsRelationEditorOpen");
        GraphUi.Bind(relationEditor, IsEnabledProperty, "CanEdit");
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
        var content = GraphUi.Stack(GraphUi.Version(vm), GraphUi.Error(vm), GraphUi.Busy(),
            GraphUi.Value("Snapshot.Title", "bodyStrong"), GraphUi.Value("ProjectStatus", "caption"),
            GraphUi.Field("目的", purpose), oldPurpose, projectActions, organization, GraphUi.Divider(),
            GraphUi.Value("SelectionLabel", "bodyStrong"), moduleDetails, moduleActions, moduleEditor,
            GraphUi.Divider(), GraphUi.Actions(GraphUi.Text("关系", "bodyStrong"),
                GraphUi.Button("新增关系", () => vm.BeginRelation(), "GraphNewRelation", true)), relationList, relationEditor);
        content.Margin = new Thickness(12);
        Content = GraphUi.Scroll(content);
    }
}
