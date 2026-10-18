#!/usr/bin/env node
// cua-dispatch.mjs — 经 Cua-Driver 把一段任务指令派发到 GUI 应用窗口（devin/Maka/codex desktop 等）
// 用法：node scripts/cua-dispatch.mjs --window <标题子串> --text <指令文本> [--send enter] [--delay-ms 30]
//        node scripts/cua-dispatch.mjs --pid <pid> --window-id <hwnd> --text-file <file>
// 前提：Cua-Driver 守护在跑（cua-driver serve）。未跑则脚本尝试自启。
// 实测笔记（2026-09-29/30 验证）：
//  - Windows Terminal(devin)=CASCADIA_HOSTING_WINDOW_CLASS：PostMessage 被丢，type/press 必须 foreground。
//  - Electron(Maka/codex desktop)：先试后台 type_text，被丢会自动返回 background_unavailable→升级 foreground。
//  - 每个 UIA 动作前先刷快照取新 element_token；本脚本只用 type_text/press_key（全局输入路径），无需 token。
//  - foreground 投递=短暂前台交换+SendInput，完成后恢复原前台；派发期间用户按键可能混入——建议在用户不操作时跑。
import { spawnSync, spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const DRIVER = "cua-driver";
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const has = (name) => args.includes(name);

const title = opt("--window", null);
const pid = Number(opt("--pid", "0")) || null;
const windowId = opt("--window-id", null);
const text = opt("--text", null) ?? (opt("--text-file") ? fs.readFileSync(opt("--text-file"), "utf8") : null);
const sendKey = opt("--send", "enter");      // "enter" | "none"
const inputMode = opt("--input", "auto");    // "auto" | "type" | "paste"（Electron 实测：SendInput 字符不落输入框，用剪贴板粘贴）
const clickAt = opt("--click-at", null);     // "x,y" 输入框坐标（paste 模式聚焦用；不传则跳过点击）
const pollSec = Number(opt("--poll", "45")); // 派发后等待秒数再截图
const shotOut = opt("--shot", path.join("tmp", "dispatch-shot.png"));

if (!text || (!title && !pid)) {
  console.error("用法: --window <标题子串> --text <指令> | --text-file <文件> [--send enter|none] [--poll 45] [--shot out.png]");
  process.exit(2);
}

const call = (tool, payload, { stdin = false } = {}) => {
  const argv = stdin ? [DRIVER, "call", tool, "-"] : [DRIVER, "call", tool, JSON.stringify(payload)];
  const r = stdin
    ? spawnSync(DRIVER, ["call", tool, "-"], { input: JSON.stringify(payload), encoding: "utf8", timeout: 30_000, windowsHide: true })
    : spawnSync(DRIVER, ["call", tool, JSON.stringify(payload)], { encoding: "utf8", timeout: 30_000, windowsHide: true });
  const out = (r.stdout ?? "") + (r.stderr ?? "");
  let json; try { json = JSON.parse(out.trim()); } catch { json = { raw: out.trim() }; }
  return { status: r.status ?? 0, json, out: out.trim() };
};

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// 1) 守护进程
let ping = call("list_windows", {});
if (ping.status !== 0 || ping.out.toLowerCase().includes("failed") || ping.out.toLowerCase().includes("error")) {
  const srv = spawn(DRIVER, ["serve"], { detached: true, stdio: "ignore", windowsHide: true });
  srv.unref();
  for (let i = 0; i < 10; i++) { await sleep(800); ping = call("list_windows", {}); if (ping.status === 0 && !ping.out.toLowerCase().includes("error")) break; }
}

// 2) 定位窗口
let target = null;
if (pid && windowId) target = { pid, window_id: windowId };
else {
  const wins = ping.json?._legacy_windows ?? ping.json?.windows ?? [];
  target = wins.find((w) => (w.title ?? "").toLowerCase().includes(title.toLowerCase()));
  if (!target) { console.error(`找不到标题含 "${title}" 的窗口`); process.exit(1); }
}
const pidN = target.pid, wid = target.window_id ?? target.windowId;
console.log(`目标窗口: pid=${pidN} hwnd=${wid} title=${target.title}`);

// 3) 前台化（CASCADIA/最小化窗口需要）
call("bring_to_front", { pid: pidN, window_id: wid });
await sleep(800);

// 4) 输入文本
const clickInput = () => {
  if (!clickAt) return;
  const [cx, cy] = clickAt.split(",").map(Number);
  call("click", { pid: pidN, window_id: wid, delivery_mode: "foreground", x: cx, y: cy });
  console.log(`点击输入框 (${cx},${cy})`);
};
if (inputMode === "paste") {
  // Electron/Chrome_WidgetWin 实测：SendInput 的 type_text 不落入网页输入框；
  // clipboard_write + 前台 Ctrl+V 可靠。先点击聚焦输入框。
  call("clipboard_write", { text }, { stdin: text.length > 8000 });
  clickInput();
  await sleep(600);
  const p = call("hotkey", { pid: pidN, window_id: wid, delivery_mode: "foreground", keys: ["ctrl", "v"] });
  console.log("paste:", JSON.stringify(p.json).slice(0, 200));
} else {
  const tryType = (mode) => call("type_text", { pid: pidN, window_id: wid, delivery_mode: mode, text }, { stdin: text.length > 8000 });
  let t = inputMode === "type" ? tryType("foreground") : tryType("background");
  if (inputMode === "auto" && ((t.json?.code === "background_unavailable") || (t.json?.escalation?.recommended === "foreground"))) {
    console.log("后台输入被拒 → 升级 foreground");
    t = tryType("foreground");
  }
  console.log("type_text:", JSON.stringify(t.json).slice(0, 200));
  // auto 模式下 Electron 可能"接收但不显示"——用户应核对回执截图；已知不可靠时改用 --input paste
}

// 5) 回车发送
if (sendKey !== "none") {
  const r = call("press_key", { pid: pidN, window_id: wid, delivery_mode: "foreground", key: "return" });
  console.log("press_key:", JSON.stringify(r.json).slice(0, 200));
}

// 6) 等待后截图验证
await sleep(pollSec * 1000);
const s = call("get_window_state", { pid: pidN, window_id: wid, include_screenshot: true, include_accessibility_tree: false });
const b64 = s.json?.screenshot_png_b64 ?? s.json?.screenshot_b64;
if (b64) { fs.mkdirSync(path.dirname(shotOut), { recursive: true }); fs.writeFileSync(shotOut, Buffer.from(b64, "base64")); console.log(`回执截图: ${shotOut}`); }
else console.log("截图未取得:", s.out.slice(0, 200));
