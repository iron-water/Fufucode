/**
 * M5 verification driver for the demo extension.
 *
 * Drives real Pi 0.87.1 in RPC mode with an isolated agent dir + local stub
 * model. Asserts on real filesystem artifacts and protocol frames, not just
 * emitted messages.
 *
 * Scenarios:
 *   tool-allow / tool-deny     custom tool gated by fufucode-bridge (approval chain)
 *   command-tour-answer        all dialogs answered -> tour result file written
 *   command-tour-cancel        dialogs cancelled -> result shows undefined/null
 *   command-tour-timeout       dialogs time out (select/confirm/input); editor has
 *                              no timeout in RPC, host must still answer it
 *   fire-forget-arrive         notify/setStatus/setWidget/setTitle/set_editor_text frames
 *   dead-lock-free             after timeout, other RPC commands still respond
 *
 * Usage: node run.mjs [scenario|all]
 */

import { mkdtempSync, writeFileSync, existsSync, mkdirSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

import { PiRpc } from "../bridge-verify/lib/pi-rpc.mjs";
import { startStubModel } from "../bridge-verify/lib/stub-model.mjs";
import { assertRuntime, resolveRepoRoot, runtimePaths } from "../bridge-verify/lib/repo-root.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));

// P2-11: derive repo root from this script's location (scripts/demo-verify ->
// repo root), so the suite runs against the extensions of whichever worktree
// it lives in. FFF_PROJECT_ROOT / FFF_PI_RUNTIME are explicit overrides.
const REPO_ROOT = resolveRepoRoot(import.meta.url);
const { nodeExe: NODE_EXE, cliJs: CLI_JS } = runtimePaths(REPO_ROOT);
assertRuntime({ NODE_EXE, CLI_JS }, REPO_ROOT);
const DEMO_EXT = join(REPO_ROOT, "extensions", "demo-extension");
const BRIDGE_EXT = join(REPO_ROOT, "extensions", "fufucode-bridge");
const ATRIA_EXT = join(REPO_ROOT, "extensions", "atria-widget-lab");
const BROKEN_EXT = join(REPO_ROOT, "extensions", "broken-fixture");
const MODEL_ID = "fff-stub/fff-stub-model";

const SETTLE_TIMEOUT_MS = 120000;

function makeWorkspace(tag) {
	const base = mkdtempSync(join(tmpdir(), `fff-demo-${tag}-`));
	for (const dir of ["agent", "project"]) mkdirSync(join(base, dir), { recursive: true });
	return { base, agentDir: join(base, "agent"), projectDir: join(base, "project") };
}

function writeModelsJson(agentDir, port) {
	writeFileSync(join(agentDir, "models.json"), JSON.stringify({
		providers: {
			"fff-stub": {
				baseUrl: `http://127.0.0.1:${port}`,
				apiKey: "stub-key",
				api: "openai-completions",
				models: [{ id: "fff-stub-model", name: "FFF Stub Model" }],
			},
		},
	}, null, 2));
}

async function startPi({ workspace, port, extensions }) {
	const args = ["--mode", "rpc", "--no-session", "--model", MODEL_ID];
	for (const ext of extensions) args.push("--extension", ext);
	const rpc = new PiRpc({
		nodeExe: NODE_EXE,
		cliJs: CLI_JS,
		args,
		cwd: workspace.projectDir,
		env: { PI_CODING_AGENT_DIR: workspace.agentDir },
	});
	await rpc.send("get_commands", {}, 30000); // readiness probe
	return rpc;
}

function hasCommand(commands, name) {
	return (commands?.commands ?? []).some((c) => c.name === name);
}

async function runTurn(rpc, message) {
	const settled = rpc.waitForEvent((e) => e.type === "agent_settled", SETTLE_TIMEOUT_MS);
	await rpc.send("prompt", { message });
	await settled;
}

// ---------------------------------------------------------------------------

async function toolGated({ approve }) {
	const tag = approve ? "tool-allow" : "tool-deny";
	const ws = makeWorkspace(tag);
	const target = join(ws.projectDir, "note.txt");
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "demo_write_note", args: { path: "note.txt", content: "hello from demo tool" } },
		{ type: "text", text: "Done." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [BRIDGE_EXT, DEMO_EXT] });
	try {
		const commands = await rpc.send("get_commands");
		const customToolPresent = hasCommand(commands, "demo.ui-tour");
		rpc.setUiPolicy(() => ({ confirmed: approve }));
		await runTurn(rpc, "Write a note using demo_write_note.");
		const written = existsSync(target);
		const confirms = rpc.uiRequests.filter((r) => r.method === "confirm");
		return {
			scenario: tag,
			passed: written === approve && confirms.length >= 1 && customToolPresent,
			details: { written, approved: approve, confirmRequests: confirms.length, commandListed: customToolPresent },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function tourScenario({ mode, timeoutMs }) {
	// mode: "answer" | "cancel" | "timeout"
	const tag = `command-tour-${mode}`;
	const ws = makeWorkspace(tag);
	const resultFile = join(ws.projectDir, "ui-tour-result.json");
	const stub = await startStubModel({ port: 0, script: [{ type: "text", text: "Idle." }] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [DEMO_EXT] });
	try {
		const expectedMethods = ["select", "confirm", "input", "editor"];
		if (mode === "answer") {
			rpc.setUiPolicy((req) => {
				switch (req.method) {
					case "select": return { value: "banana" };
					case "confirm": return { confirmed: true };
					case "input": return { value: "typed text" };
					case "editor": return { value: "edited content" };
					default: return {};
				}
			});
		}
		else if (mode === "cancel") {
			rpc.setUiPolicy(() => ({ cancelled: true }));
		}
		else {
			// timeout: answer nothing; the extension's own timeout resolves
			// select/confirm/input. editor has NO timeout -> host must still answer;
			// send cancelled after the other dialogs have resolved.
			rpc.setUiPolicy(() => undefined);
			rpc.on("ui_request", (req) => {
				if (req.method === "editor") {
					setTimeout(() => rpc.answerUi(req.id, { cancelled: true }), 4000);
				}
			});
		}

		// Command execution: /demo.ui-tour is invoked via prompt (extension
		// commands execute immediately when the message starts with "/").
		await rpc.send("prompt", { message: `/demo.ui-tour ${timeoutMs ?? ""}`.trim() });

		// Wait until the tour finishes: result file written.
		const deadline = Date.now() + SETTLE_TIMEOUT_MS;
		while (!existsSync(resultFile) && Date.now() < deadline) {
			await new Promise((r) => setTimeout(r, 250));
		}
		if (!existsSync(resultFile)) {
			throw new Error("ui-tour-result.json was not written within deadline");
		}
		const results = JSON.parse(readFileSync(resultFile, "utf8"));
		const dialogRequests = rpc.uiRequests.filter((r) => expectedMethods.includes(r.method));
		const methodsSeen = dialogRequests.map((r) => r.method);
		const timeoutFieldOn = (m) => dialogRequests.find((r) => r.method === m)?.timeout;

		const checks = {
			allFourDialogs: expectedMethods.every((m) => methodsSeen.includes(m)),
			selectHasTimeoutField: timeoutMs ? typeof timeoutFieldOn("select") === "number" : true,
			editorHasNoTimeoutField: dialogRequests.find((r) => r.method === "editor")?.timeout === undefined,
		};

		let resultChecks;
		if (mode === "answer") {
			resultChecks = results.select === "banana" && results.confirm === true
				&& results.input === "typed text" && results.editor === "edited content";
		}
		else if (mode === "cancel") {
			resultChecks = results.confirm === false
				&& !("select" in results) && !("input" in results) && !("editor" in results);
		}
		else {
			resultChecks = results.confirm === false
				&& !("select" in results) && !("input" in results) && !("editor" in results);
		}

		return {
			scenario: tag,
			passed: Object.values(checks).every(Boolean) && resultChecks,
			details: { ...checks, results, methodsSeen },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function fireForgetArrive() {
	const ws = makeWorkspace("fire-forget");
	const stub = await startStubModel({ port: 0, script: [{ type: "text", text: "Idle." }] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [DEMO_EXT] });
	try {
		rpc.setUiPolicy(() => ({ cancelled: true }));
		await rpc.send("prompt", { message: "/demo.ui-tour 2000" });
		const deadline = Date.now() + 30000;
		const wanted = ["notify", "setStatus", "setWidget", "setTitle", "set_editor_text"];
		const deadlineCheck = () => wanted.every((m) => rpc.uiRequests.some((r) => r.method === m));
		while (!deadlineCheck() && Date.now() < deadline) {
			await new Promise((r) => setTimeout(r, 100));
		}
		const byMethod = Object.fromEntries(wanted.map((m) => [m, rpc.uiRequests.find((r) => r.method === m)]));
		return {
			scenario: "fire-forget-arrive",
			passed: deadlineCheck(),
			details: {
				notify: { message: byMethod.notify?.message, notifyType: byMethod.notify?.notifyType },
				setStatus: { statusKey: byMethod.setStatus?.statusKey, statusText: byMethod.setStatus?.statusText },
				setWidget: { widgetKey: byMethod.setWidget?.widgetKey, widgetLines: byMethod.setWidget?.widgetLines, placement: byMethod.setWidget?.widgetPlacement },
				setTitle: { title: byMethod.setTitle?.title },
				setEditorText: { text: byMethod.set_editor_text?.text },
			},
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function deadLockFree() {
	const ws = makeWorkspace("deadlock");
	const stub = await startStubModel({ port: 0, script: [{ type: "text", text: "Idle." }] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [DEMO_EXT] });
	try {
		// Never answer any dialog: select/confirm/input rely on the extension
		// timeout; editor stays pending forever unless the host answers.
		rpc.setUiPolicy(() => undefined);
		// NOTE: the prompt response is emitted only AFTER the command handler
		// finishes (preflightResult fires post-handler). With editor pending we
		// must not block on it — fire it and await at the end.
		const promptDone = rpc.send("prompt", { message: "/demo.ui-tour 1500" });
		// Wait for the editor request to arrive (the preceding three must have timed out first).
		const editorReq = await rpc.waitForEvent(
			(e) => e.type === "extension_ui_request" && e.method === "editor",
			30000,
		);
		// While the editor dialog is still pending, other RPC commands must respond.
		const side = await rpc.send("get_commands", {}, 10000);
		rpc.answerUi(editorReq.id, { cancelled: true });
		await promptDone;
		return {
			scenario: "dead-lock-free",
			passed: !!side,
			details: { sideCommands: (side?.commands ?? []).length, editorId: editorReq.id },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

// ---------------------------------------------------------------------------
// Fixtures for the M5 management page (docs/archive/assignments/track-b-fixtures.md)
// ---------------------------------------------------------------------------

async function fixtureAtria({ gated }) {
	const tag = gated ? "fixture-atria-gated" : "fixture-atria";
	const ws = makeWorkspace(tag);
	const snippetSrc = join(ws.projectDir, "snippet.txt");
	writeFileSync(snippetSrc, "line1\nline2\nline3\nline4\nline5\nline6\nline7\n");
	const resultFile = join(ws.projectDir, "atria-tour-result.json");
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "atria_read_snippet", args: { path: "snippet.txt", lines: 3 } },
		{ type: "text", text: "Done." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const extensions = gated ? [BRIDGE_EXT, ATRIA_EXT] : [ATRIA_EXT];
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions });
	try {
		const commands = await rpc.send("get_commands");
		const listed = ["atria-lab.tour", "atria-lab.status"].every((n) => hasCommand(commands, n));

		// dialogs auto-answer
		rpc.setUiPolicy((req) => {
			switch (req.method) {
				case "select": return { value: "green" };
				case "confirm": return { confirmed: true };
				case "input": return { value: "atria input" };
				case "editor": return { value: "// edited" };
				default: return {};
			}
		});

		// 1) tour command drives the UI surface and writes the result file
		const tourPromise = rpc.send("prompt", { message: "/atria-lab.tour 8000" });
		const deadline = Date.now() + SETTLE_TIMEOUT_MS;
		while (!existsSync(resultFile) && Date.now() < deadline) {
			await new Promise((r) => setTimeout(r, 200));
		}
		await tourPromise;
		const tourOk = existsSync(resultFile)
			&& JSON.parse(readFileSync(resultFile, "utf8")).select === "green";

		// 2) tool call executes (or is gated by bridge when gated=true)
		const toolEnd = rpc.waitForEvent((e) => e.type === "tool_execution_end" && e.toolName === "atria_read_snippet", 60000);
		await runTurn(rpc, "Read snippet.txt with atria_read_snippet.");
		const end = await toolEnd.catch(() => null);
		const toolOk = !!end && !end.isError;

		const statusFrames = rpc.uiRequests.filter((r) => r.method === "setStatus");
		const sessionStartSignal = statusFrames.some((r) => r.statusKey === "atriaWidgetLab");
		const confirmGated = rpc.uiRequests.filter((r) => r.method === "confirm").length;

		return {
			scenario: tag,
			passed: listed && tourOk && toolOk && (gated ? confirmGated >= 1 : confirmGated === 0),
			details: { listed, tourOk, toolOk, gated, confirmRequests: confirmGated, sessionStartSignal, statusFrames: statusFrames.length },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function fixtureBroken() {
	const ws = makeWorkspace("fixture-broken");
	const stub = await startStubModel({ port: 0, script: [{ type: "text", text: "Idle." }] });
	writeModelsJson(ws.agentDir, stub.port);
	// bypass startPi's ready-probe: broken extension makes Pi exit before RPC loop
	const args = ["--mode", "rpc", "--no-session", "--model", MODEL_ID, "--extension", BROKEN_EXT];
	const rpc = new PiRpc({
		nodeExe: NODE_EXE,
		cliJs: CLI_JS,
		args,
		cwd: ws.projectDir,
		env: { PI_CODING_AGENT_DIR: ws.agentDir },
	});
	try {
		const exit = await new Promise((resolve) => rpc.child.on("exit", (code) => resolve(code)));
		const diag = rpc.stderrText;
		const sawError = diag.includes("Failed to load extension") && diag.includes("broken-fixture");
		return {
			scenario: "fixture-broken",
			passed: exit === 1 && sawError,
			details: { exitCode: exit, sawLoadError: sawError, stderrSnippet: diag.slice(0, 300) },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

// ---------------------------------------------------------------------------

const SCENARIOS = {
	"tool-allow": () => toolGated({ approve: true }),
	"tool-deny": () => toolGated({ approve: false }),
	"command-tour-answer": () => tourScenario({ mode: "answer", timeoutMs: 15000 }),
	"command-tour-cancel": () => tourScenario({ mode: "cancel", timeoutMs: 0 }),
	"command-tour-timeout": () => tourScenario({ mode: "timeout", timeoutMs: 1500 }),
	"fire-forget-arrive": fireForgetArrive,
	"dead-lock-free": deadLockFree,
	"fixture-atria": () => fixtureAtria({ gated: false }),
	"fixture-atria-gated": () => fixtureAtria({ gated: true }),
	"fixture-broken": fixtureBroken,
};

function log(result) {
	const status = result.passed ? "PASS" : "FAIL";
	console.log(`[${status}] ${result.scenario}`);
	console.log(`        ${JSON.stringify(result.details)}`);
	if (result.error) console.log(`        error: ${result.error}`);
}

async function main() {
	const arg = process.argv[2] ?? "all";
	const names = arg === "all" ? Object.keys(SCENARIOS) : [arg];
	for (const n of names) {
		if (!(n in SCENARIOS)) {
			console.error(`unknown scenario: ${n}; known: ${Object.keys(SCENARIOS).join(", ")}`);
			process.exit(2);
		}
	}
	const results = [];
	for (const name of names) {
		try {
			results.push(await SCENARIOS[name]());
		}
		catch (err) {
			results.push({ scenario: name, passed: false, error: err.message, details: {} });
		}
	}
	for (const r of results) log(r);
	const report = join(__dirname, "demo-verify-results.json");
	writeFileSync(report, JSON.stringify(results, null, 2));
	console.log(`\nresults written: ${report}`);
	process.exit(results.every((r) => r.passed) ? 0 : 1);
}

main();
