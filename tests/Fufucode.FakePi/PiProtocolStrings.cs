#nullable enable
using System.Text.Json;

// Upgrade-sensitive wire vocabulary for the verified Pi 1.1.0 fixture subset.
internal static class PiProtocolStrings
{
    public const string Version = "1.1.0";
    public const int SessionVersion = 3;
    public const string AgentDirectoryVariable = "PI_CODING_AGENT_DIR";
    public const string FollowUpBehavior = "followUp";
    public const string BridgeStatusCommand = "fufucode-bridge.status";
    public const string ExtensionSource = "extension";
    // Ordinary named factories remain <inline:name>; only builtin:true uses this prefix.
    public const string BuiltinSource = "builtin";
    public const string BuiltinPathPrefix = "builtin:";
    public const string LlamaExtensionPath = BuiltinPathPrefix + "llama.cpp";
    public const string McpExtensionPath = BuiltinPathPrefix + "mcp";
    public const bool NoExtensionsDisablesBuiltins = true;
    public static readonly string[] DefaultBuiltinExtensions = ["llama.cpp", "codemode", "tool-search", "mcp"];
    public static readonly string[] DefaultBuiltinCommands = ["llama", "mcp"];

    internal static class Command
    {
        public const string ExtensionUiResponse = "extension_ui_response";
        public const string GetAvailableModels = "get_available_models";
        public const string Steer = "steer";
        public const string FollowUp = "follow_up";
        public const string GetState = "get_state";
        public const string SetModel = "set_model";
        public const string SetThinkingLevel = "set_thinking_level";
        public const string GetAvailableThinkingLevels = "get_available_thinking_levels";
        public const string Fork = "fork";
        public const string GetCommands = "get_commands";
        public const string SetSessionName = "set_session_name";
        public const string GetEntries = "get_entries";
        public const string ClearQueue = "clear_queue";
        public const string Abort = "abort";
        public const string Prompt = "prompt";
        public const string Die = "die";
        public const string RespondExit = "respond_exit";
        public const string Hang = "hang";
        public const string Malformed = "malformed";
        public const string NoId = "no_id";
        public const string MissingFields = "missing_fields";
        public const string WrongCommand = "wrong_command";
        public const string Large = "large";
        public const string EmitMulti = "emit_multi";
        public const string Reverse = "reverse";
        public const string Stderr = "stderr";
        public const string Emit = "emit";
    }

    internal static class Wire
    {
        public const string AgentEnd = "agent_end";
        public const string AgentSettled = "agent_settled";
        public const string AgentStart = "agent_start";
        public const string AutoRetryEnd = "auto_retry_end";
        public const string AutoRetryStart = "auto_retry_start";
        public const string Compaction = "compaction";
        public const string CompactionEnd = "compaction_end";
        public const string CompactionStart = "compaction_start";
        public const string ExtensionError = "extension_error";
        public const string ExtensionUiRequest = "extension_ui_request";
        public const string FirstEvent = "first_event";
        public const string Message = "message";
        public const string MessageEnd = "message_end";
        public const string MessageStart = "message_start";
        public const string MessageUpdate = "message_update";
        public const string Mystery = "mystery";
        public const string ProgressWhileApproval = "progress_while_approval";
        public const string Response = "response";
        public const string SecondEvent = "second_event";
        public const string Session = "session";
        public const string Text = "text";
        public const string TextDelta = "text_delta";
        public const string TextEnd = "text_end";
        public const string TextStart = "text_start";
        public const string Thinking = "thinking";
        public const string ThinkingDelta = "thinking_delta";
        public const string ThinkingEnd = "thinking_end";
        public const string Toolcall = "toolCall";
        public const string ToolExecutionEnd = "tool_execution_end";
        public const string ToolExecutionStart = "tool_execution_start";
        public const string ToolExecutionUpdate = "tool_execution_update";
        public const string ToolcallDelta = "toolcall_delta";
        public const string ToolcallEnd = "toolcall_end";
        public const string ToolcallStart = "toolcall_start";
    }

    internal static class UiMethod
    {
        public const string Confirm = "confirm";
        public const string Editor = "editor";
        public const string FutureMethod = "future_method";
        public const string Input = "input";
        public const string Notify = "notify";
        public const string Select = "select";
        public const string Setstatus = "setStatus";
    }

    internal static class StopReason
    {
        public const string Aborted = "aborted";
        public const string Error = "error";
        public const string Stop = "stop";
        public const string Tooluse = "toolUse";
    }
}
