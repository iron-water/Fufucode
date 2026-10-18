// Pi 0.87.1 RPC project-trust probe. Only writes under the ignored tmp/ directory.
import { spawn } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import readline from "node:readline";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const node = path.join(root, "runtime", "node", "node.exe");
const cli = path.join(root, "runtime", "pi", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
const work = path.join(root, "tmp", "project-trust-probe");
const project = path.join(work, "project");
const extensionDir = path.join(project, ".pi", "extensions");
const skillDir = path.join(project, ".pi", "skills", "fufucode-trust-skill");
const agentDir = path.join(work, "pi-agent");
const privateHome = path.join(work, "home");
await mkdir(extensionDir, { recursive: true });
await mkdir(skillDir, { recursive: true });
await mkdir(agentDir, { recursive: true });
await mkdir(privateHome, { recursive: true });
await writeFile(path.join(extensionDir, "trust-probe.ts"),
  'export default function(pi) { pi.registerCommand("fufucode-trust-probe", { description: "Trust probe", handler: async () => {} }); }\n');
await writeFile(path.join(skillDir, "SKILL.md"),
  '---\nname: fufucode-trust-skill\ndescription: Local trust probe\n---\nProject trust probe.\n');

async function probe(label, flags) {
  const child = spawn(node, [cli, "--mode", "rpc", "--no-session", ...flags], {
    cwd: project, windowsHide: true, stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env, HOME: privateHome, USERPROFILE: privateHome,
      PI_CODING_AGENT_DIR: agentDir },
  });
  let stderr = "";
  child.stderr.on("data", chunk => { stderr += chunk.toString().slice(0, 2000); });
  const lines = readline.createInterface({ input: child.stdout });
  const response = new Promise((resolve, reject) => {
    lines.on("line", line => {
      let parsed;
      try { parsed = JSON.parse(line); } catch (error) { reject(error); return; }
      if (parsed.type === "response" && parsed.id === "trust-probe") resolve(parsed);
    });
    child.on("error", reject);
    child.on("exit", code => reject(new Error(`${label}: Pi exited ${code}: ${stderr}`)));
  });
  child.stdin.write(JSON.stringify({ id: "trust-probe", type: "get_commands" }) + "\n");
  let timer;
  try {
    const result = await Promise.race([response, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`${label}: timeout: ${stderr}`)), 10000);
    })]);
    if (!result.success || !Array.isArray(result.data?.commands))
      throw new Error(`${label}: invalid get_commands response: ${JSON.stringify(result)}`);
    return result.data.commands.map(command => ({ name: command.name, source: command.source }));
  } finally {
    clearTimeout(timer);
    child.stdin.end();
    await new Promise(resolve => child.once("exit", resolve));
    lines.close();
  }
}

for (const [label, flags, extensionExpected, skillExpected] of [
  ["RPC default ask", [], false, false],
  ["RPC --no-approve", ["--no-approve"], false, false],
  ["RPC --approve", ["--approve"], true, true],
  ["RPC --approve --no-extensions", ["--approve", "--no-extensions"], false, true],
]) {
  const commands = await probe(label, flags);
  const extensionLoaded = commands.some(command => command.name === "fufucode-trust-probe");
  const skillLoaded = commands.some(command => command.name === "skill:fufucode-trust-skill" && command.source === "skill");
  if (extensionLoaded !== extensionExpected || skillLoaded !== skillExpected)
    throw new Error(`${label}: commands=${JSON.stringify(commands)}`);
  process.stdout.write(`PASS ${label}: extension ${extensionLoaded ? "loaded" : "skipped"}, skill ${skillLoaded ? "loaded" : "skipped"}\n`);
}
