(() => {
  "use strict";

  const TOKEN_KEY = "fufucode.workbench.token";
  const HOST_KEY = "fufucode.workbench.host";
  const SESSION_KEY = "fufucode.workbench.session";
  const state = {
    host: "",
    token: null,
    pin: "",
    socket: null,
    reconnectTimer: null,
    retry: 0,
    connecting: false,
    alive: false,
    session: null,
    sessions: [],
    entries: [],
    approvals: [],
    context: null,
    draft: "",
    sending: false,
    generation: 0,
  };

  const $ = id => document.getElementById(id);
  const escapeText = value => String(value ?? "");

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

  async function pair() {
    state.host = normalizeHost($("hostInput").value);
    state.pin = $("pinInput").value.trim();
    setError("");
    if (!state.host || !/^\d{4}$/.test(state.pin)) {
      setError("请输入远控服务地址和 4 位 PIN。");
      return;
    }
    $("pairBtn").disabled = true;
    try {
      const response = await api("/api/pair", { method: "POST", body: JSON.stringify({ pin: state.pin }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || !data.success || !data.token) throw new Error(data.error || "配对失败。");
      state.token = data.token;
      localStorage.setItem(HOST_KEY, state.host);
      localStorage.setItem(TOKEN_KEY, state.token);
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
    setConnection("连接中…");
    const wsUrl = state.host.replace(/^http/i, "ws") + `/api/events?token=${encodeURIComponent(state.token)}`;
    let socket;
    try { socket = new WebSocket(wsUrl); } catch (error) { onSocketFailure(error); return; }
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
    socket.onerror = () => { };
    socket.onclose = () => {
      if (state.socket !== socket) return;
      state.socket = null;
      state.alive = false;
      state.connecting = false;
      setConnection("已断开，等待重连", "error");
      scheduleReconnect();
    };
  }

  function onSocketFailure(error) {
    state.connecting = false;
    state.alive = false;
    setConnection("连接失败", "error");
    setError(error?.message || "无法连接远控服务。");
    scheduleReconnect();
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
      if (response.status === 401) return disconnect(true);
      const data = await response.json();
      state.sessions = Array.isArray(data.sessions) ? data.sessions : Array.isArray(data) ? data : [];
      renderSessions();
    } catch (error) { console.warn("会话目录读取失败", error); }
  }

  async function switchSession(sessionId) {
    if (!sessionId || sessionId === state.session?.sessionId) return;
    try {
      const response = await api("/api/sessions/switch", { method: "POST", body: JSON.stringify({ sessionId }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || data.accepted === false) throw new Error(data.message || "会话切换失败。");
      localStorage.setItem(SESSION_KEY, sessionId);
      state.entries = [];
      state.approvals = [];
      renderTimeline();
      renderApprovals();
      setConnection("已切换，等待快照", "ready");
    } catch (error) { setError(error.message || "会话切换失败。"); }
  }

  function applyUpdate(update) {
    if (!update || !update.type) return;
    switch (update.type) {
      case "snapshot":
        state.session = update.session || null;
        state.entries = [...(state.session?.entries || [])];
        state.approvals = [...(state.session?.pendingApprovals || [])];
        if (state.session?.sessionId) localStorage.setItem(SESSION_KEY, state.session.sessionId);
        renderAll();
        break;
      case "entry":
        upsertEntry(update.entry);
        renderTimeline();
        break;
      case "approvalAdded":
        if (!state.approvals.some(item => item.approvalId === update.approval?.approvalId)) state.approvals.push(update.approval);
        renderApprovals();
        renderTimeline();
        break;
      case "approvalResolved":
        state.approvals = state.approvals.filter(item => item.approvalId !== update.resolution?.approvalId);
        renderApprovals();
        renderTimeline();
        break;
      case "status":
        state.session = state.session || { title: "当前会话", entries: [], pendingApprovals: [] };
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

  function upsertEntry(entry) {
    if (!entry) return;
    const index = state.entries.findIndex(item => item.id === entry.id);
    if (index < 0) state.entries.push(entry); else state.entries[index] = entry;
  }

  function renderAll() { renderHeader(); renderSessions(); renderTimeline(); renderApprovals(); renderContext(); }

  function renderHeader() {
    const session = state.session;
    if (!session) return;
    $("sessionTitle").textContent = session.title || "Fufucode 会话";
    $("sessionMeta").textContent = [session.model, session.activity, session.connection].filter(Boolean).join(" · ") || "已连接";
    $("stopBtn").classList.toggle("hidden", !(state.alive && session.canStop));
    $("queueText").textContent = session.queuedCount ? `队列 ${session.queuedCount}` : "";
    $("sendBtn").disabled = !(state.alive && session.canSend !== false) || state.sending;
  }

  function renderSessions() {
    const root = $("sessionList");
    root.innerHTML = "";
    if (!state.sessions.length) { root.innerHTML = '<div class="empty">暂无会话目录</div>'; return; }
    state.sessions.forEach(item => {
      const id = item.sessionId || item.id;
      const row = document.createElement("div");
      row.className = `session ${id === state.session?.sessionId ? "active" : ""}`;
      row.innerHTML = `<div class="session-title"></div><div class="session-meta"></div>`;
      row.querySelector(".session-title").textContent = item.title || item.name || "未命名会话";
      row.querySelector(".session-meta").textContent = [item.projectName || item.project, item.backendName || item.backend].filter(Boolean).join(" · ") || "Fufucode";
      row.addEventListener("click", () => switchSession(id));
      root.appendChild(row);
    });
  }

  function renderTimeline() {
    const root = $("timeline");
    root.innerHTML = "";
    if (!state.entries.length) {
      root.innerHTML = '<div class="welcome"><div class="welcome-mark">✦</div><h2>浏览器里的 Fufucode</h2><p>在下方输入消息，或从右侧把当前网页作为上下文引用给 Agent。</p></div>';
      return;
    }
    state.entries.forEach(entry => {
      const wrapper = document.createElement("article");
      wrapper.className = `entry ${String(entry.role).toLowerCase()}`;
      (entry.blocks || []).forEach(block => {
        if (block.kind === "thinking") {
          const node = document.createElement("div"); node.className = "thinking"; node.textContent = block.text || ""; wrapper.appendChild(node);
        } else if (block.kind === "tool") wrapper.appendChild(renderTool(block.tool));
        else if (block.kind === "error") { const node = document.createElement("div"); node.className = "bubble"; node.textContent = block.message || ""; wrapper.appendChild(node); }
        else { const node = document.createElement("div"); node.className = "bubble"; node.textContent = block.text || ""; wrapper.appendChild(node); }
      });
      root.appendChild(wrapper);
    });
    root.scrollTop = root.scrollHeight;
  }

  function renderTool(tool) {
    const card = document.createElement("div"); card.className = "tool";
    const head = document.createElement("div"); head.className = "tool-head";
    head.innerHTML = `<strong></strong><span class="tool-status"></span>`;
    head.querySelector("strong").textContent = tool?.toolName || "工具调用";
    const status = String(tool?.status || "Pending");
    const statusNode = head.querySelector(".tool-status");
    statusNode.textContent = status;
    statusNode.className = `tool-status ${status === "Completed" ? "ok" : status === "Failed" || status === "Cancelled" ? "error" : "pending"}`;
    card.appendChild(head);
    const detail = document.createElement("pre");
    detail.textContent = [tool?.statusText, tool?.argsJson, tool?.output ? `———\n${tool.output}` : ""].filter(Boolean).join("\n");
    card.appendChild(detail);
    return card;
  }

  function renderApprovals() {
    const root = $("approvalList"); root.innerHTML = "";
    if (!state.approvals.length) { root.innerHTML = '<div class="context-placeholder">暂无待处理审批</div>'; return; }
    state.approvals.forEach(item => {
      const node = document.createElement("div"); node.className = "approval";
      node.innerHTML = '<div class="approval-name"></div><div class="approval-summary"></div><div class="approval-actions"><button class="danger">拒绝</button><button class="primary">允许一次</button></div>';
      node.querySelector(".approval-name").textContent = item.toolName || "工具调用";
      node.querySelector(".approval-summary").textContent = [item.summary, item.argsJson].filter(Boolean).join("\n");
      node.querySelector(".danger").addEventListener("click", () => resolveApproval(item.approvalId, "deny"));
      node.querySelector(".primary").addEventListener("click", () => resolveApproval(item.approvalId, "approve"));
      root.appendChild(node);
    });
  }

  async function resolveApproval(id, decision) {
    try {
      const response = await api("/api/approval", { method: "POST", body: JSON.stringify({ approvalId: id, decision }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || data.accepted === false) setError(data.message || "审批已失效。");
    } catch (error) { setError(error.message || "审批提交失败。"); }
  }

  async function send() {
    const text = $("composer").value.trim();
    if (!text || !state.alive || state.sending || state.session?.canSend === false) return;
    state.sending = true; renderHeader();
    const submitted = $("composer").value;
    try {
      const response = await api("/api/message", { method: "POST", body: JSON.stringify({ text }) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || data.accepted === false) throw new Error(data.message || "消息未受理。");
      if ($("composer").value === submitted) { $("composer").value = ""; resizeComposer(); }
    } catch (error) { setError(error.message || "发送失败。"); }
    finally { state.sending = false; renderHeader(); }
  }

  async function stop() {
    if (!state.alive) return;
    try { await api("/api/stop", { method: "POST", body: "{}" }); }
    catch (error) { setError(error.message || "停止失败。"); }
  }

  async function refreshContext() {
    if (typeof chrome === "undefined" || !chrome.runtime || !chrome.runtime.sendMessage) return;
    chrome.runtime.sendMessage({ type: "WORKBENCH_CONTEXT" }, result => {
      if (chrome.runtime.lastError) return;
      state.context = result || null;
      renderContext();
    });
  }

  function renderContext() {
    const root = $("contextCard");
    const context = state.context;
    if (!context?.url) { root.innerHTML = '<div class="context-placeholder">打开一个网页后，这里会显示当前标签页。</div>'; return; }
    root.innerHTML = '<div class="context-title"></div><div class="context-url"></div><div class="context-actions"><button class="secondary">引用当前页面</button><button class="secondary">读取页面状态</button></div>';
    root.querySelector(".context-title").textContent = context.title || "当前页面";
    root.querySelector(".context-url").textContent = context.url;
    root.querySelectorAll("button")[0].addEventListener("click", () => runBrowserAction("collect"));
    root.querySelectorAll("button")[1].addEventListener("click", () => runBrowserAction("state"));
  }

  function runBrowserAction(action) {
    if (typeof chrome === "undefined" || !chrome.runtime || !chrome.runtime.sendMessage) return;
    chrome.runtime.sendMessage({ type: "WORKBENCH_BROWSER_ACTION", action }, result => {
      if (chrome.runtime.lastError) setError(chrome.runtime.lastError.message);
      else if (result?.error) setError(result.error);
      else if (result?.message) $("draftHint").textContent = result.message;
    });
  }

  function resizeComposer() { const el = $("composer"); el.style.height = "auto"; el.style.height = `${Math.min(el.scrollHeight, 180)}px`; }
  function disconnect(clear = false) {
    state.generation++;
    if (state.socket) state.socket.close();
    state.token = clear ? null : state.token;
    state.alive = false;
    state.connecting = false;
    if (clear) { localStorage.removeItem(TOKEN_KEY); localStorage.removeItem(SESSION_KEY); }
    setConnection(clear ? "未连接" : "已断开", clear ? "" : "error");
  }

  $("pairBtn").addEventListener("click", pair);
  $("pinInput").addEventListener("keydown", event => { if (event.key === "Enter") pair(); });
  $("sendBtn").addEventListener("click", send);
  $("composer").addEventListener("keydown", event => { if (event.key === "Enter" && !event.shiftKey && !event.isComposing) { event.preventDefault(); send(); } });
  $("composer").addEventListener("input", resizeComposer);
  $("stopBtn").addEventListener("click", stop);
  $("reconnectBtn").addEventListener("click", () => { if (state.socket) state.socket.close(); else connect(); });
  $("refreshSessions").addEventListener("click", refreshSessions);
  $("refreshContext").addEventListener("click", refreshContext);
  $("openExternal").addEventListener("click", refreshContext);

  state.host = normalizeHost(localStorage.getItem(HOST_KEY) || "");
  state.token = localStorage.getItem(TOKEN_KEY) || null;
  $("hostInput").value = state.host;
  if (state.token && state.host) connect(); else setConnection("需要连接");
  refreshContext();
})();
