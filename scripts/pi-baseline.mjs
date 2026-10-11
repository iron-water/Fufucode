import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const node = join(root, "runtime", "node", "node.exe");
const cli = join(root, "runtime", "pi", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
const cwd = await mkdtemp(join(tmpdir(), "fufucode-pi-"));
const child = spawn(node, [cli, "--mode", "rpc", "--no-session"], {
  cwd, windowsHide: true, stdio: ["pipe", "pipe", "pipe"],
});
let buffer = "";
let stderr = "";
const pending = new Map();
let exited = false;
function failPending(error) {
  for (const waiter of pending.values()) waiter.reject(error);
  pending.clear();
}
const timeout = setTimeout(() => {
  failPending(new Error(`Pi did not respond within 15 seconds; stderr: ${stderr}`));
  child.kill();
}, 15000);
child.once("error", error => failPending(error));
child.once("exit", code => {
  exited = true;
  failPending(new Error(`Pi exited before responding (code ${code}); stderr: ${stderr}`));
});
child.stdout.setEncoding("utf8");
child.stdout.on("data", chunk => {
  buffer += chunk;
  for (let end; (end = buffer.indexOf("\n")) >= 0;) {
    const line = buffer.slice(0, end).replace(/\r$/, "");
    buffer = buffer.slice(end + 1);
    if (!line) continue;
    let record;
    try { record = JSON.parse(line); } catch (error) {
      console.error("invalid RPC record", error);
      child.kill();
      return;
    }
    if (record.type === "response" && pending.has(record.id)) {
      pending.get(record.id).resolve(record);
      pending.delete(record.id);
    }
  }
});
child.stderr.setEncoding("utf8");
child.stderr.on("data", chunk => { stderr = (stderr + chunk).slice(-4000); });
function request(id, type) {
  return new Promise((resolve, reject) => {
    if (exited) { reject(new Error("Pi already exited.")); return; }
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ id, type }) + "\n", error => {
      if (error) { pending.delete(id); reject(error); }
    });
  });
}
try {
  for (const type of ["get_state", "get_available_models"]) {
    const response = await request(type, type);
    if (!response.success || response.command !== type) throw new Error(`${type}: ${response.error ?? "invalid response"}`);
    console.log(`${type}: success; data keys: ${Object.keys(response.data ?? {}).join(", ")}`);
  }
  child.stdin.end();
  const code = child.exitCode ?? await new Promise(resolve => child.once("exit", resolve));
  if (code !== 0) throw new Error(`Pi exit ${code}; stderr: ${stderr}`);
  console.log("Pi exited normally (code 0)");
} finally {
  clearTimeout(timeout);
  if (!child.killed && child.exitCode === null) child.kill();
  await rm(cwd, { recursive: true, force: true });
}
