#nullable enable
using System.Text.Json;

// Every registry row binds its named encoder and its reviewable fixture schema here.
// This is the existing fixture subset compatible with Pi 1.0.0, not a complete official schema.
// Metadata never validates or rewrites envelopes: JSON defaults and byte framing remain unchanged.
internal static class PiEventHandlers
{
    public static readonly EventSchema ResponseSchema = new(
        ["type", "command"],
        "id:string?, success:bool?, error:string?, data:object?; data varies per RPC handler",
        "Normal ACK requires id and success. no_id deliberately omits id; missing_fields omits success; wrong_command substitutes command. Reverse adds data.order/text; generic fallback returns data.ok.");
    public static string Response(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema AgentStartSchema = new(
        ["type"],
        "No payload fields",
        "Starts stream/early/hold/error/approval/persistence scenarios; does not itself change streaming.");
    public static string AgentStart(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema AgentEndSchema = new(
        ["type", "willRetry"],
        "willRetry:bool",
        "stream/early emits willRetry=true before retry and settled; never the terminal completion marker.");
    public static string AgentEnd(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema AgentSettledSchema = new(
        ["type"],
        "No payload fields",
        "Final completion, abort, approval response, all CLI dialogs resolved, or deliberate late_events/approval_settled injection.");
    public static string AgentSettled(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema MessageStartSchema = new(
        ["type", "message"],
        "message:{role:assistant, content:object[]}",
        "Existing fixtures always begin with empty content; this is only the fixture subset.");
    public static string MessageStart(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema MessageUpdateSchema = new(
        ["type", "usage", "assistantMessageEvent"],
        "usage:{input:int, output:int, totalTokens:int}; assistantMessageEvent:{type:string, contentIndex:int, ...}",
        "text_start; text_delta(delta); thinking_delta(delta); toolcall_start(id,toolName); toolcall_delta(delta); text_end/thinking_end(content); toolcall_end(toolCall:{type:toolCall,id,name,arguments:{path}}). Usage repeats intentionally to test deduplication.");
    public static string MessageUpdate(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema MessageEndSchema = new(
        ["type", "message"],
        "message:{role:assistant, content:object[], stopReason:string, usage?:object, errorMessage?:string}",
        "stop: text-only approval/persistence or stream text+thinking+toolCall with usage. error: empty content + provider errorMessage. aborted: empty content + request aborted. toolUse: incomplete persistence crash fixture.");
    public static string MessageEnd(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema AutoRetryStartSchema = new(
        ["type"],
        "attempt?:int",
        "stream/early adds attempt=1; late_events emits type only to exercise late-event filtering.");
    public static string AutoRetryStart(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema AutoRetryEndSchema = new(
        ["type", "success"],
        "success:bool",
        "Existing stream/early and late_events fixtures emit success=true.");
    public static string AutoRetryEnd(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema CompactionStartSchema = new(
        ["type"],
        "No payload fields",
        "Only late_events emits this minimal envelope; no real compaction payload is simulated.");
    public static string CompactionStart(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema CompactionEndSchema = new(
        ["type"],
        "No payload fields",
        "Only late_events emits this minimal envelope; persisted compaction entries use separate session-entry schema.");
    public static string CompactionEnd(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema ExtensionUiRequestSchema = new(
        ["type", "id", "method"],
        "id:string; method:string; method-specific fields below",
        "confirm(title,message,timeout); select(title,options:string[],timeout); input(title,placeholder); editor(title,prefill); setStatus(statusKey,statusText); notify(message,notifyType); future_method intentionally has no additional fields. IDs drive fixture replies in extension_ui_response.");
    public static string ExtensionUiRequest(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema ExtensionErrorSchema = new(
        ["type", "extensionPath", "event", "error"],
        "extensionPath:string, event:string, error:string",
        "approval scenarios emit fixture.ts/other_handler/example failure; this is a synthetic extension runtime error, not startup stderr failure.");
    public static string ExtensionError(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema ProgressWhileApprovalSchema = new(
        ["type", "value"],
        "value:int",
        "Synthetic unknown event, value=1, exercises progress while approval is pending.");
    public static string ProgressWhileApproval(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema ToolExecutionStartSchema = new(
        ["type", "toolCallId", "toolName", "args"],
        "toolCallId:string, toolName:string, args:{path:string}",
        "stream/early uses call-1/read/abc.");
    public static string ToolExecutionStart(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema ToolExecutionUpdateSchema = new(
        ["type", "toolCallId", "toolName", "partialResult"],
        "partialResult:{content:[{type:text,text:string}]}",
        "stream/early sends partial; replacement semantics are checked by unchanged tests.");
    public static string ToolExecutionUpdate(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema ToolExecutionEndSchema = new(
        ["type", "toolCallId", "toolName", "result", "isError"],
        "result:{content:[{type:text,text:string}]}; isError:bool",
        "stream/early sends complete with isError=false.");
    public static string ToolExecutionEnd(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema MysterySchema = new(
        ["type", "text"],
        "text:string",
        "Synthetic unknown event: Unicode separators + split bytes, or 10000 characters for record-limit failure.");
    public static string Mystery(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema FirstEventSchema = new(
        ["type"],
        "No payload fields",
        "Synthetic LF batch, immediately precedes second_event in one pipe write.");
    public static string FirstEvent(object envelope) => JsonSerializer.Serialize(envelope);

    public static readonly EventSchema SecondEventSchema = new(
        ["type"],
        "No payload fields",
        "Synthetic LF batch, immediately follows first_event in one pipe write.");
    public static string SecondEvent(object envelope) => JsonSerializer.Serialize(envelope);

}
