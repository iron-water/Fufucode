/**
 * Repo-root / runtime-path resolution shared by the verify suites.
 *
 * P2-11 fix: paths are derived from the script's own location
 * (scripts/<suite>/run.mjs -> <repo>/), so any worktree checks out its own
 * extensions instead of mixing in the shared workspace's files.
 *
 * Overrides (escape hatches, explicit user choice):
 *   FFF_PROJECT_ROOT  -> extension/repo root (default: derived from script)
 *   FFF_PI_RUNTIME    -> pinned runtime dir containing node/ and pi/
 *                        (default: <REPO_ROOT>/runtime)
 */

import { existsSync } from "node:fs";
import { dirname, isAbsolute, join, resolve } from "node:path";

export function resolveRepoRoot(scriptFileUrl) {
	const envRoot = process.env.FFF_PROJECT_ROOT;
	if (envRoot) {
		if (!isAbsolute(envRoot)) {
			throw new Error(`FFF_PROJECT_ROOT must be absolute, got: ${envRoot}`);
		}
		return envRoot;
	}
	const scriptDir = dirname(new URL(scriptFileUrl).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
	return resolve(scriptDir, "..", "..");
}

export function resolveRuntime(repoRoot) {
	return process.env.FFF_PI_RUNTIME ?? join(repoRoot, "runtime");
}

export function runtimePaths(repoRoot) {
	const runtime = resolveRuntime(repoRoot);
	return {
		nodeExe: join(runtime, "node", "node.exe"),
		cliJs: join(runtime, "pi", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js"),
	};
}

export function assertRuntime(paths, repoRoot) {
	for (const [name, p] of Object.entries(paths)) {
		if (!existsSync(p)) {
			throw new Error(
				`${name} not found: ${p}\n` +
				`  repo root: ${repoRoot}\n` +
				`  (this worktree likely has no runtime/ — set FFF_PI_RUNTIME to a checkout that has it, e.g. FFF_PI_RUNTIME=C:\\Projects\\Fufucode\\runtime)`,
			);
		}
	}
}
