using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

/// <summary>Presentation of authority receipts. Raw payloads remain unchanged and inspectable.</summary>
internal static class GraphEntryPresentation
{
    internal static bool IsSource(GraphEntry entry) => entry.Kind == "source";
    internal static bool IsActiveTicketVeto(GraphEntry entry, string ticketId)
    {
        if (entry.Kind != "Veto" || entry.TicketId != ticketId) return false;
        try
        {
            using var document = JsonDocument.Parse(entry.Text);
            return Get(document.RootElement, "status") == "Active" && Get(document.RootElement, "target") == "ticket:" + ticketId;
        }
        catch (JsonException) { return false; }
    }
    internal static bool IsStructured(string text)
    {
        var first = text.AsSpan().TrimStart();
        return !first.IsEmpty && first[0] is '{' or '[';
    }
    internal static string Summary(GraphEntry entry)
    {
        if (!IsStructured(entry.Text)) return PlainText(entry);
        try
        {
            using var document = JsonDocument.Parse(entry.Text);
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) return GraphLabels.EntryKind(entry.Kind);
            var heading = GraphLabels.EntryKind(entry.Kind);
            switch (entry.Kind)
            {
                case "ticket-created":
                    return Join("邮件已建立", Get(data, "title"), Get(data, "goal"));
                case "scan":
                    var modules = Get(data, "modules"); var relations = Get(data, "relations");
                    return modules.Length > 0 && relations.Length > 0 ? $"扫描完成 · {modules} 个模块，{relations} 条关系" : "扫描完成";
                case "source": return "来源记录";
                case "module":
                    var after = Object(data, "after");
                    return Join("模块已更新", Get(after, "name"), GraphLabels.ModuleSummary(Get(after, "summary")));
                case "module-removed": return Join("模块已移除", Get(data, "name"));
                case "relation": return Join("关系已更新", Get(Object(data, "after"), "label"));
                case "relation-removed": return Join("关系已移除", Get(data, "label"));
                case "purpose": return Join("目的已更新", Get(data, "after"));
                case "options": return "项目选项已更新";
                case "evidence": case "evidence-claim":
                    var criterion = Get(data, "criterion");
                    var result = Boolean(data, "passed");
                    var valid = Boolean(data, "valid");
                    return Join(criterion.Length == 0 ? heading : criterion,
                        valid == false ? "证据已失效" : result == true ? "通过" : result == false ? "未通过" : "待核对",
                        Get(data, "text"));
                case "evidence-source": return Join("证据来源已记录", Get(data, "criterion"));
                case "run-result": return Join("运行结果：" + GraphLabels.Status(Get(data, "status")),
                    Get(data, "error"), Get(data, "output"));
                case "attention": return Join(Get(data, "question"), Get(data, "answer"));
                case "conflict": return Join("存在待核对的变更", Get(data, "text"));
                default: return Join(heading, Get(data, "text"), Get(data, "reason"));
            }
        }
        catch (JsonException) { return GraphLabels.EntryKind(entry.Kind) + " · 记录格式异常"; }
    }
    private static string PlainText(GraphEntry entry) => entry.Text switch
    {
        "Linked session" => "会话已关联",
        "Planning ticket created from the human purpose" => "已根据项目目的建立规划邮件",
        "Cancellation requested; awaiting executor acknowledgement" => "已请求停止，等待执行者确认",
        "Source disappeared; identity retained because tickets, child modules or manual relations still reference it." => "来源已移除，关联模块和邮件仍保留",
        "Evidence content changed; previous passing evidence invalidated" => "证据内容已变化，先前的通过结果已失效",
        _ => entry.Text
    };
    internal static string RevisionSummary(string summary)
    {
        if (summary.StartsWith("Updated module: ", StringComparison.Ordinal)) return "更新模块：" + summary[16..];
        if (summary.StartsWith("Agent command: ", StringComparison.Ordinal)) return "项目操作已记录";
        if (summary.StartsWith("Recorded actual ", StringComparison.Ordinal) && summary.EndsWith(" session message", StringComparison.Ordinal))
            return summary.Contains("assistant", StringComparison.Ordinal) ? "收到助手邮件" : "收到会话邮件";
        return summary switch
        {
            "Project opened; automatic execution requires explicit resume" => "项目已打开",
            "Human updated project purpose" => "目的已更新", "Human updated Graph options" => "项目选项已更新",
            "Scanned real project sources" => "项目扫描完成", "Created work ticket" => "邮件已建立",
            "Added project correspondence" => "往来已记录", "Updated module relation" => "关系已更新",
            "Removed module relation" => "关系已移除", "Removed module" => "模块已移除",
            "Recorded model evidence claim" => "收到证据提交", "Linked execution session" => "会话已关联",
            "Human reviewed ticket evidence" => "证据已审核", "Human vetoed ticket" => "邮件已否决",
            "Human lifted ticket veto" => "否决已解除", "Human reconciled unknown ticket outcome" => "未知结果已核查",
            "Started managed ticket execution" => "邮件已开始运行", "Requested managed execution cancellation" => "已请求停止运行",
            "Execution progress" => "运行进展", "Received managed execution terminal receipt" => "运行结果已记录",
            "Human stopped queued ticket" => "已停止排队邮件", "Human requested ticket stop" => "已请求停止邮件",
            "Human enabled autonomous queue advancement" => "自动推进已启用", "Queued work resumed within the existing human mandate" => "队列已恢复",
            "Automatic queue stopped after an error" => "推进因错误停止", "Automatic queue is idle" => "队列空闲",
            "Rebirth authority changed" => "项目状态已更新", "Rejected command identity conflict" => "操作冲突已记录",
            "Rejected stale Graph command" => "已拒绝过期操作", "Rejected session message identity conflict" => "邮件冲突已记录",
            _ => IsStructured(summary) ? "变更记录" : summary
        };
    }
    private static JsonElement Object(JsonElement source, string name)
    {
        if (source.ValueKind == JsonValueKind.Object)
            foreach (var property in source.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    private static string Get(JsonElement source, string name)
    {
        var value = Object(source, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => ""
        };
    }
    private static bool? Boolean(JsonElement source, string name) => Object(source, name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static string Join(params string[] parts) => string.Join(Environment.NewLine, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
