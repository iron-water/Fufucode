// 无人值守驱动器 v2：读 docs/archive/coordination/queue.md，对 pending 的 codex 条目派发→等待→代提交→验证→合并→标记。
// v2 修复：假 done 漏洞（无产出检测）、agent 禁止自行 git 操作（沙箱挡 .git 元数据，由驱动器代提交）、内存状态同步。
// 用法：node scripts/orchestrator.mjs --tick [--track A]   （锁防重叠；每小时由定时任务唤起）
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const mainWorktree = repo;
const queuePath = path.join(repo, "docs/archive/coordination/queue.md");
const lockPath = path.join(repo, "tmp/orchestrator.lock");
const logPath = path.join(repo, "logs/orchestrator.log");
const MAX_HOURS = 4;

const args = process.argv.slice(2);
if (!args.includes("--historical-queue-execution")) {
  console.error("Historical queues are disabled. Follow docs/AI_DEVELOPMENT_PLAN.md; no automatic dispatch occurred.");
  process.exit(2);
}
const onlyTrack = args.includes("--track") ? args[args.indexOf("--track") + 1] : null;

const log = (...m) => {
  const line = `[${new Date().toISOString()}] ${m.join(" ")}`;
  console.log(line);
  fs.mkdirSync(path.dirname(logPath), { recursive: true });
  fs.appendFileSync(logPath, line + "\n");
};
const run = (cmd, cwd, timeoutMs = 10 * 60_000) =>
  spawnSync(cmd, { cwd, shell: true, encoding: "utf8", timeout: timeoutMs });

// ---- 锁 ----
fs.mkdirSync(path.dirname(lockPath), { recursive: true });
if (fs.existsSync(lockPath)) {
  const info = fs.readFileSync(lockPath, "utf8");
  const started = Number(info.split("\n")[1] || 0);
  if (Date.now() - started < MAX_HOURS * 3600_000) {
    console.log(`orchestrator 已在运行（pid ${info.split("\n")[0]}），本次跳过。`);
    process.exit(0);
  }
  log(`发现陈旧锁（pid ${info.split("\n")[0]}），强制接管。`);
}
fs.writeFileSync(lockPath, `${process.pid}\n${Date.now()}`);
process.on("exit", () => fs.existsSync(lockPath) && fs.rmSync(lockPath, { force: true }));

// ---- 解析队列（列序：1轨道 2任务 3分支 4worktree 5任务书 6调度命令 7状态）----
const raw = fs.readFileSync(queuePath, "utf8").split("\n");
const rows = raw
  .map((line, idx) => ({ line, idx, cells: line.split("|").map((c) => c.trim()) }))
  .filter((r) => r.cells.length >= 9 && /^[A-I]\d*$/.test(r.cells[1]));

const entries = rows.map((r) => ({
  track: r.cells[1],
  task: r.cells[2],
  branch: r.cells[3],
  worktree: r.cells[4],
  taskbook: r.cells[5],
  cmd: r.cells[6],
  status: r.cells[7],
  lineIdx: r.idx,
  raw: r.line,
}));

const setCell = (e, col, value) => {
  const cells = e.raw.split("|").map((c) => c.trim());
  cells[col] = value;
  e.raw = cells.join(" | ");
  raw[e.lineIdx] = e.raw;
  fs.writeFileSync(queuePath, raw.join("\n"));
  e.status = value;
  // 自动提交队列状态（否则共享工作区脏文件会挡住后续 fast-forward，tick 读到陈旧队列）
  spawnSync("git add docs/archive/coordination/queue.md", { cwd: repo, shell: true });
  spawnSync(`git commit -m ${JSON.stringify("chore: 队列状态更新[" + e.track + "=" + value + "]")}`, { cwd: repo, shell: true });
};

const dispatchPrompt = (e) =>
  `你是轨道 ${e.track}。阅读 ${e.taskbook} 与 AGENTS.md，执行其中分配给本轨道的任务。` +
  `重要纪律：**不要执行任何 git commit/merge/push**（沙箱无 .git 元数据写权限，会失败）——把全部改动留在工作区，由外部驱动器代提交与合并。` +
  `**不要运行 dotnet build / dotnet test 或任何构建命令**（沙箱构建会污染文件权限，验证由外部驱动器执行）——你只写代码与文档。` +
  `**不要执行 dotnet add package 或任何写用户目录的操作**（同样会被沙箱拒绝）——需要第三方依赖时优先改用无依赖方案（如 P/Invoke、纯 BCL），确需依赖时写入 progress 文件交协调者。` +
  `若任务无法完成，把原因与已完成的中间产物说明写进工作区内的进度文件。最终回复以 DONE 或 BLOCKED:<原因> 开头。`;

const git = (cmd, cwd = mainWorktree, timeoutMs = 5 * 60_000) => run("git " + cmd, cwd, timeoutMs);

// ---- 验证组（按 worktree 是否含解决方案自适应）----
const verify = (e) => {
  const results = [];
  const sln = path.join(e.worktree, "Fufucode.sln");
  if (fs.existsSync(sln)) {
    // 清除沙箱 ACL 污染并删除构建中间物（codex 沙箱构建会给 obj 文件加拒绝 ACL）
    const cleanDirs = [];
    const walk = (dir) => {
      let list = [];
      try { list = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
      for (const it of list) {
        if (!it.isDirectory()) continue;
        if (it.name === "obj" || it.name === "bin") { cleanDirs.push(path.join(dir, it.name)); continue; }
        if (it.name === ".git" || it.name === "node_modules" || it.name === "runtime") continue;
        walk(path.join(dir, it.name));
      }
    };
    walk(e.worktree);
    for (const d of cleanDirs) {
      spawnSync(`icacls "${d}" /reset /t /q /c`, { shell: true });
      fs.rmSync(d, { recursive: true, force: true });
    }
    if (cleanDirs.length) log(`${e.track}: 已清理 ${cleanDirs.length} 个沙箱污染的 obj/bin 目录。`);
    for (const [name, cmd] of [
      ["build", "dotnet build Fufucode.sln"],
      ["fakepi-build", "dotnet build tests/Fufucode.FakePi/Fufucode.FakePi.csproj"],
      ["selftest", "dotnet run --project src/Fufucode.App -- --selftest"],
      ["core-tests", "dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj"],
    ]) {
      const r = run(cmd, e.worktree, 20 * 60_000);
      const ok = r.status === 0;
      results.push(`${name}:${ok ? "PASS" : "FAIL(exit=" + r.status + ")"}`);
      if (!ok) {
        const tail = (r.stdout || "").split("\n").slice(-15).join("\n");
        fs.appendFileSync(logPath, `--- ${e.track} ${name} FAIL tail ---\n${tail}\n`);
        return { ok: false, results };
      }
    }
  } else {
    results.push("verify:none(无解决方案，资产类交付)");
  }
  return { ok: true, results };
};

// ---- 主循环 ----
for (const e of entries) {
  if (onlyTrack && e.track !== onlyTrack) continue;
  if (e.status !== "pending") continue;
  if (e.cmd.trim() !== "codex") {
    log(`${e.track}: 调度命令为 '${e.cmd}'，驱动器只自动派发 codex 条目——跳过。`);
    continue;
  }

  log(`${e.track}: 派发 → ${e.task}`);
  setCell(e, 7, "running");
  const r = run(
    `codex exec --sandbox workspace-write ${JSON.stringify(dispatchPrompt(e))}`,
    e.worktree,
    MAX_HOURS * 3600_000
  );
  const output = (r.stdout || "") + (r.stderr || "");
  fs.appendFileSync(logPath, `--- ${e.track} agent 输出尾部 ---\n${output.split("\n").slice(-40).join("\n")}\n`);

  // 无产出检测：工作区干净 且 分支无新提交 → blocked，绝不标 done
  const st = git("status --short", e.worktree);
  let newCommits = "";
  if (e.branch !== "main") {
    newCommits = (git(`log main..${e.branch} --oneline`, mainWorktree).stdout || "").trim();
  }
  const hasWorkingChanges = (st.stdout || "").trim().length > 0;
  const hasNewCommits = newCommits.length > 0;
  if (!hasWorkingChanges && !hasNewCommits) {
    log(`${e.track}: agent 无产出（工作区干净、无新提交），标记 blocked。`);
    setCell(e, 7, "blocked");
    continue;
  }

  // 代提交（沙箱 agent 不能碰 .git）。branch=main 时先验证后提交，坏代码不进主干。
  const hasSln = fs.existsSync(path.join(e.worktree, "Fufucode.sln"));
  if (e.branch === "main" && hasSln) {
    const pre = verify(e);
    if (!pre.ok) {
      log(`${e.track}: 验证失败 [${pre.results.join(" ")}]，branch=main 模式不提交，标记 blocked（工作区保留改动供下轮修复）。`);
      setCell(e, 7, "blocked");
      continue;
    }
    log(`${e.track}: 验证全绿 [${pre.results.join(" ")}]，代提交产出。`);
    git("add -A", e.worktree);
    const c = git(`commit -m "track-${e.track}: 无人值守轮产出（验证后代提交）"`, e.worktree);
    if (c.status !== 0) {
      log(`${e.track}: 提交失败：${(c.stderr || "").split("\n")[0]}，标记 blocked。`);
      setCell(e, 7, "blocked");
      continue;
    }
    setCell(e, 7, "done");
    continue;
  }
  if (hasWorkingChanges) {
    const add = git("add -A", e.worktree);
    const commit = git(`commit -m "track-${e.track}: 无人值守轮产出（驱动器代提交）"`, e.worktree);
    if (commit.status !== 0) {
      log(`${e.track}: 代提交失败：${(commit.stderr || "").split("\n")[0]}，标记 blocked。`);
      setCell(e, 7, "blocked");
      continue;
    }
    log(`${e.track}: 已代提交工作区改动。`);
  }

  // 验证（在提交后的树上）
  const v = verify(e);
  if (!v.ok) {
    log(`${e.track}: 验证失败 [${v.results.join(" ")}]，标记 blocked，不合并。`);
    setCell(e, 7, "blocked");
    continue;
  }

  // 合并（worktree 即 main 的轨道跳过）
  if (e.branch !== "main") {
    const merge = git(`merge ${e.branch} --no-edit`);
    if (merge.status !== 0) {
      log(`${e.track}: 合并失败：${(merge.stderr || "").split("\n")[0]}，标记 blocked。`);
      setCell(e, 7, "blocked");
      continue;
    }
  }
  log(`${e.track}: 验证全绿 [${v.results.join(" ")}]，产出已进入 main。`);
  setCell(e, 7, "done");
}

const statuses = fs
  .readFileSync(queuePath, "utf8")
  .split("\n")
  .map((l) => l.split("|").map((c) => c.trim()))
  .filter((c) => c.length >= 9 && /^[A-I]$/.test(c[1]))
  .map((c) => `${c[1]}=${c[7]}`)
  .join(" ");
log(`本轮结束。队列状态：${statuses}`);
