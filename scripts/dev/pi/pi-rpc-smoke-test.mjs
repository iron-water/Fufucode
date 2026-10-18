// Pi backend plugin diagnostic — not part of the default native package.
// Smoke test: drive pi --mode rpc over JSONL stdin/stdout, mimicking what a C# host must do.
import { spawn } from "node:child_process";
import { homedir } from "node:os";
import { join } from "node:path";

const cli = join(homedir(), "AppData/Roaming/npm/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js");
const child = spawn(process.execPath, [cli, "--mode", "rpc", "--no-session"], {
	cwd: "C:\\Projects\\Fufucode",
	stdio: ["pipe", "pipe", "pipe"],
});

let buf = "";
const records = [];
child.stdout.setEncoding("utf8");
child.stdout.on("data", (d) => {
	buf += d;
	let i;
	while ((i = buf.indexOf("\n")) >= 0) {
		const line = buf.slice(0, i).replace(/\r$/, "");
		buf = buf.slice(i + 1);
		if (line.trim()) records.push(JSON.parse(line));
	}
});
let stderr = "";
child.stderr.setEncoding("utf8");
child.stderr.on("data", (d) => (stderr += d));

const send = (obj) => child.stdin.write(JSON.stringify(obj) + "\n");
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const waitFor = async (pred, timeoutMs) => {
	const end = Date.now() + timeoutMs;
	while (Date.now() < end) {
		const hit = records.find(pred);
		if (hit) return hit;
		await wait(50);
	}
	return null;
};
const mark = () => records.length;

await waitFor((r) => r.type === "response", 15000);
console.log("=== startup: records seen =", records.length, "stderr =", JSON.stringify(stderr.slice(0, 200)));

// 1. free state query
send({ id: "s1", type: "get_state" });
let resp = await waitFor((r) => r.type === "response" && r.id === "s1", 10000);
console.log("=== get_state:", JSON.stringify(resp && { success: resp.success, model: resp.data?.model?.id, sessionFile: resp.data?.sessionFile, isStreaming: resp.data?.isStreaming }));

// 2. available models
send({ id: "s2", type: "get_available_models" });
resp = await waitFor((r) => r.type === "response" && r.id === "s2", 10000);
const models = resp?.data?.models ?? [];
console.log("=== models:", models.length, "first:", models[0] && `${models[0].provider}/${models[0].id}`);

// 3. tiny real prompt to validate the event stream end-to-end
const n0 = mark();
send({ id: "s3", type: "prompt", message: "只回复两个字：正常" });
resp = await waitFor((r) => r.type === "response" && r.id === "s3", 10000);
console.log("=== prompt accepted:", resp && resp.success);
const settled = await waitFor((r) => r.type === "agent_settled", 120000);
const evTypes = records.slice(n0).map((r) => r.type);
const uniq = [...new Set(evTypes)];
console.log("=== settled:", !!settled, "| event types seen:", uniq.join(", "));
const lastText = records.filter((r) => r.type === "message_end" && r.message?.role === "assistant").pop();
console.log("=== assistant text:", JSON.stringify(lastText?.message?.content?.map((c) => c.text ?? `[${c.type}]`).join("") ?? null));

child.stdin.end();
await new Promise((r) => child.on("exit", r));
console.log("=== clean exit, code =", child.exitCode, "| total records:", records.length);
