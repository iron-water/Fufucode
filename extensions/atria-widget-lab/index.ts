/**
 * Atria Widget Lab — fixture extension for the M5 plugin management page.
 *
 * A feature-rich but harmless extension: registers a read-only tool, two
 * commands, and exercises the full non-blocking ctx.ui surface. Intended for
 * the management page's "normal multi-capability extension" display and
 * enable/disable round-trips.
 *
 * Contents:
 *  - `atria_read_snippet` tool (read-only: prints the first N lines of a file)
 *  - `/atria-lab.tour` command: walks the UI surface (select/input/editor +
 *    notify/setStatus/setWidget/setTitle/set_editor_text), persists results to
 *    `atria-tour-result.json` in the session cwd
 *  - `/atria-lab.status` command: reports extension state (presence in
 *    get_commands doubles as the management page's "loaded" observable)
 *  - `session_start` handler: emits a `setStatus` frame as the load signal
 *
 * Deliberately does NOT register a `confirm` dialog or mutate files — that
 * belongs to fufucode-bridge, not to this fixture.
 */

import * as fs from "node:fs";
import * as path from "node:path";

import { Type } from "@earendil-works/pi-ai";
import { defineTool, type ExtensionAPI, type ExtensionContext } from "@earendil-works/pi-coding-agent";

const readSnippet = defineTool({
	name: "atria_read_snippet",
	label: "Atria Read Snippet",
	description: "Read the first N lines of a text file. Read-only; used by the management page to exercise a harmless custom tool.",
	promptSnippet: "atria_read_snippet <path> [lines]: print the first N lines of a file",
	parameters: Type.Object({
		path: Type.String({ description: "File path, relative to the workspace or absolute" }),
		lines: Type.Optional(Type.Number({ description: "Max lines to read (default 5, max 50)" })),
	}),
	async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
		const target = path.isAbsolute(params.path) ? params.path : path.join(ctx.cwd, params.path);
		const maxLines = Math.min(Math.max(params.lines ?? 5, 1), 50);
		const text = fs.readFileSync(target, "utf8").split(/\r?\n/).slice(0, maxLines).join("\n");
		return {
			content: [{ type: "text", text }],
			details: { path: target, lines: maxLines },
		};
	},
});

async function tour(ctx: ExtensionContext, timeoutMs: number) {
	const results: Record<string, unknown> = {
		extension: "atria-widget-lab",
		mode: ctx.mode,
		hasUI: ctx.hasUI,
		timeoutMs,
	};
	if (!ctx.hasUI) {
		results.error = "no UI available";
		return results;
	}
	const dialogOpts = timeoutMs > 0 ? { timeout: timeoutMs } : {};

	ctx.ui.notify("atria-lab.tour started", "info");
	ctx.ui.setStatus("atriaLabTour", "touring");
	ctx.ui.setWidget("atriaLabWidget", ["atria widget: line A", "atria widget: line B"]);
	ctx.ui.setTitle("atria widget lab");
	ctx.ui.setEditorText("// atria set_editor_text payload");
	results.fired = ["notify", "setStatus", "setWidget", "setTitle", "setEditorText"];

	results.select = await ctx.ui.select("Atria pick one", ["red", "green", "blue"], dialogOpts);
	results.input = await ctx.ui.input("Atria enter text", "type here", dialogOpts);
	// editor: no timeout support in RPC mode — host must always answer.
	results.editor = await ctx.ui.editor("Atria editor", "// edit me");

	ctx.ui.setStatus("atriaLabTour", "done");
	ctx.ui.notify("atria-lab.tour finished", "info");
	return results;
}

export default function atriaWidgetLab(pi: ExtensionAPI) {
	pi.registerTool(readSnippet);

	// Load signal: lets the management page observe that the extension is alive.
	pi.on("session_start", async (_event, ctx: ExtensionContext) => {
		if (ctx.hasUI) {
			ctx.ui.setStatus("atriaWidgetLab", "ready");
		}
	});

	pi.registerCommand("atria-lab.tour", {
		description: "Walk the non-confirm ctx.ui surface; persist results to atria-tour-result.json",
		handler: async (args, ctx) => {
			const timeoutMs = Number(args.trim()) || 15_000;
			const results = await tour(ctx, timeoutMs);
			const out = path.join(ctx.cwd, "atria-tour-result.json");
			fs.writeFileSync(out, JSON.stringify(results, null, 2), "utf8");
			if (ctx.hasUI) ctx.ui.notify(`atria-lab.tour results saved to ${out}`, "info");
		},
	});

	pi.registerCommand("atria-lab.status", {
		description: "Report atria-widget-lab load state (management-page observable)",
		handler: async (_args, ctx) => {
			if (ctx.hasUI) {
				ctx.ui.notify(`atria-widget-lab: loaded (mode=${ctx.mode}, hasUI=${ctx.hasUI ? "true" : "false"})`, "info");
				ctx.ui.setStatus("atriaWidgetLab", "alive");
			}
		},
	});
}
