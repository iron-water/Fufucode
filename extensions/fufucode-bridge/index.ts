/**
 * Fufucode Bridge — Pi tool-approval extension.
 *
 * Hooks the `tool_call` event (fires before the tool runs) and routes the
 * decision through the official `ctx.ui.confirm` RPC round-trip
 * (extension_ui_request / extension_ui_response).
 *
 * Decision semantics (任务书 §2):
 *   allow    -> let the tool run
 *   deny     -> block the tool; the model receives an error tool result
 *   cancel / dialog timeout / lost connection -> treated as deny
 *
 * An approval is only valid for the request and parameter snapshot it was
 * issued for. Because `emitToolCall` may mutate `event.input` in place between
 * handlers, the snapshot is taken before the dialog and re-compared after it:
 * a change invalidates the approval and re-asks (blocking on re-ask failure).
 *
 * A command is registered so a host can detect that approval is actually in
 * force: if the extension failed to load, the command is absent and the host
 * must report "approval unavailable" instead of assuming protection.
 */

import type { ExtensionAPI, ExtensionContext, ToolCallEvent, ToolCallEventResult } from "@earendil-works/pi-coding-agent";

import { Policy } from "./policy.js";

const SELF = "fufucode-bridge";

function summarize(toolName: string, input: Record<string, unknown>): string {
	const one = (key: string, limit = 220): string => {
		const value = input[key];
		if (value === undefined) return "";
		const text = typeof value === "string"
			? value
			: (() => {
				try {
					return JSON.stringify(value);
				}
				catch {
					return String(value);
				}
			})();
		return text.length > limit ? `${text.slice(0, limit)}…` : text;
	};

	switch (toolName) {
		case "write":
			return `path: ${one("path")}\ncontent:\n${one("content", 600)}`;
		case "edit":
			return `path: ${one("path")}\nedits: ${one("edits", 400)}`;
		case "bash":
		case "powershell":
			return `command: ${one("command", 400)}`;
		case "read":
			return `path: ${one("path")}`;
		case "grep":
			return `pattern: ${one("pattern")}`;
		case "find":
			return `pattern: ${one("pattern")}`;
		case "ls":
			return `path: ${one("path")}`;
		default:
			return Object.keys(input).length > 0 ? JSON.stringify(input, null, 2).slice(0, 600) : "(no arguments)";
	}
}

export default function bridgeExtension(pi: ExtensionAPI) {
	const policy = new Policy();
	let executionMode: "manual" | "auto" | "full" | "plan" = "manual";
	pi.registerCommand("fufucode-bridge.mode", {
		description: "Set Fufucode execution policy: manual, auto, full, plan",
		handler: async (args, ctx) => {
			const mode = args.trim();
			if (!["manual", "auto", "full", "plan"].includes(mode)) throw new Error("Unknown Fufucode execution mode");
			executionMode = mode as typeof executionMode;
			ctx.ui.notify(`${SELF}: executionMode=${executionMode}`, "info");
		},
	});

	// P2-16: a failed custom-policy parse previously fell back silently. Emit a
	// diagnostic once per session so a user never believes a broken config is in
	// force. Safety direction is unchanged (fallback stays default confirm).
	let fallbackReported = false;
	const reportFallback = (ctx: ExtensionContext): void => {
		if (fallbackReported || policy.diagnostics.length === 0 || !ctx.hasUI) return;
		fallbackReported = true;
		for (const msg of policy.diagnostics) {
			ctx.ui.notify(`fufucode-bridge: ${msg}`, "warning");
		}
	};

	pi.on("session_start", async (_event, ctx: ExtensionContext) => {
		reportFallback(ctx);
	});

	// Liveness probe (A03): a host queries `get_commands`; absence of this
	// command means the extension is not loaded and approval is NOT in force.
	pi.registerCommand("fufucode-bridge.status", {
		description: "Report Fufucode bridge approval status",
		handler: async (_args, ctx: ExtensionContext) => {
			reportFallback(ctx);
			ctx.ui.notify(
				`${SELF}: active (mode=${ctx.mode}, hasUI=${ctx.hasUI ? "true" : "false"}, timeout=${policy.timeoutMs}ms)`,
				"info",
			);
		},
	});

	pi.on("tool_call", async (event: ToolCallEvent, ctx: ExtensionContext): Promise<ToolCallEventResult | void> => {
		reportFallback(ctx);
		const toolName = event.toolName;
		const decision = executionMode === "plan" ? (["read", "grep", "find", "ls"].includes(toolName) ? "allow" : "deny")
			: executionMode === "full" ? "allow"
			: executionMode === "auto" && ["write", "edit"].includes(toolName) && policy.decide(toolName) !== "deny" ? "allow"
			: policy.decide(toolName);

		if (decision === "allow") {
			return;
		}
		if (decision === "deny") {
			return { block: true, reason: `${SELF}: tool '${toolName}' is denied by policy` };
		}

		// Fail-safe without a dialog-capable UI: block rather than execute silently.
		if (!ctx.hasUI) {
			return {
				block: true,
				reason: `${SELF}: approval required for '${toolName}' but no UI is available`,
			};
		}

		// Parameter snapshot taken *before* awaiting the dialog. tool_call handlers
		// run serially and can mutate event.input in place, so a later handler may
		// change arguments after an approval has already been issued.
		const snapshot = JSON.stringify(event.input);

		const ask = async (): Promise<boolean> => {
			try {
				const confirmed = await ctx.ui.confirm(
					`${SELF}: allow tool '${toolName}'?`,
					summarize(toolName, event.input),
					policy.timeoutMs > 0 ? { timeout: policy.timeoutMs } : {},
				);
				return confirmed === true;
			}
			catch {
				// Any RPC failure counts as rejection, never as approval.
				return false;
			}
		};

		let allowed = await ask();

		if (allowed && JSON.stringify(event.input) !== snapshot) {
			// Arguments changed under the issued approval: re-ask, block on doubt.
			allowed = (await ask()) && JSON.stringify(event.input) === snapshot;
		}

		if (allowed) {
			return;
		}
		return {
			block: true,
			reason: `${SELF}: approval denied for '${toolName}'`,
		};
	});
}
