/**
 * Verifies the Fufucode approval bridge against a real Pi agent loop.
 *
 * Scenarios A01/A02/A03 cover allow, deny and fail-closed paths.
 *
 * Each scenario:
 *   - builds an isolated workspace (agent dir via PI_CODING_AGENT_DIR)
 *   - starts a local OpenAI-compatible stub model
 *   - spawns real Pi in RPC mode (`--no-session -e <extension>`)
 *   - drives a prompt, auto-answers extension_ui_request dialogs per scenario
 *   - asserts on real filesystem state and protocol frames
 *
 * Usage: node run.mjs <scenario>    scenarios listed in SCENARIOS below
 *        node run.mjs all
 */

import { mkdtempSync, writeFileSync, existsSync, rmSync, mkdirSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

import { PiRpc } from "./lib/pi-rpc.mjs";
import { startStubModel } from "./lib/stub-model.mjs";
import { assertRuntime, resolveRepoRoot, runtimePaths } from "./lib/repo-root.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));

// P2-11: derive repo root from this script's location (scripts/bridge-verify ->
// repo root), so the suite runs against the extensions of whichever worktree
// it lives in. FFF_PROJECT_ROOT / FFF_PI_RUNTIME are explicit overrides.
const REPO_ROOT = resolveRepoRoot(import.meta.url);
const { nodeExe: NODE_EXE, cliJs: CLI_JS } = runtimePaths(REPO_ROOT);
assertRuntime({ NODE_EXE, CLI_JS }, REPO_ROOT);
const EXTENSION = join(REPO_ROOT, "extensions", "fufucode-bridge");
const MODEL_ID = "fff-stub/fff-stub-model";

const AGENT_SETTLE_TIMEOUT_MS = 90000;

function makeWorkspace(tag) {
	const base = mkdtempSync(join(tmpdir(), `fff-bridge-${tag}-`));
	for (const dir of ["agent", "project", "evidence"]) {
		mkdirSync(join(base, dir), { recursive: true });
	}
	return { base, agentDir: join(base, "agent"), projectDir: join(base, "project"), evidenceDir: join(base, "evidence") };
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

function writeShortTimeoutPolicy(path) {
	// Reuses the bridge's own config format (policy.ts): defaults + short confirm timeout.
	writeFileSync(path, JSON.stringify({ timeoutMs: 1500 }, null, 2));
}

function writeBrokenExtension(path) {
	writeFileSync(path, "throw new Error('intentional load failure for A03');\n");
}

async function startPi({ workspace, port, extensions, extraEnv = {} }) {
	const args = ["--mode", "rpc", "--no-session", "--model", MODEL_ID];
	for (const ext of extensions) {
		args.push("--extension", ext);
	}
	const rpc = new PiRpc({
		nodeExe: NODE_EXE,
		cliJs: CLI_JS,
		args,
		cwd: workspace.projectDir,
		env: {
			PI_CODING_AGENT_DIR: workspace.agentDir,
			...extraEnv,
		},
	});
	return rpc;
}

async function awaitReady(rpc) {
	// get_commands doubles as the readiness + A03 liveness probe.
	try {
		return await rpc.send("get_commands", {}, 30000);
	}
	catch (err) {
		rpc.kill();
		throw new Error(`pi did not become ready: ${err.message}`);
	}
}

async function runTurn(rpc, message) {
	const settled = rpc.waitForEvent((e) => e.type === "agent_settled", AGENT_SETTLE_TIMEOUT_MS);
	await rpc.send("prompt", { message });
	await settled;
}

function bridgeCommandPresent(getCommandsData) {
	const names = (getCommandsData?.commands ?? []).map((c) => c.name);
	return names.includes("fufucode-bridge.status");
}

// ---------------------------------------------------------------------------
// Scenarios
// ---------------------------------------------------------------------------

async function a01Allow() {
	const ws = makeWorkspace("a01-allow");
	const target = join(ws.projectDir, "out.txt");
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "write", args: { path: target, content: "written by stub model" } },
		{ type: "text", text: "Done writing the file." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [EXTENSION] });
	try {
		await awaitReady(rpc);
		rpc.setUiPolicy(() => ({ confirmed: true }));
		await runTurn(rpc, "Write the file out.txt.");
		const executed = existsSync(target);
		return {
			scenario: "a01-allow",
			passed: executed && rpc.uiRequests.length >= 1,
			details: { target, executed, uiRequests: rpc.uiRequests.length },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function a01Deny() {
	const ws = makeWorkspace("a01-deny");
	const target = join(ws.projectDir, "out.txt");
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "write", args: { path: target, content: "should not be written" } },
		{ type: "text", text: "Could not write." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [EXTENSION] });
	try {
		await awaitReady(rpc);
		rpc.setUiPolicy(() => ({ confirmed: false }));
		await runTurn(rpc, "Write the file out.txt.");
		const written = existsSync(target);
		const blockedFrames = rpc.uiRequests.filter((r) => r.method === "confirm").length;
		return {
			scenario: "a01-deny",
			passed: !written && blockedFrames >= 1,
			details: { target, written, confirmRequests: blockedFrames },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function a01Timeout() {
	const ws = makeWorkspace("a01-timeout");
	const target = join(ws.projectDir, "out.txt");
	const policyPath = join(ws.agentDir, "bridge-policy.json");
	writeShortTimeoutPolicy(policyPath);
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "write", args: { path: target, content: "should not be written" } },
		{ type: "text", text: "Could not write." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({
		workspace: ws,
		port: stub.port,
		extensions: [EXTENSION],
		extraEnv: { FUFUCODE_BRIDGE_CONFIG: policyPath },
	});
	try {
		await awaitReady(rpc);
		rpc.setUiPolicy(() => undefined); // never answer: let the dialog time out
		await runTurn(rpc, "Write the file out.txt.");
		const written = existsSync(target);
		return {
			scenario: "a01-timeout",
			passed: !written && rpc.uiRequests.length >= 1,
			details: { target, written, confirmRequests: rpc.uiRequests.length },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function policyFallback() {
	// P2-16: a malformed custom policy must produce a diagnostic notify and
	// fall back to the default confirm policy (behavior unchanged).
	const ws = makeWorkspace("policy-fallback");
	const target = join(ws.projectDir, "out.txt");
	const badPolicy = join(ws.agentDir, "bad-policy.json");
	writeFileSync(badPolicy, "{ this is not valid json ]");
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "write", args: { path: target, content: "should not be written" } },
		{ type: "text", text: "Done." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({
		workspace: ws,
		port: stub.port,
		extensions: [EXTENSION],
		extraEnv: { FUFUCODE_BRIDGE_CONFIG: badPolicy },
	});
	try {
		await awaitReady(rpc);
		rpc.setUiPolicy(() => ({ confirmed: false }));
		await runTurn(rpc, "Write the file out.txt.");
		const written = existsSync(target);
		const notifies = rpc.uiRequests.filter((r) => r.method === "notify");
		const fallbackNotify = notifies.find((r) => (r.message ?? "").includes("自定义策略解析失败") && r.notifyType === "warning");
		const confirms = rpc.uiRequests.filter((r) => r.method === "confirm").length;
		return {
			scenario: "policy-fallback",
			passed: !written && !!fallbackNotify && confirms >= 1,
			details: { written, fallbackNotify: fallbackNotify?.message ?? null, confirmRequests: confirms, notifyCount: notifies.length },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function a01BashDeny() {
	const ws = makeWorkspace("a01-bash");
	const marker = join(ws.projectDir, "marker.txt");
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "bash", args: { command: `echo executed > ${marker.replace(/\\/g, "/")}` } },
		{ type: "text", text: "Could not run the command." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [EXTENSION] });
	try {
		await awaitReady(rpc);
		rpc.setUiPolicy(() => ({ confirmed: false }));
		await runTurn(rpc, "Run the echo command.");
		const executed = existsSync(marker);
		const confirmFrames = rpc.uiRequests.filter((r) => r.method === "confirm").length;
		return {
			scenario: "a01-bash-deny",
			passed: !executed && confirmFrames >= 1,
			details: { marker, executed, confirmRequests: confirmFrames },
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function a02Nonblocking() {
	const ws = makeWorkspace("a02");
	const target = join(ws.projectDir, "out.txt");
	const stub = await startStubModel({ port: 0, script: [
		{ type: "tool", name: "write", args: { path: target, content: "deferred write" } },
		{ type: "text", text: "Done." },
	] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [EXTENSION] });
	try {
		await awaitReady(rpc);
		// Hold the approval dialog open: answer only after the side probe completes.
		let releaseDialog;
		const held = new Promise((resolve) => { releaseDialog = resolve; });
		const confirmArrived = rpc.waitForEvent((e) => e.type === "extension_ui_request" && e.method === "confirm", 60000);
		rpc.setUiPolicy(() => undefined);
		const turn = runTurn(rpc, "Write the file out.txt.");
		await confirmArrived;

		// Side probe: a normal RPC command must be answered while the approval is pending.
		const sideResponse = await rpc.send("get_commands", {}, 15000);

		releaseDialog();
		const req = rpc.uiRequests.find((r) => r.method === "confirm");
		rpc.answerUi(req.id, { confirmed: false });
		await turn;

		return {
			scenario: "a02-nonblocking",
			passed: !!sideResponse && bridgeCommandPresent(sideResponse),
			details: {
				sideResponseCommands: (sideResponse?.commands ?? []).length,
				confirmRequests: rpc.uiRequests.length,
			},
			evidence: ws.base,
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function a03Missing() {
	const ws = makeWorkspace("a03-missing");
	const stub = await startStubModel({ port: 0, script: [{ type: "text", text: "Idle." }] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [] });
	try {
		const commands = await awaitReady(rpc);
		const present = bridgeCommandPresent(commands);
		return {
			scenario: "a03-missing",
			passed: !present,
			details: { bridgeCommandListed: present, commandCount: (commands?.commands ?? []).length },
			evidence: ws.base,
			reported: present ? "approval present" : "审批不可用 (bridge command not listed)",
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

async function a03Broken() {
	const ws = makeWorkspace("a03-broken");
	const broken = join(ws.base, "broken-ext.ts");
	writeBrokenExtension(broken);
	const stub = await startStubModel({ port: 0, script: [{ type: "text", text: "Idle." }] });
	writeModelsJson(ws.agentDir, stub.port);
	const rpc = await startPi({ workspace: ws, port: stub.port, extensions: [broken] });
	try {
		let commands;
		try {
			commands = await awaitReady(rpc);
		}
		catch (readyError) {
			// Pi treats a broken -e extension as fatal at startup: it exits with a
			// diagnostic on stderr instead of continuing with a soft error record.
			// The host cannot reach the approval gate, so approval is unavailable.
			return {
				scenario: "a03-broken",
				passed: /pi exited/.test(readyError.message) || /did not become ready/.test(readyError.message),
				details: { readyError: readyError.message, bridgeCommandListed: false },
				evidence: ws.base,
				reported: "审批不可用 (bridge failed to load; pi exited)",
			};
		}
		const present = bridgeCommandPresent(commands);
		return {
			scenario: "a03-broken",
			passed: !present,
			details: { bridgeCommandListed: present, commandCount: (commands?.commands ?? []).length },
			evidence: ws.base,
			reported: present ? "approval present" : "审批不可用 (bridge failed to load)",
		};
	}
	finally {
		rpc.kill();
		stub.close();
	}
}

// ---------------------------------------------------------------------------
// Runner
// ---------------------------------------------------------------------------

const SCENARIOS = {
	"a01-allow": a01Allow,
	"a01-deny": a01Deny,
	"a01-timeout": a01Timeout,
	"a01-bash-deny": a01BashDeny,
	"policy-fallback": policyFallback,
	"a02-nonblocking": a02Nonblocking,
	"a03-missing": a03Missing,
	"a03-broken": a03Broken,
};

function log(result) {
	const status = result.passed ? "PASS" : "FAIL";
	const extra = result.reported ? ` | ${result.reported}` : "";
	console.log(`[${status}] ${result.scenario}${extra}`);
	console.log(`        details: ${JSON.stringify(result.details)}`);
}

async function main() {
	const arg = process.argv[2] ?? "all";
	const names = arg === "all" ? Object.keys(SCENARIOS) : [arg];
	if (names.some((n) => !(n in SCENARIOS))) {
		console.error(`unknown scenario: ${arg}`);
		console.error(`known: ${Object.keys(SCENARIOS).join(", ")}`);
		process.exit(2);
	}
	const results = [];
	for (const name of names) {
		try {
			const result = await SCENARIOS[name]();
			log(result);
			results.push(result);
		}
		catch (err) {
			const result = { scenario: name, passed: false, error: err.message, details: {} };
			log(result);
			results.push(result);
		}
	}
const reportPath = join(dirname(fileURLToPath(import.meta.url)), "bridge-verify-results.json");
	writeFileSync(reportPath, JSON.stringify(results, null, 2));
	console.log(`\nresults written: ${reportPath}`);
	const failed = results.filter((r) => !r.passed);
	process.exit(failed.length === 0 ? 0 : 1);
}

main();
