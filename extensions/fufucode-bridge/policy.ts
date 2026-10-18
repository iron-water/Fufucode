/**
 * Fufucode bridge policy: maps a tool name to an approval decision.
 *
 * The policy is data-driven. Defaults follow the assignment (计划 §8.2 / 任务书 §3):
 *   - read-only / search tools are auto-allowed,
 *   - file-writing and shell tools always require confirmation,
 *   - unknown custom tools default to confirmation (fail-safe).
 *
 * Overrides come from a JSON config file, resolved in this order:
 *   1. `FUFUCODE_BRIDGE_CONFIG` env var (absolute path to a JSON file),
 *   2. `bridge.config.json` next to the extension,
 *   3. built-in defaults.
 */

import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

export type Decision = "allow" | "confirm" | "deny";

export interface PolicyConfig {
	/** Tools executed without asking. */
	allow?: string[];
	/** Tools that require an explicit approval round-trip. */
	confirm?: string[];
	/** Tools blocked outright, no dialog. */
	deny?: string[];
	/** Decision for tool names not listed above. */
	unknown?: Decision;
	/** Confirm dialog timeout in ms. 0 disables the timeout (waits forever). */
	timeoutMs?: number;
}

const DEFAULT_CONFIG: Required<PolicyConfig> = {
	allow: ["read", "grep", "find", "ls"],
	confirm: ["write", "edit", "bash", "powershell"],
	deny: [],
	unknown: "confirm",
	timeoutMs: 120_000,
};

interface LoadAttempt {
	path: string;
	config?: PolicyConfig;
	error?: string;
}

function tryReadConfigFile(filePath: string): LoadAttempt {
	try {
		const raw = fs.readFileSync(filePath, "utf8");
		const parsed = JSON.parse(raw) as PolicyConfig;
		if (parsed && typeof parsed === "object") {
			return { path: filePath, config: parsed };
		}
		return { path: filePath, error: "config is not an object" };
	}
	catch (err) {
		return { path: filePath, error: err instanceof Error ? err.message : String(err) };
	}
}

function resolveConfig(): { config: Required<PolicyConfig>; diagnostics: string[] } {
	const candidates: string[] = [];
	const envPath = process.env.FUFUCODE_BRIDGE_CONFIG;
	if (envPath) {
		candidates.push(path.resolve(envPath));
	}
	try {
		const here = path.dirname(fileURLToPath(import.meta.url));
		candidates.push(path.join(here, "bridge.config.json"));
	} catch {
		// import.meta.url unavailable (should not happen under jiti); skip.
	}

	const diagnostics: string[] = [];
	for (const candidate of candidates) {
		// A missing file only diagnoses when it was explicitly requested via env.
		if (!fs.existsSync(candidate)) {
			if (candidate === path.resolve(envPath ?? "")) {
				diagnostics.push(`FUFUCODE_BRIDGE_CONFIG 指向的文件不存在: ${candidate}`);
			}
			continue;
		}
		const attempt = tryReadConfigFile(candidate);
		if (attempt.config) {
			return { config: { ...DEFAULT_CONFIG, ...attempt.config }, diagnostics };
		}
		diagnostics.push(`自定义策略解析失败（${candidate}: ${attempt.error ?? "unknown"}），已回退默认 confirm 策略`);
	}
	return { config: { ...DEFAULT_CONFIG }, diagnostics };
}

export class Policy {
	private readonly config: Required<PolicyConfig>;
	readonly diagnostics: string[];

	constructor(config?: PolicyConfig) {
		if (config) {
			this.config = { ...DEFAULT_CONFIG, ...config };
			this.diagnostics = [];
		}
		else {
			const resolved = resolveConfig();
			this.config = resolved.config;
			this.diagnostics = resolved.diagnostics;
		}
	}

	decide(toolName: string): Decision {
		const name = toolName;
		if (this.config.deny.includes(name)) return "deny";
		if (this.config.allow.includes(name)) return "allow";
		if (this.config.confirm.includes(name)) return "confirm";
		return this.config.unknown;
	}

	get timeoutMs(): number {
		return this.config.timeoutMs;
	}

	/** Snapshot used for the approval record (see index.ts). */
	toJSON(): Required<PolicyConfig> {
		return { ...this.config };
	}
}
