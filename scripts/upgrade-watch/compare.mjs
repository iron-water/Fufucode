/**
 * Pi 升级雷达：pinned 版本与上游版本的协议面差异对比。
 *
 * 任务来源：docs/archive/assignments/track-b-upgradewatch.md。只对比、只报告，不自动升级、
 * 不改 lockfile（计划 §3 红线）。
 *
 * 对比面：
 *  - RPC 命令清单（rpc-mode.js `case "…"`）与请求字段（rpc-types.d.ts 接口）
 *  - 事件类型清单（json-event.js 中 `type: "…"` 字面量 + docs/json.md）
 *  - extension_ui_request/response 子协议（rpc-mode.js 的 method 集合与
 *    timeout 支持）+ docs/rpc-extension-ui.md
 *  - session 格式版本（session-manager.js 的 CURRENT_SESSION_VERSION）
 *
 * 用法：
 *   node compare.mjs                          # 默认：runtime/pi 锁定版本 vs 最新版
 *   node compare.mjs --base runtime           # 基线 = runtime/pi 已安装树
 *   node compare.mjs --base 0.87.0 --target 0.87.1
 *   node compare.mjs --out report.md
 *
 * --base/--target 取值："runtime"（本仓库 runtime/pi）| npm 版本号 | 解包目录。
 * 报告分「破坏性 / 需评估 / 无影响」三档，每条附文件与行号证据。
 */

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, "..", "..");
const PKG = "@earendil-works/pi-coding-agent";
const REGISTRY_URL = `https://registry.npmjs.org/${encodeURIComponent(PKG)}`;

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

function parseArgs(argv) {
	const args = { base: "runtime", target: undefined, out: undefined };
	for (let i = 0; i < argv.length; i++) {
		const a = argv[i];
		if (a === "--base") args.base = argv[++i];
		else if (a === "--target") args.target = argv[++i];
		else if (a === "--out") args.out = argv[++i];
		else if (a === "--help" || a === "-h") { console.log("see file header"); process.exit(0); }
	}
	return args;
}

async function fetchJson(url) {
	const res = await fetch(url);
	if (!res.ok) throw new Error(`GET ${url} -> ${res.status}`);
	return res.json();
}

async function download(url, dest) {
	const res = await fetch(url);
	if (!res.ok) throw new Error(`GET ${url} -> ${res.status}`);
	writeFileSync(dest, Buffer.from(await res.arrayBuffer()));
}

function extractTgz(tgz, destDir) {
	mkdirSync(destDir, { recursive: true });
	// bsdtar is present on Windows 10+; package tarballs contain a top-level "package/" dir.
	execFileSync("tar.exe", ["-xzf", tgz, "-C", destDir], { stdio: "inherit" });
	return join(destDir, "package");
}

function sha(text) {
	return createHash("sha256").update(text).digest("hex").slice(0, 12);
}

/** Locate a file inside an installed package tree (dist/ first, then recursive). */
function findInPkg(root, rel) {
	const direct = join(root, rel);
	if (existsSync(direct)) return direct;
	// shallow recursive fallback
	const baseName = rel.split(/[\\/]/).pop();
	const hits = [];
	const walk = (dir, depth) => {
		if (depth > 6) return;
		for (const e of readdirSync(dir, { withFileTypes: true })) {
			if (e.name === "node_modules" || e.name.startsWith(".")) continue;
			const p = join(dir, e.name);
			if (e.isDirectory()) walk(p, depth + 1);
			else if (e.name === baseName) hits.push(p);
		}
	};
	walk(root, 0);
	return hits[0] ?? null;
}

function readFileOrNull(path) {
	try { return readFileSync(path, "utf8"); } catch { return null; }
}

// ---------------------------------------------------------------------------
// surface extraction
// ---------------------------------------------------------------------------

function extractSurfaces(pkgRoot) {
	const surfaces = { pkgRoot };

	const rpcMode = findInPkg(pkgRoot, "dist/modes/rpc/rpc-mode.js");
	const rpcTypes = findInPkg(pkgRoot, "dist/modes/rpc/rpc-types.d.ts");
	const sessionMgr = findInPkg(pkgRoot, "dist/core/session-manager.js");
	const rpcDoc = findInPkg(pkgRoot, "docs/rpc.md");
	const rpcCmdDoc = findInPkg(pkgRoot, "docs/rpc-commands.md");
	const uiDoc = findInPkg(pkgRoot, "docs/rpc-extension-ui.md");
	const jsonDoc = findInPkg(pkgRoot, "docs/json.md");
	const sessionDoc = findInPkg(pkgRoot, "docs/session-format.md");

	// --- RPC command list ---
	const rpcModeText = rpcMode ? readFileOrNull(rpcMode) : null;
	surfaces.rpcCommands = [];
	surfaces.rpcCommandsLoc = rpcMode?.replace(pkgRoot, "").replace(/\\/g, "/");
	if (rpcModeText) {
		for (const m of rpcModeText.matchAll(/case\s+"([a-z_]+)"\s*:/g)) {
			surfaces.rpcCommands.push(m[1]);
		}
	}

	// --- extension_ui_request methods (from rpc-mode.js output calls) ---
	surfaces.uiMethods = [];
	if (rpcModeText) {
		for (const m of rpcModeText.matchAll(/method:\s*"([a-zA-Z_]+)"/g)) {
			if (!surfaces.uiMethods.includes(m[1])) surfaces.uiMethods.push(m[1]);
		}
	}
	// timeout support per method: `timeout:` inside createDialogPromise request objects
	surfaces.uiTimeoutMethods = [];
	if (rpcModeText) {
		for (const m of rpcModeText.matchAll(/method:\s*"([a-zA-Z_]+)"[^}]*?timeout:\s*opts\?\.timeout/g)) {
			surfaces.uiTimeoutMethods.push(m[1]);
		}
	}

	// --- RPC command request fields (rpc-types.d.ts interfaces) ---
	const rpcTypesText = rpcTypes ? readFileOrNull(rpcTypes) : null;
	surfaces.rpcInterfaces = {};
	surfaces.rpcTypesLoc = rpcTypes?.replace(pkgRoot, "").replace(/\\/g, "/");
	if (rpcTypesText) {
		for (const m of rpcTypesText.matchAll(/export interface (\w+)\s*{([^}]+)}/g)) {
			const name = m[1];
			const fields = [...m[2].matchAll(/^\s*(?:readonly\s+)?(\w+)\??:/gm)].map((f) => f[1]);
			surfaces.rpcInterfaces[name] = fields;
		}
	}

	// --- event types ---
	// Two surfaces:
	//  - documented set: `"type": "..."` literals in docs/json.md
	//  - emission set: `type: "..."` literals at emit sites
	//    (agent-session.js + rpc-mode.js + json-event.js + json-mode.js).
	// json-event.js alone only contains a transform and undercounts; docs alone
	// lag the runtime (0.87.0 docs omitted agent_settled though it was emitted).
	surfaces.eventTypesDoc = new Set();
	const jsonText = jsonDoc ? readFileOrNull(jsonDoc) : null;
	if (jsonText) {
		for (const m of jsonText.matchAll(/"type"\s*:\s*"([a-z_]+)"/g)) surfaces.eventTypesDoc.add(m[1]);
	}
	surfaces.eventTypesDoc = [...surfaces.eventTypesDoc].sort();

	surfaces.eventTypesEmitted = new Set();
	const emitFiles = [
		"dist/core/agent-session.js",
		"dist/modes/rpc/rpc-mode.js",
		"dist/modes/json-event.js",
		"dist/modes/json-mode.js",
	];
	for (const rel of emitFiles) {
		const p = findInPkg(pkgRoot, rel);
		const t = p ? readFileOrNull(p) : null;
		if (t) for (const m of t.matchAll(/type:\s*"([a-z_]+)"/g)) surfaces.eventTypesEmitted.add(m[1]);
	}
	surfaces.eventTypesEmitted = [...surfaces.eventTypesEmitted].sort();
	surfaces.eventSource = emitFiles.join(", ");

	// --- session version ---
	const sessionText = sessionMgr ? readFileOrNull(sessionMgr) : null;
	surfaces.sessionVersion = null;
	if (sessionText) {
		const m = sessionText.match(/CURRENT_SESSION_VERSION\s*=\s*(\d+)/);
		if (m) surfaces.sessionVersion = Number(m[1]);
	}

	// --- doc hashes (subprotocol docs) ---
	surfaces.docHashes = {};
	for (const [key, p] of Object.entries({ rpcDoc, rpcCmdDoc, uiDoc, jsonDoc, sessionDoc })) {
		const t = p ? readFileOrNull(p) : null;
		surfaces.docHashes[key] = { path: p?.replace(pkgRoot, "").replace(/\\/g, "/"), sha: t ? sha(t) : null };
	}
	return surfaces;
}

// ---------------------------------------------------------------------------
// compare + report
// ---------------------------------------------------------------------------

function setDiff(a, b) {
	return {
		added: b.filter((x) => !a.includes(x)),
		removed: a.filter((x) => !b.includes(x)),
	};
}

function compareSurfaces(base, target) {
	const findings = [];

	// RPC commands
	const cmds = setDiff(base.rpcCommands, target.rpcCommands);
	for (const c of cmds.removed) {
		findings.push({ tier: "breaking", area: "rpc-commands", item: `command '${c}' removed`, evidence: base.rpcCommandsLoc });
	}
	for (const c of cmds.added) {
		findings.push({ tier: "evaluate", area: "rpc-commands", item: `command '${c}' added`, evidence: target.rpcCommandsLoc });
	}

	// RPC request fields (interfaces present in both)
	for (const name of new Set([...Object.keys(base.rpcInterfaces), ...Object.keys(target.rpcInterfaces)])) {
		const b = base.rpcInterfaces[name] ?? [];
		const t = target.rpcInterfaces[name] ?? [];
		const d = setDiff(b, t);
		if (d.removed.length) {
			findings.push({ tier: "breaking", area: "rpc-fields", item: `${name}: removed fields [${d.removed.join(", ")}]`, evidence: base.rpcTypesLoc });
		}
		if (d.added.length) {
			findings.push({ tier: "evaluate", area: "rpc-fields", item: `${name}: added fields [${d.added.join(", ")}]`, evidence: target.rpcTypesLoc });
		}
	}

	// Event types — emitted set (runtime truth)
	const ev = setDiff(base.eventTypesEmitted, target.eventTypesEmitted);
	for (const e of ev.removed) {
		findings.push({ tier: "breaking", area: "events-emitted", item: `event '${e}' no longer emitted`, evidence: base.eventSource });
	}
	for (const e of ev.added) {
		findings.push({ tier: "evaluate", area: "events-emitted", item: `event '${e}' now emitted`, evidence: target.eventSource });
	}
	// Documented set (docs/json.md) — doc-only changes are informational
	const evDoc = setDiff(base.eventTypesDoc, target.eventTypesDoc);
	for (const e of evDoc.removed) {
		findings.push({ tier: "evaluate", area: "events-docs", item: `event '${e}' removed from json.md`, evidence: "docs/json.md" });
	}
	for (const e of evDoc.added) {
		findings.push({ tier: "evaluate", area: "events-docs", item: `event '${e}' added to json.md`, evidence: "docs/json.md" });
	}

	// extension_ui methods
	const m = setDiff(base.uiMethods, target.uiMethods);
	for (const x of m.removed) {
		findings.push({ tier: "breaking", area: "ext-ui", item: `ui method '${x}' removed`, evidence: "dist/modes/rpc/rpc-mode.js" });
	}
	for (const x of m.added) {
		findings.push({ tier: "evaluate", area: "ext-ui", item: `ui method '${x}' added`, evidence: "dist/modes/rpc/rpc-mode.js" });
	}
	const tm = setDiff(base.uiTimeoutMethods, target.uiTimeoutMethods);
	for (const x of tm.removed) {
		findings.push({ tier: "evaluate", area: "ext-ui", item: `ui method '${x}' lost timeout support`, evidence: "dist/modes/rpc/rpc-mode.js" });
	}
	for (const x of tm.added) {
		findings.push({ tier: "evaluate", area: "ext-ui", item: `ui method '${x}' gained timeout support`, evidence: "dist/modes/rpc/rpc-mode.js" });
	}

	// session version
	if (base.sessionVersion !== null && target.sessionVersion !== null && base.sessionVersion !== target.sessionVersion) {
		findings.push({ tier: "breaking", area: "session-format", item: `session version ${base.sessionVersion} -> ${target.sessionVersion}`, evidence: "dist/core/session-manager.js" });
	}

	// doc changes (informational)
	for (const [key, meta] of Object.entries(base.docHashes)) {
		const other = target.docHashes[key];
		if (meta.sha && other?.sha && meta.sha !== other.sha) {
			findings.push({ tier: "evaluate", area: "docs", item: `${key} changed`, evidence: `${meta.path}` });
		}
	}

	return findings;
}

function renderReport(baseLabel, targetLabel, findings) {
	const tier = { breaking: [], evaluate: [], none: [] };
	for (const f of findings) (tier[f.tier] ?? tier.evaluate).push(f);
	const noneNote = findings.length === 0 ? "两个版本在所扫描的协议面上完全一致。\n" : "";

	let md = `# Pi 升级雷达报告\n\n`;
	md += `- 基线：\`${baseLabel}\`\n- 目标：\`${targetLabel}\`\n- 生成：${new Date().toISOString()}\n- 范围：rpc 命令/字段、事件类型、extension_ui 子协议、session 版本、docs\n\n`;

	md += `## 破坏性（升级前必须处理）\n\n`;
	if (tier.breaking.length) {
		for (const f of tier.breaking) md += `- **${f.area}** — ${f.item}\n  - 证据：\`${f.evidence}\`\n`;
	} else {
		md += "（无）\n";
	}
	md += `\n## 需评估（行为可能变化，人工确认）\n\n`;
	if (tier.evaluate.length) {
		for (const f of tier.evaluate) md += `- **${f.area}** — ${f.item}\n  - 证据：\`${f.evidence}\`\n`;
	} else {
		md += "（无）\n";
	}
	md += `\n## 无影响\n\n`;
	if (tier.none.length) {
		for (const f of tier.none) md += `- **${f.area}** — ${f.item}\n`;
	} else {
		md += noneNote || "（本次扫描面上未发现新增差异之外的条目）\n";
	}
	md += `\n> 本报告只对比，不自动升级、不改 lockfile（计划 §3 红线）。\n`;
	return md;
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

async function resolveVersion(spec) {
	if (spec === "runtime") {
		const runtimePkg = join(REPO_ROOT, "runtime", "pi");
		const pkgJson = join(runtimePkg, "package.json");
		if (!existsSync(pkgJson)) throw new Error(`runtime/pi not found at ${runtimePkg}`);
		const v = JSON.parse(readFileSync(pkgJson, "utf8")).dependencies?.[PKG];
		if (!v) throw new Error(`cannot read ${PKG} version from ${pkgJson}`);
		const installed = join(runtimePkg, "node_modules", ...PKG.split("/"));
		if (!existsSync(installed)) throw new Error(`installed package tree not found at ${installed}`);
		return { label: `runtime/pi (${v})`, root: installed, version: v };
	}
	if (existsSync(spec)) {
		const root = resolve(spec);
		const pj = join(root, "package.json");
		const version = existsSync(pj) ? JSON.parse(readFileSync(pj, "utf8")).version : "(dir)";
		return { label: `dir ${spec} (${version})`, root, version };
	}
	return { spec, needsFetch: true };
}

async function materialize(spec, tmp) {
	const r = await resolveVersion(spec);
	if (!r.needsFetch) return r;
	const meta = await fetchJson(REGISTRY_URL);
	const version = spec ?? meta["dist-tags"]?.latest;
	const tarball = meta.versions?.[version]?.dist?.tarball;
	if (!tarball) throw new Error(`no tarball for ${version}`);
	const tgz = join(tmp, `${PKG.replace(/\W/g, "_")}-${version}.tgz`);
	await download(tarball, tgz);
	const root = extractTgz(tgz, join(tmp, `pkg-${version}`));
	return { label: `npm ${version}`, root, version };
}

async function main() {
	const args = parseArgs(process.argv.slice(2));
	const tmp = mkdtempSync(join(tmpdir(), "fff-upgradewatch-"));
	try {
		const base = await materialize(args.base, tmp);
		const target = await materialize(args.target ?? (await fetchJson(REGISTRY_URL))["dist-tags"].latest, tmp);
		console.log(`base:   ${base.label}`);
		console.log(`target: ${target.label}`);

		const b = extractSurfaces(base.root);
		const t = extractSurfaces(target.root);
		const findings = compareSurfaces(b, t);
		const md = renderReport(base.label, target.label, findings);
		const outPath = args.out ?? join(REPO_ROOT, "scripts", "upgrade-watch", `report-${base.version}-to-${target.version}.md`);
		writeFileSync(outPath, md);
		console.log(`\nfindings: ${findings.length}`);
		console.log(`report:   ${outPath}`);
		const breaking = findings.filter((f) => f.tier === "breaking").length;
		process.exit(breaking > 0 ? 3 : 0);
	}
	finally {
		rmSync(tmp, { recursive: true, force: true });
	}
}

main().catch((e) => { console.error("fatal:", e.message); process.exit(1); });
