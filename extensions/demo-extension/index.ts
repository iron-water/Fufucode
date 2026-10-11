/**
 * Fufucode M5 demo extension.
 *
 * Purpose: exercise the full extension_ui_request surface in RPC mode so the
 * host contract (which methods need a response, field semantics, timeout
 * behaviour) can be pinned down for the desktop shell and the Core adapter.
 *
 * Contents:
 *  - `demo_write_note` tool: writes a file under the session cwd. Not listed in
 *    the bridge policy, so when fufucode-bridge is loaded the call is gated by
 *    an approval dialog (unknown tool -> confirm).
 *  - `/demo.ui-tour` command: walks the whole ctx.ui surface in order
 *    (fire-and-forget first, then dialogs) and saves every result to
 *    `ui-tour-result.json` in the session cwd.
 *
 * Dialog timeout: `/demo.ui-tour <ms>` or env FUFUCODE_DEMO_TIMEOUT_MS
 * (default 15000). Note: editor does not accept a timeout field in RPC mode —
 * the request record simply has no `timeout` key and the pending request waits
 * until the host responds.
 */

import * as fs from "node:fs";
import * as path from "node:path";

import { Type } from "@earendil-works/pi-ai";
import { defineTool, type ExtensionAPI, type ExtensionContext } from "@earendil-works/pi-coding-agent";

const demoWriteNote = defineTool({
	name: "demo_write_note",
	label: "Demo Write Note",
	description: "Write a short text note to a file under the current workspace. Used to demonstrate approval gating of custom tools.",
	promptSnippet: "demo_write_note <path> <content>: write a small note file (gated by the approval bridge)",
	parameters: Type.Object({
		path: Type.String({ description: "File path, relative to the workspace or absolute" }),
		content: Type.String({ description: "Text content to write" }),
	}),
	async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
		const target = path.isAbsolute(params.path) ? params.path : path.join(ctx.cwd, params.path);
		fs.mkdirSync(path.dirname(target), { recursive: true });
		fs.writeFileSync(target, params.content, "utf8");
		return {
			content: [{ type: "text", text: `demo_write_note: wrote ${target}` }],
			details: { path: target, bytes: params.content.length },
		};
	},
});

function parseTimeoutMs(args: string): number {
	const fromArgs = Number(args.trim());
	if (Number.isFinite(fromArgs) && fromArgs >= 0 && args.trim() !== "") return fromArgs;
	const fromEnv = Number(process.env.FUFUCODE_DEMO_TIMEOUT_MS ?? "");
	if (Number.isFinite(fromEnv) && fromEnv >= 0) return fromEnv;
	return 15_000;
}

async function tour(ctx: ExtensionContext, timeoutMs: number) {
	const results: Record<string, unknown> = {
		mode: ctx.mode,
		hasUI: ctx.hasUI,
		timeoutMs,
	};

	if (!ctx.hasUI) {
		results.error = "no UI available";
		return results;
	}

	const dialogOpts = timeoutMs > 0 ? { timeout: timeoutMs } : {};

	// --- Fire-and-forget surface (no response expected) ---
	ctx.ui.notify("demo.ui-tour started", "info");
	ctx.ui.setStatus("demoStatus", "touring");
	ctx.ui.setWidget("demoWidget", ["demo widget line 1", "demo widget line 2"]);
	ctx.ui.setTitle("fufucode demo tour");
	ctx.ui.setEditorText("// demo set_editor_text payload");
	results.fired = ["notify", "setStatus", "setWidget", "setTitle", "setEditorText"];

	// --- Dialog surface ---
	results.select = await ctx.ui.select("Pick one", ["apple", "banana", "cherry"], dialogOpts);
	results.confirm = await ctx.ui.confirm("Confirm?", "Do you want to proceed?", dialogOpts);
	results.input = await ctx.ui.input("Enter text", "placeholder text", dialogOpts);
	// editor: no timeout support in RPC mode — host must always answer.
	results.editor = await ctx.ui.editor("Edit something", "prefilled content");

	ctx.ui.notify("demo.ui-tour finished", "info");
	return results;
}

export default function demoExtension(pi: ExtensionAPI) {
	pi.registerTool(demoWriteNote);

	pi.registerCommand("demo.ui-tour", {
		description: "Walk the full ctx.ui surface and persist every result to ui-tour-result.json",
		handler: async (args, ctx) => {
			const timeoutMs = parseTimeoutMs(args);
			const results = await tour(ctx, timeoutMs);
			const out = path.join(ctx.cwd, "ui-tour-result.json");
			fs.writeFileSync(out, JSON.stringify(results, null, 2), "utf8");
			if (ctx.hasUI) {
				ctx.ui.notify(`demo.ui-tour results saved to ${out}`, "info");
			}
		},
	});
}
