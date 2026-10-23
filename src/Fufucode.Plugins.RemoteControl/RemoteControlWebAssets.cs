using System;
using System.Collections.Generic;
using System.IO;
using Fufucode.Core.RemoteControl;

namespace Fufucode.App.Features.RemoteControl;

/// <summary>
/// 手机远控内嵌 Web 页面：自包含 HTML/CSS/JS（无外部 CDN、无构建步骤），
/// 版式对标 WorkBuddy 移动端（深/浅自适应、底部导航、紧凑卡片）。
/// 页面本身只是外壳，所有会话数据与控制指令都经 PIN 换取的令牌访问服务接口。
/// </summary>
public sealed class RemoteControlWebAssets : IRemoteWebAssets
{
    private const string IndexHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="color-scheme" content="light dark">
<meta name="theme-color" content="#f3f3f3">
<title>Fufucode 手机远控</title>
<link rel="stylesheet" href="/app.css">
</head>
<body data-screen="connect">

<div id="banner" class="banner" hidden>
  <span id="bannerText">连接已断开</span>
  <button id="bannerAction" type="button" hidden>重连</button>
</div>

<main id="screens">

  <section class="screen" id="screen-connect" aria-label="配对连接">
    <header class="navbar"><h1>连接电脑</h1><span id="pinState" class="pill">未连接</span></header>
    <div class="pane">
      <p class="hint">在电脑端「手机远控」面板读取 4 位配对码。仅局域网可用，令牌会自动过期。</p>
      <label class="field" for="pin">4 位配对码
        <input id="pin" inputmode="numeric" pattern="[0-9]*" maxlength="4" autocomplete="one-time-code"
               placeholder="0000" data-test="pin">
      </label>
      <p id="pinError" class="error" role="alert" hidden></p>
      <button id="btnPair" class="primary" data-test="pair">验证并连接</button>
      <div id="pairStatus" class="conn-status" aria-live="polite"></div>
    </div>
  </section>

  <section class="screen" id="screen-chat" aria-label="会话">
    <header class="navbar">
      <h1 id="chatTitle">当前会话</h1>
      <span id="chatState" class="pill" data-test="chat-state">空闲</span>
    </header>
    <div id="timeline" class="timeline" data-test="timeline" aria-live="polite"></div>
    <footer class="composer">
      <textarea id="input" rows="1" placeholder="给 Agent 发消息" data-test="input"></textarea>
      <button id="btnSend" class="icon-btn primary-round" aria-label="发送" data-test="send">➤</button>
      <button id="btnStop" class="icon-btn danger-round" aria-label="紧急停止" data-test="stop" hidden>■</button>
    </footer>
  </section>

  <section class="screen" id="screen-approvals" aria-label="工具审批">
    <header class="navbar"><h1>工具审批</h1><span id="approvalState" class="pill">0 项待处理</span></header>
    <div id="approvalList" class="pane" data-test="approval-list"></div>
  </section>

  <section class="screen" id="screen-device" aria-label="设备状态">
    <header class="navbar"><h1>设备</h1><span id="deviceState" class="pill">在线</span></header>
    <div class="pane">
      <ul class="list">
        <li><div class="grow"><div class="name" id="devTitle">Fufucode</div>
          <div class="meta" id="devModel">模型未知</div></div></li>
        <li><div class="grow"><div class="name">连接状态</div>
          <div class="meta" id="devConnection">已连接</div></div></li>
        <li><div class="grow"><div class="name">活动状态</div>
          <div class="meta" id="devActivity">空闲</div></div></li>
        <li><div class="grow"><div class="name">排队消息</div>
          <div class="meta" id="devQueue">0 条</div></div></li>
        <li><div class="grow"><div class="name">令牌有效期</div>
          <div class="meta" id="devToken">--</div></div></li>
      </ul>
      <button id="btnDisconnect" class="danger" data-test="disconnect">断开并清除本机令牌</button>
    </div>
  </section>
</main>

<nav class="tabbar" id="tabbar" hidden>
  <button class="tab active" data-tab="chat" data-test="tab-chat">会话<span id="tabChatDot" class="state-dot run" hidden></span></button>
  <button class="tab" data-tab="approvals" data-test="tab-approvals">审批<span id="tabApprovalBadge" class="badge" hidden>0</span></button>
  <button class="tab" data-tab="device" data-test="tab-device">设备</button>
</nav>

<div id="approvalSheet" class="sheet" hidden role="dialog" aria-modal="true" aria-label="工具审批">
  <div class="sheet-card">
    <h2 id="sheetTitle">批准执行</h2>
    <p id="sheetDetail" class="mono"></p>
    <div class="row">
      <button id="btnDeny" class="danger" data-test="deny">拒绝</button>
      <button id="btnApprove" class="primary" data-test="approve">允许一次</button>
    </div>
  </div>
</div>

<script src="/app.js"></script>
</body>
</html>
""";

    private const string AppCss = """
/* Fufucode 手机远控：窄屏优先，桌面仅居中演示壳。版式对标 WorkBuddy 移动端。 */
:root {
  --bg:#f3f3f3; --surface:#ffffff; --ink:#1b1b1b; --muted:#616161;
  --accent:#0078d4; --accent-ink:#ffffff; --danger:#c42b1c;
  --border:#d1d1d1; --ok:#107c10; --warn:#9d5d00; --tabbar-h:56px;
}
@media (prefers-color-scheme: dark) {
  :root { --bg:#1f1f1f; --surface:#2b2b2b; --ink:#f3f3f3; --muted:#adadad;
    --accent:#60cdff; --accent-ink:#000000; --danger:#f1707b;
    --border:#4a4a4a; --ok:#6ccb5f; --warn:#fce100; }
}
* { box-sizing:border-box; }
html, body { margin:0; height:100%; height:100dvh; background:var(--bg); color:var(--ink);
  font:15px/1.45 "Segoe UI", system-ui, -apple-system, "PingFang SC", "Microsoft YaHei", sans-serif; }
body { display:flex; flex-direction:column; max-width:480px; margin:0 auto;
  border-left:1px solid var(--border); border-right:1px solid var(--border); }
[hidden] { display:none !important; }

.banner { display:flex; gap:8px; align-items:center; justify-content:center;
  padding:6px 12px; font-size:13px; background:#fdf3f4; color:var(--danger);
  border-bottom:1px solid var(--border); }
@media (prefers-color-scheme: dark) { .banner { background:#3d2125; } }
.banner button { border:1px solid var(--danger); background:none; color:var(--danger);
  border-radius:4px; padding:2px 10px; }

main { flex:1; min-height:0; position:relative; }
.screen { position:absolute; inset:0; display:none; flex-direction:column; }
.screen.active { display:flex; }

.navbar { display:flex; align-items:center; gap:8px; padding:0 12px; min-height:48px;
  background:var(--surface); border-bottom:1px solid var(--border);
  padding-top:env(safe-area-inset-top); }
.navbar h1 { font-size:16px; font-weight:600; margin:0; flex:1;
  overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.pill { font-size:12px; padding:2px 8px; border-radius:10px;
  background:#e5f1fb; color:#005a9e; white-space:nowrap; }
@media (prefers-color-scheme: dark) { .pill { background:#0f3a52; color:#60cdff; } }
.pill.err { color:var(--danger); background:#fde7e9; }
@media (prefers-color-scheme: dark) { .pill.err { background:#44272b; } }

.pane { flex:1; overflow-y:auto; padding:16px; }
.hint { color:var(--muted); margin-top:0; }
.field { display:block; margin:12px 0; color:var(--muted); font-size:13px; }
.field input { display:block; width:100%; margin-top:4px; padding:12px; font-size:22px;
  letter-spacing:10px; text-align:center; border:1px solid var(--border);
  border-radius:6px; background:var(--surface); color:var(--ink); }
.error { color:var(--danger); font-size:13px; margin:8px 0; }
.conn-status { margin-top:12px; font-size:13px; color:var(--muted); }

button { font:inherit; cursor:pointer; }
.primary { background:var(--accent); color:var(--accent-ink); border:none;
  border-radius:6px; padding:12px 16px; width:100%; font-weight:600; min-height:48px; }
.danger { background:none; border:1px solid var(--danger); color:var(--danger);
  border-radius:6px; padding:12px 16px; width:100%; min-height:48px; }
button:disabled { opacity:.45; cursor:default; }

.list { list-style:none; margin:0; padding:0; }
.list li { display:flex; align-items:center; gap:10px; padding:12px;
  background:var(--surface); border:1px solid var(--border); border-radius:8px;
  margin-bottom:8px; }
.list .grow { flex:1; min-width:0; }
.list .name { font-weight:600; }
.list .meta { font-size:12px; color:var(--muted); overflow:hidden;
  text-overflow:ellipsis; white-space:nowrap; }

.timeline { flex:1; min-height:0; overflow-y:auto; padding:12px; }
.msg { margin-bottom:12px; display:flex; }
.msg .bubble { max-width:82%; padding:8px 12px; border-radius:12px;
  background:var(--surface); border:1px solid var(--border);
  white-space:pre-wrap; overflow-wrap:anywhere; }
.msg.user { justify-content:flex-end; }
.msg.user .bubble { background:var(--accent); color:var(--accent-ink); border:none; }
.msg.sys .bubble { font-size:13px; color:var(--muted); max-width:100%; border-style:dashed; }
.think { font-size:13px; color:var(--muted); background:var(--surface);
  border:1px dashed var(--border); border-radius:12px; padding:8px 12px;
  max-width:82%; margin-bottom:12px; white-space:pre-wrap; overflow-wrap:anywhere; }

.tool-card { border:1px solid var(--border); border-radius:8px; margin-bottom:12px;
  background:var(--surface); overflow:hidden; }
.tool-card header { display:flex; align-items:center; gap:8px; padding:8px 10px;
  font-size:13px; font-weight:600; border-bottom:1px solid var(--border); }
.tool-card pre { margin:0; padding:10px; font-size:12px; overflow-x:auto;
  white-space:pre-wrap; overflow-wrap:anywhere; font-family:Consolas,ui-monospace,monospace; }
.tool-card .actions { display:flex; gap:8px; padding:8px 10px; }
.tool-card .actions button { padding:8px 12px; }
.t-pending { color:var(--warn); } .t-ok { color:var(--ok); } .t-err { color:var(--danger); }
.cursor { display:inline-block; width:8px; height:14px; background:var(--accent);
  vertical-align:-2px; animation:blink 1s steps(1) infinite; }
@keyframes blink { 50% { opacity:0; } }

.composer { display:flex; gap:6px; align-items:flex-end; padding:8px;
  background:var(--surface); border-top:1px solid var(--border);
  padding-bottom:calc(8px + env(safe-area-inset-bottom)); }
.composer textarea { flex:1; resize:none; border:1px solid var(--border);
  border-radius:18px; padding:9px 12px; max-height:120px; font:inherit;
  background:var(--bg); color:var(--ink); }
/* 44px 是移动端可靠触摸命中区下限；38px 在真机上容易误触。 */
.icon-btn { width:44px; height:44px; border-radius:50%; border:1px solid var(--border);
  background:var(--surface); color:var(--ink); font-size:18px; flex:none; }
.primary-round { background:var(--accent); color:var(--accent-ink); border:none; }
.danger-round { background:var(--danger); color:#fff; border:none; }

.tabbar { display:flex; align-items:stretch; background:var(--surface);
  border-top:1px solid var(--border); padding-bottom:env(safe-area-inset-bottom); }
.tabbar .tab { flex:1; display:flex; align-items:center; justify-content:center; gap:6px;
  min-height:48px; border:none; background:none; color:var(--muted);
  font-size:13px; position:relative; }
.tabbar .tab.active { color:var(--accent); font-weight:600; }
.badge { min-width:18px; height:18px; border-radius:9px; background:var(--danger);
  color:#fff; font-size:11px; display:grid; place-items:center; padding:0 4px; }
.state-dot { width:8px; height:8px; border-radius:50%; background:var(--ok); }

.sheet { position:fixed; inset:0; background:rgba(0,0,0,.45); display:flex;
  align-items:flex-end; justify-content:center; z-index:10; }
.sheet-card { width:100%; max-width:480px; background:var(--surface);
  border-radius:14px 14px 0 0; padding:16px;
  padding-bottom:calc(16px + env(safe-area-inset-bottom)); }
.sheet-card h2 { margin:0 0 8px; font-size:16px; }
.row { display:flex; gap:10px; margin-top:12px; }
.row .primary { flex:2; }
.mono { font-family:Consolas,ui-monospace,monospace; font-size:13px; white-space:pre-wrap;
  overflow-wrap:anywhere; background:var(--bg); border-radius:6px; padding:10px; }

@media (max-width:340px) {
  .composer textarea { font-size:14px; }
  .navbar h1 { font-size:15px; }
}
""";

    private const string AppJs = """
/* Fufucode 手机远控：PIN 配对 → 令牌 → WebSocket 增量流；断线只读并可重连补齐快照。 */
"use strict";
(function () {
  var TOKEN_KEY = "fufucode.remote.token";
  var state = {
    token: null, socket: null, retry: 0, retryTimer: null, alive: false, connecting: false, generation: 0,
    screen: "chat", session: null, entries: [], approvals: [], notice: null, tokenLifetime: "--"
  };
  var $ = function (id) { return document.getElementById(id); };

  function api(path, body) {
    return fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: body === undefined
        ? { "Authorization": "Bearer " + state.token }
        : { "Content-Type": "application/json", "Authorization": "Bearer " + state.token },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
  }

  /* ---------- 配对 ---------- */
  function pair() {
    if ($("btnPair").disabled) return;
    var pin = $("pin").value.trim();
    var err = $("pinError"), status = $("pairStatus");
    err.hidden = true;
    if (!/^\d{4}$/.test(pin)) { err.textContent = "请输入 4 位数字配对码"; err.hidden = false; return; }
    status.textContent = "正在验证…";
    var generation = ++state.generation;
    $("btnPair").disabled = true;
    fetch("/api/pair", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pin: pin })
    }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (data) {
        if (!response.ok || !data.success) {
          throw new Error(data.error || (response.status === 429
            ? "尝试次数过多，请稍后再试。" : "配对码不正确。"));
        }
        return data;
      });
    }).then(function (data) {
      if (generation !== state.generation) return;
      state.token = data.token;
      try { sessionStorage.setItem(TOKEN_KEY, state.token); } catch (e) { /* 无痕模式忽略 */ }
      $("pin").value = "";
      status.textContent = "已验证，正在连接…";
      showTab("chat");
      connect();
    }).catch(function (error) {
      if (generation !== state.generation) return;
      err.textContent = error.message || "配对失败。"; err.hidden = false;
      status.textContent = "";
    }).finally(function () { $("btnPair").disabled = false; });
  }
  $("btnPair").addEventListener("click", pair);
  $("pin").addEventListener("keydown", function (e) { if (e.key === "Enter") pair(); });

  /* ---------- 连接与增量 ---------- */
  function connect() {
    if (!state.token || state.connecting || state.alive) return;
    state.connecting = true;
    var scheme = location.protocol === "https:" ? "wss" : "ws";
    var url = scheme + "://" + location.host + "/api/events?token=" + encodeURIComponent(state.token);
    var socket;
    try { socket = new WebSocket(url); } catch (e) { state.connecting = false; scheduleReconnect(); return; }
    state.socket = socket;
    socket.onopen = function () {
      if (state.socket !== socket) return;
      state.connecting = false; state.retry = 0; state.alive = true;
      $("tabbar").hidden = false;
      setBanner(null);
      setPinState("已连接", false);
    };
    socket.onmessage = function (event) {
      if (state.socket !== socket) return;
      var update;
      try { update = JSON.parse(event.data); } catch (e) { return; }
      applyUpdate(update);
    };
    socket.onclose = function () {
      if (state.socket !== socket) return;
      state.connecting = false;
      state.alive = false;
      setBanner("连接已断开，任务在电脑端继续执行");
      setPinState("离线", true);
      api("/api/status").then(function (response) {
        if (state.socket !== socket) return;
        if (response.status === 401) $("btnDisconnect").click();
        else scheduleReconnect();
      }).catch(function () { if (state.socket === socket) scheduleReconnect(); });
    };
    socket.onerror = function () { /* onclose 统一处理重连 */ };
  }

  function scheduleReconnect() {
    if (!state.token) return;
    if (state.retryTimer) return;
    state.retry = Math.min(state.retry + 1, 6);
    var delay = Math.min(800 * Math.pow(1.6, state.retry), 8000);
    state.retryTimer = setTimeout(function () { state.retryTimer = null; connect(); }, delay);
  }

  function setBanner(text) {
    var banner = $("banner"), action = $("bannerAction");
    if (!text) { banner.hidden = true; action.hidden = true; return; }
    $("bannerText").textContent = text;
    banner.hidden = false;
    action.hidden = false;
  }
  $("bannerAction").addEventListener("click", function () { connect(); });

  function setPinState(text, isError) {
    var el = $("pinState");
    el.textContent = text;
    el.className = "pill" + (isError ? " err" : "");
  }

  function applyUpdate(update) {
    switch (update.type) {
      case "snapshot":
        state.session = update.session;
        state.entries = (update.session.entries || []).slice();
        state.approvals = (update.session.pendingApprovals || []).slice();
        renderAll();
        break;
      case "entry":
        upsertEntry(update.entry);
        renderTimeline();
        break;
      case "approvalAdded":
        if (!state.approvals.some(function (a) { return a.approvalId === update.approval.approvalId; }))
          state.approvals.push(update.approval);
        renderApprovals();
        renderTimeline();
        break;
      case "approvalResolved":
        state.approvals = state.approvals.filter(function (a) {
          return a.approvalId !== update.resolution.approvalId;
        });
        renderApprovals();
        renderTimeline();
        break;
      case "status":
        if (!state.session) state.session = { title: "当前会话", entries: [], pendingApprovals: [] };
        state.session.activity = update.activity;
        state.session.connection = update.connection;
        state.session.canSend = update.canSend;
        state.session.canStop = update.canStop;
        state.session.queuedCount = update.queuedCount;
        renderStatus();
        break;
      case "notice":
        state.notice = update.message;
        renderStatus();
        break;
    }
  }

  function upsertEntry(entry) {
    var index = -1;
    for (var i = 0; i < state.entries.length; i++) {
      if (state.entries[i].id === entry.id) { index = i; break; }
    }
    if (index < 0) state.entries.push(entry); else state.entries[index] = entry;
  }

  /* ---------- 渲染 ---------- */
  function renderAll() { renderTimeline(); renderApprovals(); renderStatus(); }

  function renderStatus() {
    var session = state.session;
    if (!session) return;
    var activity = session.activity || "Idle";
    var labels = { Idle: "空闲", Running: "运行中", Compacting: "压缩中", Retrying: "重试中", Cancelling: "停止中" };
    var label = labels[activity] || activity;
    var chatState = $("chatState");
    chatState.textContent = state.alive ? label : "离线";
    chatState.className = "pill" + (state.alive ? "" : " err");
    $("tabChatDot").hidden = activity === "Idle";
    $("chatTitle").textContent = session.title || "当前会话";
    $("devTitle").textContent = session.title || "Fufucode";
    $("devModel").textContent = session.model || "模型未知";
    $("devConnection").textContent = state.alive
      ? (session.connection || "Connected") : "离线（只读）";
    $("devActivity").textContent = label;
    $("devQueue").textContent = (session.queuedCount || 0) + " 条" +
      (state.notice ? " · " + state.notice : "");
    var canSend = state.alive && session.canSend !== false;
    $("input").disabled = !canSend;
    $("btnSend").disabled = !canSend;
    $("btnStop").hidden = !(state.alive && session.canStop);
    var approvalState = $("approvalState");
    approvalState.textContent = state.approvals.length + " 项待处理";
    approvalState.className = "pill" + (state.approvals.length ? " err" : "");
    $("deviceState").textContent = state.alive ? "在线" : "离线";
    $("deviceState").className = "pill" + (state.alive ? "" : " err");
  }

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function renderTimeline() {
    var timeline = $("timeline");
    timeline.innerHTML = "";
    state.entries.forEach(function (entry) {
      (entry.blocks || []).forEach(function (block) {
        if (block.kind === "thinking") timeline.appendChild(el("div", "think", block.text));
        else if (block.kind === "tool") timeline.appendChild(toolCard(block.tool));
        else if (block.kind === "error") timeline.appendChild(el("div", "msg sys").appendChild(el("div", "bubble", block.message)).parentElement);
        else timeline.appendChild(bubble(entry, block.text, entry.isStreaming));
      });
    });
    timeline.scrollTop = timeline.scrollHeight;
  }

  function bubble(entry, text, streaming) {
    var wrapper = el("div", "msg " + (entry.role === "User" || entry.role === "user" ? "user" : "bot"));
    var body = el("div", "bubble", text);
    if (streaming) body.appendChild(el("span", "cursor"));
    wrapper.appendChild(body);
    return wrapper;
  }

  function toolCard(tool) {
    var card = el("div", "tool-card");
    var head = el("header");
    var status = statusText(tool.status);
    head.appendChild(el("span", status.cls, status.icon));
    head.appendChild(el("span", null, tool.toolName));
    head.appendChild(el("span", status.cls, status.label));
    card.appendChild(head);
    var detail = tool.statusText ? tool.statusText + "\n" : "";
    detail += tool.argsJson || "";
    if (tool.output) detail += "\n———\n" + tool.output;
    card.appendChild(el("pre", null, detail));
    if (String(tool.status) === "AwaitingApproval") {
      var approval = state.approvals.filter(function (a) { return a.toolCallId === tool.toolCallId; })[0];
      var actions = el("div", "actions");
      if (approval && state.alive) {
        var approve = el("button", "primary", "批准");
        approve.addEventListener("click", function () { resolveApproval(approval.approvalId, "approve"); });
        var deny = el("button", "danger", "拒绝");
        deny.addEventListener("click", function () { resolveApproval(approval.approvalId, "deny"); });
        actions.appendChild(deny); actions.appendChild(approve);
      } else {
        actions.appendChild(el("span", "meta t-err", "该审批已失效或离线，请在电脑端处理"));
      }
      card.appendChild(actions);
    }
    return card;
  }

  function statusText(status) {
    switch (String(status)) {
      case "AwaitingApproval": return { icon: "⏸", label: "待审批", cls: "t-pending" };
      case "Running": return { icon: "▶", label: "执行中", cls: "t-pending" };
      case "Completed": return { icon: "✔", label: "已完成", cls: "t-ok" };
      case "Failed": return { icon: "✖", label: "失败", cls: "t-err" };
      case "Cancelled": return { icon: "✖", label: "已取消", cls: "t-err" };
      default: return { icon: "…", label: "准备中", cls: "t-pending" };
    }
  }

  function renderApprovals() {
    var list = $("approvalList");
    var badge = $("tabApprovalBadge");
    badge.hidden = state.approvals.length === 0;
    badge.textContent = String(state.approvals.length);
    list.innerHTML = "";
    if (state.approvals.length === 0) {
      list.appendChild(el("p", "hint", "当前没有待审批的工具调用。紧急操作可以在会话页一键停止。"));
      renderStatus();
      return;
    }
    state.approvals.forEach(function (approval) {
      var card = el("div", "tool-card");
      var head = el("header");
      head.appendChild(el("span", "t-pending", "⏸"));
      head.appendChild(el("span", null, approval.toolName));
      head.appendChild(el("span", "t-pending", "待审批"));
      card.appendChild(head);
      card.appendChild(el("pre", null, (approval.summary ? approval.summary + "\n" : "") + approval.argsJson));
      var actions = el("div", "actions");
      var open = el("button", "primary", "查看并决定");
      open.addEventListener("click", function () { openSheet(approval.approvalId); });
      actions.appendChild(open);
      card.appendChild(actions);
      list.appendChild(card);
    });
    renderStatus();
  }

  /* ---------- 审批 ---------- */
  var sheetApprovalId = null;
  function openSheet(id) {
    var approval = state.approvals.filter(function (a) { return a.approvalId === id; })[0];
    if (!approval) return;
    sheetApprovalId = id;
    $("sheetTitle").textContent = "批准执行：" + approval.toolName;
    $("sheetDetail").textContent = (approval.summary ? approval.summary + "\n" : "") + approval.argsJson;
    $("approvalSheet").hidden = false;
  }
  function closeSheet() { $("approvalSheet").hidden = true; sheetApprovalId = null; }
  $("approvalSheet").addEventListener("click", function (e) {
    if (e.target === $("approvalSheet")) closeSheet();
  });
  $("btnApprove").addEventListener("click", function () { resolveApproval(sheetApprovalId, "approve"); closeSheet(); });
  $("btnDeny").addEventListener("click", function () { resolveApproval(sheetApprovalId, "deny"); closeSheet(); });

  function resolveApproval(id, decision) {
    if (!id) return;
    api("/api/approval", { approvalId: id, decision: decision }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (data) {
        if (!response.ok || data.accepted === false) {
          state.notice = "审批已失效，本次操作未生效。";
          renderStatus();
        }
      });
    }).catch(function () { /* 断线重连后由快照补齐 */ });
  }

  /* ---------- 发送 / 停止 ---------- */
  function send() {
    var input = $("input");
    var text = input.value.trim();
    if (!text || !state.token || !state.alive || !state.session || !state.session.canSend || state.sending) return;
    state.sending = true;
    var submitted = input.value, token = state.token;
    api("/api/message", { text: text }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (data) {
        if (state.token !== token) return;
        if (!response.ok || data.accepted !== true) {
          state.notice = data.message || "消息未受理。"; renderStatus();
        } else if (input.value === submitted) {
          input.value = ""; input.style.height = "auto";
        }
      });
    }).catch(function () { if (state.token === token) { state.notice = "发送结果未知，草稿已保留，请核对会话后重试。"; renderStatus(); } })
      .finally(function () { if (state.token === token) state.sending = false; });
  }
  $("btnSend").addEventListener("click", send);
  $("input").addEventListener("keydown", function (e) {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); send(); }
  });
  $("input").addEventListener("input", function () {
    var input = $("input");
    input.style.height = "auto";
    input.style.height = Math.min(input.scrollHeight, 120) + "px";
  });
  $("btnStop").addEventListener("click", function () {
    if (state.stopping || !state.alive) return;
    if (!window.confirm("紧急停止：清空排队消息并中止在途任务。继续？")) return;
    state.stopping = true;
    api("/api/stop", {}).then(function (response) {
      return response.json().then(function (data) {
        state.notice = response.ok && data.accepted === true ? "停止请求已受理。" : (data.message || "停止未受理。");
        renderStatus();
      });
    }).catch(function () { state.notice = "停止结果未知，请核对电脑端状态。"; renderStatus(); })
      .finally(function () { state.stopping = false; });
  });

  /* ---------- 底部导航 ---------- */
  function showTab(name) {
    state.screen = name;
    document.body.dataset.screen = name;
    document.querySelectorAll(".screen").forEach(function (section) {
      section.classList.toggle("active", section.id === "screen-" + name);
    });
    document.querySelectorAll(".tabbar .tab").forEach(function (tab) {
      tab.classList.toggle("active", tab.dataset.tab === name);
    });
    $("tabbar").hidden = !state.token;
    renderAll();
  }
  document.querySelectorAll(".tabbar .tab").forEach(function (tab) {
    tab.addEventListener("click", function () { showTab(tab.dataset.tab); });
  });

  $("btnDisconnect").addEventListener("click", function () {
    state.generation++;
    try { sessionStorage.removeItem(TOKEN_KEY); } catch (e) { /* 忽略 */ }
    if (state.socket) { try { state.socket.close(); } catch (e) { /* 忽略 */ } }
    state.token = null; state.socket = null; state.alive = false;
    state.connecting = false; state.sending = false;
    if (state.retryTimer) clearTimeout(state.retryTimer);
    state.retryTimer = null;
    state.entries = []; state.approvals = []; state.session = null;
    setBanner(null);
    $("tabbar").hidden = true;
    setPinState("未连接", false);
    showTab("chat");
    document.querySelectorAll(".screen").forEach(function (section) {
      section.classList.toggle("active", section.id === "screen-connect");
    });
    document.body.dataset.screen = "connect";
    $("pairStatus").textContent = "已断开，请重新输入配对码。";
  });

  /* ---------- 键盘适配：visualViewport 兜底 ---------- */
  if (window.visualViewport) {
    window.visualViewport.addEventListener("resize", function () {
      var viewport = window.visualViewport;
      document.body.style.height = viewport.height + "px";
      var timeline = $("timeline");
      if (timeline) timeline.scrollTop = timeline.scrollHeight;
    });
  }

  /* ---------- 启动 ---------- */
  try { state.token = sessionStorage.getItem(TOKEN_KEY); } catch (e) { state.token = null; }
  if (state.token) {
    setPinState("恢复中", false);
    $("pairStatus").textContent = "使用已保存的令牌重新连接…";
    showTab("chat");
    connect();
  } else {
    showTab("chat");
    document.querySelectorAll(".screen").forEach(function (section) {
      section.classList.toggle("active", section.id === "screen-connect");
    });
    document.body.dataset.screen = "connect";
  }
})();
""";

    private readonly Dictionary<string, RemoteWebAsset> _assets;

    public RemoteControlWebAssets()
    {
        _assets = new Dictionary<string, RemoteWebAsset>(StringComparer.Ordinal)
        {
            ["/"] = RemoteWebAsset.Text("text/html; charset=utf-8", IndexHtml),
            ["/index.html"] = RemoteWebAsset.Text("text/html; charset=utf-8", IndexHtml),
            ["/app.css"] = RemoteWebAsset.Text("text/css; charset=utf-8", AppCss),
            ["/app.js"] = RemoteWebAsset.Text("text/javascript; charset=utf-8", AppJs),
        };
        AddWorkbenchAssets();
    }

    private void AddWorkbenchAssets()
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "extensions", "browser-extension-mv3", "workbench"),
            Path.Combine(Environment.CurrentDirectory, "extensions", "browser-extension-mv3", "workbench"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extensions", "browser-extension-mv3", "workbench"),
        };
        foreach (var root in roots)
        {
            var html = Path.Combine(root, "index.html");
            var css = Path.Combine(root, "workbench.css");
            var js = Path.Combine(root, "workbench.js");
            if (!File.Exists(html) || !File.Exists(css) || !File.Exists(js)) continue;
            _assets["/workbench"] = new RemoteWebAsset("text/html; charset=utf-8", File.ReadAllBytes(html));
            _assets["/workbench/index.html"] = _assets["/workbench"];
            _assets["/workbench/workbench.css"] = new RemoteWebAsset("text/css; charset=utf-8", File.ReadAllBytes(css));
            _assets["/workbench/workbench.js"] = new RemoteWebAsset("text/javascript; charset=utf-8", File.ReadAllBytes(js));
            break;
        }
    }

    /// <summary>内嵌页面是否只引用自身资源（无外部 CDN，离线局域网可用）。</summary>
    public bool IsSelfContained =>
        !IndexHtml.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
        !IndexHtml.Contains("https://", StringComparison.OrdinalIgnoreCase) &&
        !AppCss.Contains("url(http", StringComparison.OrdinalIgnoreCase);

    /// <summary>生成的资源路径。</summary>
    public IReadOnlyCollection<string> Paths => _assets.Keys;

    public bool TryGet(string path, out RemoteWebAsset asset)
    {
        if (path.Length == 0) path = "/";
        if (_assets.TryGetValue(path, out var found))
        {
            asset = found;
            return true;
        }

        asset = null!;
        return false;
    }
}
