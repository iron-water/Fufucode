(() => {
  "use strict";

  const TOKEN_KEY = "fufucode.workbench.token";
  const HOST_KEY = "fufucode.workbench.host";
  const DRAWERS = { sessions: "sessionsDrawer", context: "contextDrawer", settings: "settingsDrawer" };
  const state = {
    host: "",
    token: null,
    socket: null,
    reconnectTimer: null,
    retry: 0,
    connecting: false,
    alive: false,
    session: null,
    sessions: [],
    entries: new Map(),
    order: [],
    approvals: [],
    context: null,
    sending: false,
    stopping: false,
    generation: 0,
    nearBottom: true,
    expanded: new Set(),
    collapsed: new Set(),
  };

  const $ = id => document.getElementById(id);

  function syncSurfaceMode() {
    const isExtension = typeof chrome !== "undefined" && !!chrome.runtime;
    document.body.classList.toggle("sidepanel-surface", window.innerWidth <= 860 || (isExtension && window.innerWidth < 980));
  }
  syncSurfaceMode();
  window.addEventListener("resize", syncSurfaceMode);

  function normalizeHost(value) {
    let host = String(value || "").trim().replace(/\/$/, "");
    if (!host) return "";
    if (!/^https?:\/\//i.test(host)) host = `http://${host}`;
    return host;
  }

  function setConnection(text, kind = "") {
    $("connectionText").textContent = text;
    $("connectionDot").className = `dot ${kind}`;
  }

  function setError(text) { $("errorText").textContent = text || ""; }

  function api(path, options = {}) {
    const headers = { ...(options.headers || {}) };
    if (state.token) headers.Authorization = `Bearer ${state.token}`;
    if (options.body && !headers["Content-Type"]) headers["Content-Type"] = "application/json";
    return fetch(`${state.host}${path}`, { ...options, headers });
  }

  /* ── 抽屉 ── */
  function openDrawer(name, scrollTarget) {
    closeDrawers();
    const drawer = $(DRAWERS[name]);
    if (!drawer) return;
    drawer.classList.add("open");
    document.querySelectorAll(".action-bar button").forEach(button =>
      button.classList.toggle("active", button.dataset.drawer === name));
    if (scrollTarget) requestAnimationFrame(() => $(scrollTarget)?.scrollIntoView({ block: "start" }));
  }
  function closeDrawers() {
    document.querySelectorAll(".drawer.open").forEach(el => el.classList.remove("open"));
    document.querySelectorAll(".action-bar button").forEach(button => button.classList.remove("active"));
  }
  document.querySelectorAll(".action-bar button").forEach(button => {
    button.addEventListener("click", () => openDrawer(button.dataset.drawer, button.dataset.scroll));
  });
  document.querySelectorAll("[data-close]").forEach(button => button.addEventListener("click", closeDrawers));
  document.addEventListener("keydown", event => { if (event.key === "Escape") closeDrawers(); });
  $("moreBtn").addEventListener("click", () => openDrawer("settings"));

  /* ── 配对与连接 ── */
  async function pair() {
    state.host = normalizeHost($("hostInput").value);
    setError("");
    if (!state.host) { setError("请输入 Fufucode 远控地址。"); return; }
    if (!/^\d{4}$/.test($("pinInput").value.trim())) { setError("请输入 4 位 PIN。"); return; }
    $("pairBtn").disabled = true;
    try {
      const response = await api("/api/pair", { method: "POST", body: JSON.stringify({ pin: $("pinInput").value.trim() }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || !data.success || !data.token) throw new Error(data.error || "配对失败。");
      state.token = data.token;
      localStorage.setItem(HOST_KEY, state.host);
      localStorage.setItem(TOKEN_KEY, state.token);
      closeDrawers();
      connect();
    } catch (error) {
      setError(error.message || "配对失败。");
      setConnection("配对失败", "error");
    } finally {
      $("pairBtn").disabled = false;
    }
  }

  function connect() {
    if (!state.host || !state.token || state.connecting || state.alive) return;
    state.connecting = true;
    setConnection("连接中");
    let socket;
    try {
      socket = new WebSocket(state.host.replace(/^http/i, "ws") + `/api/events?token=${encodeURIComponent(state.token)}`);
    } catch (error) {
      state.connecting = false;
      setConnection("连接失败", "error");
      setError(error.message);
      scheduleReconnect();
      return;
    }
    state.socket = socket;
    socket.onopen = () => {
      if (state.socket !== socket) return;
      state.connecting = false;
      state.alive = true;
      state.retry = 0;
      setConnection("已连接", "ready");
      refreshSessions();
      refreshContext();
    };
    socket.onmessage = event => {
      if (state.socket !== socket) return;
      try { applyUpdate(JSON.parse(event.data)); } catch (error) { console.warn("无效远控事件", error); }
    };
    socket.onerror = () => {};
    socket.onclose = () => {
      if (state.socket !== socket) return;
      state.socket = null;
      state.alive = false;
      state.connecting = false;
      setConnection("已断开", "error");
      scheduleReconnect();
    };
  }

  function scheduleReconnect() {
    if (!state.token || state.reconnectTimer) return;
    state.retry = Math.min(state.retry + 1, 7);
    const delay = Math.min(700 * 1.7 ** state.retry, 10000);
    state.reconnectTimer = setTimeout(() => { state.reconnectTimer = null; connect(); }, delay);
  }

  async function refreshSessions() {
    if (!state.token) return;
    try {
      const response = await api("/api/sessions");
      if (response.status === 401) { disconnect(true); return; }
      const data = await response.json();
      state.sessions = Array.isArray(data.sessions) ? data.sessions : Array.isArray(data) ? data : [];
      renderSessions();
    } catch (error) { console.warn("会话目录读取失败", error); }
  }

  async function switchSession(sessionId) {
    if (!sessionId || sessionId === state.session?.sessionId) { closeDrawers(); return; }
    try {
      const response = await api("/api/sessions/switch", { method: "POST", body: JSON.stringify({ sessionId }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || data.accepted === false) throw new Error(data.message || "会话切换失败。");
      state.entries.clear();
      state.order = [];
      state.approvals = [];
      closeDrawers();
      renderTimeline();
      renderApprovals();
      setConnection("已切换", "ready");
    } catch (error) { setError(error.message || "会话切换失败。"); }
  }

  /* ── 增量更新 ── */
  function applyUpdate(update) {
    if (!update || !update.type) return;
    switch (update.type) {
      case "snapshot": {
        state.session = update.session || null;
        state.entries.clear();
        state.order = [];
        for (const entry of state.session?.entries || []) addEntry(entry);
        state.approvals = [...(state.session?.pendingApprovals || [])];
        renderAll();
        break;
      }
      case "entry":
        addEntry(update.entry);
        renderTimeline();
        break;
      case "approvalAdded":
        if (!state.approvals.some(item => item.approvalId === update.approval?.approvalId)) state.approvals.push(update.approval);
        renderApprovals();
        break;
      case "approvalResolved":
        state.approvals = state.approvals.filter(item => item.approvalId !== update.resolution?.approvalId);
        renderApprovals();
        break;
      case "status":
        state.session = state.session || { entries: [], pendingApprovals: [] };
        Object.assign(state.session, {
          activity: update.activity,
          connection: update.connection,
          canSend: update.canSend,
          canStop: update.canStop,
          queuedCount: update.queuedCount,
        });
        renderHeader();
        break;
      case "sessions":
        state.sessions = update.catalog?.sessions || update.catalog || [];
        renderSessions();
        break;
      case "notice":
        $("draftHint").textContent = update.message || "";
        break;
    }
  }

  function addEntry(entry) {
    if (!entry || !entry.id) return;
    if (!state.entries.has(entry.id)) state.order.push(entry.id);
    state.entries.set(entry.id, entry);
  }

  function renderAll() { renderHeader(); renderSessions(); renderTimeline(); renderApprovals(); }

  function renderHeader() {
    const session = state.session;
    if (!session) return;
    $("sessionTitle").textContent = session.title || "Fufucode";
    $("sessionMeta").textContent = [session.model, activityLabel(session.activity), session.queuedCount ? `队列 ${session.queuedCount}` : ""].filter(Boolean).join(" · ") || "已连接";
    $("stopBtn").classList.toggle("hidden", !(state.alive && session.canStop));
    $("sendBtn").disabled = !(state.alive && session.canSend !== false) || state.sending;
  }

  function activityLabel(activity) {
    return ({ Idle: "空闲", Running: "运行中", Compacting: "压缩中", Retrying: "重试中", Cancelling: "停止中" })[activity] || activity || "";
  }

  function renderSessions() {
    const root = $("sessionList");
    root.innerHTML = "";
    if (!state.sessions.length) { root.innerHTML = '<div class="empty">暂无会话</div>'; return; }
    for (const item of state.sessions) {
      const id = item.sessionId || item.id;
      const button = document.createElement("button");
      button.className = "session" + (id === state.session?.sessionId ? " active" : "");
      const title = document.createElement("span"); title.className = "session-title"; title.textContent = item.title || item.name || "未命名会话";
      const meta = document.createElement("span"); meta.className = "session-meta";
      meta.textContent = [item.projectName || item.project, item.backendName || item.backend].filter(Boolean).join(" · ") || "Fufucode";
      button.append(title, meta);
      button.addEventListener("click", () => switchSession(id));
      root.appendChild(button);
    }
  }

  /* ── 时间线：按 entry 键控更新，保留展开状态 ── */
  function renderTimeline() {
    const root = $("timeline");
    if (!state.order.length) {
      root.innerHTML = '<div class="welcome"><h2>开始新的对话</h2><p>在下方输入消息，或先把当前网页引用给 Agent。</p></div>';
      return;
    }
    const near = state.nearBottom;
    const existing = new Map();
    root.querySelectorAll("[data-entry-id]").forEach(el => existing.set(el.dataset.entryId, el));
    root.querySelector(".welcome")?.remove();
    const keep = new Set();
    for (const id of state.order) {
      const entry = state.entries.get(id);
      if (!entry) continue;
      keep.add(id);
      const key = JSON.stringify(entry);
      let el = existing.get(id);
      if (el && el.dataset.renderKey === key) continue;
      const fresh = renderEntry(entry);
      fresh.dataset.entryId = id;
      fresh.dataset.renderKey = key;
      if (el) el.replaceWith(fresh); else root.appendChild(fresh);
    }
    for (const [id, el] of existing) if (!keep.has(id)) el.remove();
    if (near) root.scrollTop = root.scrollHeight;
    updateJumpButton();
  }

  function renderEntry(entry) {
    const article = document.createElement("article");
    const role = String(entry.role).toLowerCase();
    article.className = `entry ${role}`;
    const blocks = entry.blocks || [];
    let index = 0;
    while (index < blocks.length) {
      const block = blocks[index];
      if (block.kind === "tool") {
        const run = [];
        const start = index;
        while (index < blocks.length && blocks[index].kind === "tool") { run.push(blocks[index].tool); index++; }
        article.appendChild(renderToolGroup(entry.id, start, run));
        continue;
      }
      if (block.kind === "thinking") article.appendChild(renderThinking(entry.id, index, block.text, entry.isStreaming));
      else if (block.kind === "error") {
        const node = document.createElement("div"); node.className = "error-card"; node.textContent = block.message || ""; article.appendChild(node);
      } else {
        const node = document.createElement("div"); node.className = "text-block"; node.textContent = block.text || ""; article.appendChild(node);
      }
      index++;
    }
    if (role === "assistant" && !entry.isStreaming && entry.model) {
      const meta = document.createElement("div"); meta.className = "message-model"; meta.textContent = entry.model; article.appendChild(meta);
    }
    return article;
  }

  function renderThinking(entryId, index, text, streaming) {
    const key = `${entryId}:think:${index}`;
    const card = document.createElement("div");
    card.className = "thinking";
    const open = !state.collapsed.has(key) && (state.expanded.has(key) || streaming);
    if (open) card.classList.add("open");
    const head = document.createElement("button");
    head.innerHTML = '<span>思考</span><span class="chev">›</span>';
    const body = document.createElement("div"); body.className = "thinking-body"; body.textContent = text || "";
    head.addEventListener("click", () => toggle(card, key));
    card.append(head, body);
    return card;
  }

  function toggle(element, key) {
    const open = element.classList.toggle("open");
    if (open) { state.expanded.add(key); state.collapsed.delete(key); }
    else { state.collapsed.add(key); state.expanded.delete(key); }
  }

  function statusMeta(status) {
    switch (String(status)) {
      case "AwaitingApproval": return { cls: "wait", label: "待审批", forced: true };
      case "Running": return { cls: "running", label: "运行中", forced: false };
      case "Completed": return { cls: "ok", label: "已完成", forced: false };
      case "Failed": return { cls: "err", label: "失败", forced: true };
      case "Cancelled": return { cls: "muted", label: "已取消", forced: true };
      default: return { cls: "muted", label: "准备中", forced: false };
    }
  }

  function renderToolGroup(entryId, startIndex, tools) {
    const first = tools[0] || {};
    const key = `${entryId}:tool:${first.toolCallId || startIndex}`;
    const metas = tools.map(tool => statusMeta(tool.status));
    const forced = metas.some(meta => meta.forced);
    const group = document.createElement("div");
    group.className = "tool-group";
    const open = !state.collapsed.has(key) && (forced || state.expanded.has(key));
    if (open) group.classList.add("open");

    const head = document.createElement("button");
    head.className = "group-head";
    const name = document.createElement("span"); name.className = "group-name";
    name.textContent = tools.length === 1 ? (first.toolName || "工具调用") : `${tools.length} 个工具`;
    const summary = document.createElement("span"); summary.className = "group-summary";
    summary.textContent = tools.length === 1 ? (first.statusText || "") : (first.toolName || "");
    const badge = document.createElement("span");
    const primary = metas.find(meta => meta.cls === "err") || metas.find(meta => meta.cls === "wait") || metas[metas.length - 1];
    badge.className = `badge ${primary.cls}`;
    badge.textContent = tools.length === 1 ? primary.label : `${metas.filter(meta => meta.cls === "ok").length}/${tools.length}`;
    const chev = document.createElement("span"); chev.className = "chev"; chev.textContent = "›";
    head.append(name, summary, badge, chev);
    head.addEventListener("click", () => toggle(group, key));

    const body = document.createElement("div"); body.className = "group-body";
    for (const tool of tools) {
      const meta = statusMeta(tool.status);
      const row = document.createElement("div"); row.className = "tool-row";
      const rowHead = document.createElement("div"); rowHead.className = "tool-row-head";
      const toolName = document.createElement("span"); toolName.className = "tool-name"; toolName.textContent = tool.toolName || "工具";
      rowHead.append(toolName);
      if (tools.length === 1 || meta.cls !== "ok") {
        const rowBadge = document.createElement("span"); rowBadge.className = `badge ${meta.cls}`; rowBadge.textContent = meta.label;
        rowHead.appendChild(rowBadge);
      }
      row.appendChild(rowHead);
      if (tool.argsJson) { const pre = document.createElement("pre"); pre.className = "tool-detail"; pre.textContent = tool.argsJson; row.appendChild(pre); }
      if (tool.output) { const pre = document.createElement("pre"); pre.className = "tool-detail"; pre.textContent = tool.output; row.appendChild(pre); }
      body.appendChild(row);
    }
    group.append(head, body);
    return group;
  }

  function updateJumpButton() {
    const button = $("jumpBottom");
    button.classList.toggle("hidden", state.nearBottom || !state.order.length);
  }

  function isNearBottom() {
    const root = $("timeline");
    return root.scrollHeight - root.scrollTop - root.clientHeight < 80;
  }

  $("timeline").addEventListener("scroll", () => {
    state.nearBottom = isNearBottom();
    updateJumpButton();
  });
  $("jumpBottom").addEventListener("click", () => {
    const root = $("timeline");
    root.scrollTop = root.scrollHeight;
    state.nearBottom = true;
    updateJumpButton();
  });

  /* ── 审批 ── */
  function renderApprovals() {
    const root = $("approvalList");
    const count = state.approvals.length;
    $("approvalCount").textContent = String(count);
    $("approvalCount").hidden = count === 0;
    $("navApprovalCount").textContent = String(count);
    $("navApprovalCount").hidden = count === 0;
    root.innerHTML = "";
    if (!count) { root.innerHTML = '<div class="placeholder">暂无待处理审批</div>'; return; }
    for (const item of state.approvals) {
      const card = document.createElement("div"); card.className = "approval";
      const name = document.createElement("div"); name.className = "approval-name"; name.textContent = item.toolName || "工具调用";
      const summary = document.createElement("div"); summary.className = "approval-summary"; summary.textContent = [item.summary, item.argsJson].filter(Boolean).join("\n");
      const actions = document.createElement("div"); actions.className = "approval-actions";
      const deny = document.createElement("button"); deny.className = "deny"; deny.textContent = "拒绝";
      const approve = document.createElement("button"); approve.className = "approve"; approve.textContent = "允许一次";
      deny.addEventListener("click", () => resolveApproval(item.approvalId, "deny"));
      approve.addEventListener("click", () => resolveApproval(item.approvalId, "approve"));
      actions.append(deny, approve);
      card.append(name, summary, actions);
      root.appendChild(card);
    }
  }

  async function resolveApproval(id, decision) {
    try {
      const response = await api("/api/approval", { method: "POST", body: JSON.stringify({ approvalId: id, decision }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || data.accepted === false) setError(data.message || "审批已失效。");
    } catch (error) { setError(error.message || "审批提交失败。"); }
  }

  /* ── 发送 / 停止 ── */
  async function send() {
    const input = $("composer");
    const text = input.value.trim();
    if (!text || !state.alive || state.sending || state.session?.canSend === false) return;
    state.sending = true; renderHeader();
    const submitted = input.value;
    try {
      const response = await api("/api/message", { method: "POST", body: JSON.stringify({ text }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || data.accepted === false) throw new Error(data.message || "消息未受理。");
      if (input.value === submitted) { input.value = ""; resizeComposer(); }
      state.nearBottom = true;
    } catch (error) { setError(error.message || "发送失败。"); }
    finally { state.sending = false; renderHeader(); }
  }

  async function stop() {
    if (!state.alive || state.stopping) return;
    state.stopping = true;
    try {
      const response = await api("/api/stop", { method: "POST", body: "{}" });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || data.accepted === false) setError(data.message || "停止未受理。");
    } catch (error) { setError(error.message || "停止失败。"); }
    finally { state.stopping = false; }
  }

  function resizeComposer() {
    const input = $("composer");
    input.style.height = "auto";
    input.style.height = `${Math.min(input.scrollHeight, 200)}px`;
  }

  /* ── 当前网页上下文（仅扩展环境可用） ── */
  function chromeAvailable() {
    return typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.sendMessage;
  }

  function refreshContext() {
    if (!chromeAvailable()) { $("pageStrip").hidden = true; return; }
    chrome.runtime.sendMessage({ type: "WORKBENCH_CONTEXT" }, result => {
      if (chrome.runtime.lastError) return;
      state.context = result && result.url ? result : null;
      renderContext();
    });
  }

  function renderContext() {
    const context = state.context;
    const usable = !!context && /^https?:/i.test(context.url || "");
    $("pageStrip").hidden = !usable;
    document.querySelector(".action-row").hidden = !usable;
    if (!usable) return;
    $("pageTitle").textContent = context?.title || "当前网页";
    $("pageUrl").textContent = context?.url || "";
    const card = $("contextCard");
    card.innerHTML = "";
    if (!usable) { card.innerHTML = '<div class="placeholder">打开一个 http/https 网页后，这里会显示当前标签页。</div>'; return; }
    const title = document.createElement("div"); title.className = "page-title"; title.textContent = context.title || "当前网页";
    const link = document.createElement("div"); link.className = "page-link"; link.textContent = context.url;
    const host = document.createElement("div"); host.className = "page-host"; host.textContent = context.host || "";
    card.append(title, link, host);
  }

  function runBrowserAction(action) {
    if (!chromeAvailable()) return;
    chrome.runtime.sendMessage({ type: "WORKBENCH_BROWSER_ACTION", action }, result => {
      if (chrome.runtime.lastError) { setError(chrome.runtime.lastError.message); return; }
      if (result?.error) setError(result.error);
      else if (result?.message) $("draftHint").textContent = result.message;
      else if (action === "state") $("draftHint").textContent = "已读取当前页面状态。";
    });
  }

  function disconnect(clear) {
    state.generation++;
    if (state.socket) { try { state.socket.close(); } catch (error) {} }
    state.socket = null;
    state.alive = false;
    state.connecting = false;
    if (clear) { state.token = null; localStorage.removeItem(TOKEN_KEY); }
    setConnection(clear ? "未连接" : "已断开", clear ? "" : "error");
  }

  /* ── 事件 ── */
  $("pairBtn").addEventListener("click", pair);
  $("pinInput").addEventListener("keydown", event => { if (event.key === "Enter") pair(); });
  $("reconnectBtn").addEventListener("click", () => { if (state.socket) state.socket.close(); else connect(); });
  $("refreshSessions").addEventListener("click", refreshSessions);
  $("refreshContext").addEventListener("click", refreshContext);
  $("quickCollect").addEventListener("click", () => runBrowserAction("collect"));
  $("quickRead").addEventListener("click", () => runBrowserAction("state"));
  $("quickBrowser").addEventListener("click", () => openDrawer("context"));
  $("quickShot").addEventListener("click", () => runBrowserAction("screenshot"));
  $("sendBtn").addEventListener("click", send);
  $("stopBtn").addEventListener("click", stop);
  $("composer").addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) { event.preventDefault(); send(); }
  });
  $("composer").addEventListener("input", resizeComposer);

  /* ── 启动 ── */
  state.host = normalizeHost(localStorage.getItem(HOST_KEY) || "");
  state.token = localStorage.getItem(TOKEN_KEY) || null;
  $("hostInput").value = state.host;
  if (!state.token || !state.host) {
    setConnection("未连接");
    setError("");
    $("draftHint").textContent = "连接 Fufucode 桌面端后即可开始对话。";
  } else {
    connect();
  }
  refreshContext();
})();
