/* M0 手机原型逻辑：全部状态机为本地夹具驱动，仅演示 N1 需要的交互面。
   真实语义（唯一宿主写入、命令身份/去重、审批绑定代次）由 D6 实现；
   此处仅模拟“断线→重连→快照恢复→审批代次失效”的可观察行为。 */
"use strict";
(function () {
  var F = window.FF_FIXTURE;
  var $ = function (id) { return document.getElementById(id); };

  var state = {
    screen: "connect",
    conn: "disconnected",        // disconnected | connecting | connected | faulted | hostExited
    generation: 0,               // 连接代次：断线重连后旧审批一律失效
    session: null,
    running: false,
    approvals: {},               // approvalId -> {callId, generation, detail}
    attachments: [],
    streamTimer: null,
    navStack: ["connect"]
  };

  /* ---------- 导航（真实返回路径） ---------- */
  function show(name) {
    document.querySelectorAll(".screen").forEach(function (s) {
      s.classList.toggle("active", s.id === "screen-" + name);
    });
    document.body.dataset.screen = name;
    state.screen = name;
    var top = state.navStack[state.navStack.length - 1];
    if (top !== name) state.navStack.push(name);
    // 换屏后重放连接视觉：连接页不显示横幅，其他页按真实状态恢复
    if (state.conn !== "disconnected") setConn(state.conn, null);
  }
  function back() {
    state.navStack.pop();
    var prev = state.navStack[state.navStack.length - 1] || "connect";
    document.querySelectorAll(".screen").forEach(function (s) {
      s.classList.toggle("active", s.id === "screen-" + prev);
    });
    document.body.dataset.screen = prev;
    state.screen = prev;
    if (prev === "connect") setConn("disconnected", "");
    if (prev === "list") renderLists();
  }
  document.querySelectorAll("[data-nav]").forEach(function (b) {
    b.addEventListener("click", back);
  });

  /* ---------- 连接状态与断线横幅 ---------- */
  var HOST_TEXT = F.hostStates;
  function setConn(conn, reason) {
    state.conn = conn;
    var banner = $("banner"), t = $("bannerText"), act = $("bannerAction");
    var host = $("hostStatus");
    // 连接页自身展示连接状态，不叠加断线横幅
    if (state.screen === "connect" && conn !== "hostExited") {
      banner.hidden = true;
      if (host) { host.textContent = "离线"; host.className = "pill err"; }
      updateComposer();
      return;
    }
    if (conn === "connected") {
      banner.hidden = true;
      if (host) { host.textContent = "已连接"; host.className = "pill"; }
    } else if (conn === "connecting") {
      banner.hidden = false; banner.className = "banner banner-offline";
      t.textContent = "正在重连…"; act.hidden = true;
      if (host) { host.textContent = "重连中"; host.className = "pill"; }
    } else if (conn === "hostExited") {
      banner.hidden = false; banner.className = "banner banner-offline";
      t.textContent = HOST_TEXT.hostExited;
      act.hidden = true; // 宿主退出不能靠手机重连恢复
      if (host) { host.textContent = "宿主退出"; host.className = "pill err"; }
    } else {
      banner.hidden = false; banner.className = "banner banner-offline";
      t.textContent = reason || "连接已断开，任务在电脑端继续执行";
      act.hidden = conn === "connecting"; act.textContent = "重连";
      if (host) { host.textContent = "离线"; host.className = "pill err"; }
    }
    updateComposer();
    // 任何离开 connected 的变化（断线/重连中/宿主退出）都使挂起审批失效并关闭弹窗；
    // 即使重连期间短暂保留，代次递增也会让提交时复核拒绝旧审批
    if (conn !== "connected")
      invalidateOpenApproval(conn === "hostExited" ? "宿主退出" :
        conn === "connecting" ? "重连" : "连接断开");
  }
  $("bannerAction").addEventListener("click", reconnect);
  function reconnect() {
    if (state.conn === "hostExited") return;
    setConn("connecting", "");
    setTimeout(function () {
      state.generation += 1;                 // 新连接代次
      // 审批绑定代次：跨代次的旧审批全部失效
      Object.keys(state.approvals).forEach(function (k) {
        if (state.approvals[k].generation !== state.generation) delete state.approvals[k];
      });
      setConn("connected", "");
      if (state.screen === "chat") renderTimeline(); // 快照补齐
      sysMsg("已重连（新连接代次 " + state.generation + "）：快照已恢复，旧审批已失效");
    }, 700);
  }

  /* ---------- 连接 / 配对 ---------- */
  function renderDevices() {
    var ul = $("deviceList"); ul.innerHTML = "";
    F.devices.forEach(function (d) {
      var li = document.createElement("li");
      li.innerHTML = '<div class="grow"><div class="name"></div><div class="meta"></div></div>';
      li.querySelector(".name").textContent = d.name;
      li.querySelector(".meta").textContent = d.revoked ? "已撤销" : "可连接";
      if (d.revoked) li.style.opacity = .5;
      ul.appendChild(li);
    });
  }
  $("btnConnect").addEventListener("click", function () {
    var code = $("pairCode").value.trim();
    var err = $("pairError"), st = $("connStatus");
    err.hidden = true;
    if (!/^\d{6}$/.test(code)) { err.textContent = "请输入 6 位数字配对码"; err.hidden = false; return; }
    if (code !== F.validPairCode) { err.textContent = "配对码错误或已过期"; err.hidden = false; return; }
    st.textContent = "正在连接局域网宿主…";
    state.generation = 1;
    setTimeout(function () {
      setConn("connected", "");
      st.textContent = "";
      show("list"); renderLists();
    }, 500);
  });

  /* ---------- 列表 ---------- */
  function stateDot(s) {
    return '<span class="state-dot ' +
      (s === "running" ? "run" : s === "error" ? "err" : s === "waiting" ? "wait" : "") +
      '"></span>';
  }
  function renderLists() {
    var pl = $("projectList"); pl.innerHTML = "";
    F.projects.forEach(function (p) {
      var li = document.createElement("li");
      li.innerHTML = '<div class="grow"><div class="name"></div><div class="meta"></div></div>';
      li.querySelector(".name").textContent = p.name;
      li.querySelector(".meta").textContent = p.path;
      pl.appendChild(li);
    });
    var sl = $("sessionList"); sl.innerHTML = "";
    F.sessions.forEach(function (s) {
      var li = document.createElement("li");
      li.innerHTML = stateDot(s.state) +
        '<div class="grow"><div class="name"></div><div class="meta"></div></div>' +
        (s.unreadApproval ? '<span class="badge">' + s.unreadApproval + "</span>" : "") +
        '<button class="go" aria-label="打开">›</button>';
      li.querySelector(".name").textContent = s.title;
      li.querySelector(".meta").textContent =
        (s.state === "running" ? "运行中" : s.state === "error" ? "上次中断" : "空闲") +
        (s.unreadApproval ? " · 有待审批" : "");
      li.querySelector(".go").addEventListener("click", function () { openSession(s); });
      li.addEventListener("click", function (e) {
        if (e.target.tagName !== "BUTTON") openSession(s);
      });
      sl.appendChild(li);
    });
  }

  /* ---------- 聊天 ---------- */
  function openSession(s) {
    state.session = s;
    $("chatTitle").textContent = s.title;
    updateChatState();
    renderTimeline();
    show("chat");
  }
  function updateChatState() {
    var el = $("chatState"), s = state.session;
    var label = state.conn !== "connected" ? "离线"
      : state.running ? "运行中"
      : s && s.state === "error" ? "上次中断" : "空闲";
    el.textContent = label;
    el.className = "pill" + (label === "离线" || label === "上次中断" ? " err" : "");
  }
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function renderTimeline() {
    var tl = $("timeline"); tl.innerHTML = "";
    (state.session ? state.session.messages : []).forEach(function (m) {
      tl.appendChild(renderMsg(m));
    });
    tl.scrollTop = tl.scrollHeight;
  }
  function renderMsg(m) {
    if (m.role === "tool") {
      var card = el("div", "tool-card");
      var head = el("header");
      head.appendChild(stateIcon(m.status));
      head.appendChild(el("span", null, m.name));
      var tag = el("span", m.status === "ok" ? "t-ok" : m.status === "pending_approval" ? "t-pending" : "t-err",
        m.status === "ok" ? "已完成" : m.status === "pending_approval" ? "待审批" : "失败");
      head.appendChild(tag);
      card.appendChild(head);
      card.appendChild(el("pre", null, m.detail));
      var appr = m.approvalId && state.approvals[m.approvalId];
      if (m.status === "pending_approval" && appr && appr.generation === state.generation) {
        var btn = el("button", "text-btn", "查看审批");
        btn.addEventListener("click", function () { openApproval(m.approvalId); });
        card.appendChild(btn);
      } else if (m.status === "pending_approval") {
        card.appendChild(el("div", "meta t-err", "该审批已失效（连接代次变化），等待新审批"));
      }
      return card;
    }
    var cls = m.role === "user" ? "user" : m.role === "system" ? "sys" : "bot";
    var div = el("div", "msg " + cls);
    var b = el("div", "bubble", m.text);
    if (m.role === "assistant-streaming") b.appendChild(el("span", "cursor"));
    div.appendChild(b);
    return div;
  }
  function stateIcon(st) {
    return el("span", st === "ok" ? "t-ok" : st === "pending_approval" ? "t-pending" : "t-err",
      st === "ok" ? "✔" : st === "pending_approval" ? "⏸" : "✖");
  }
  function sysMsg(text) {
    if (state.screen !== "chat" || !state.session) return;
    var m = { role: "system", text: text };
    state.session.messages.push(m);
    var div = renderMsg(m);
    var tl = $("timeline"); tl.appendChild(div); tl.scrollTop = tl.scrollHeight;
  }

  /* ---------- 发送 / 停止 / 流式 ---------- */
  var input = $("input");
  input.addEventListener("input", function () {
    input.style.height = "auto";
    input.style.height = Math.min(input.scrollHeight, 120) + "px";
  });
  function updateComposer() {
    var offline = state.conn !== "connected";
    $("btnSend").hidden = state.running;
    $("btnStop").hidden = !state.running;
    $("btnSend").disabled = offline;
    input.disabled = offline;
    $("btnAttach").disabled = offline;
    updateChatState();
  }
  function send() {
    var text = input.value.trim();
    if (!text && !state.attachments.length) return;
    if (state.conn !== "connected") { sysMsg("未连接：消息未发送"); return; }
    var msg = { role: "user", text: text || "（仅附件）" };
    if (state.attachments.length) {
      msg.text += "\n📎 " + state.attachments.map(function (a) { return a.name; }).join("、");
    }
    state.session.messages.push(msg);
    $("timeline").appendChild(renderMsg(msg));
    input.value = ""; input.style.height = "auto";
    state.attachments = []; renderChips();
    $("timeline").scrollTop = $("timeline").scrollHeight;
    state.running = true; updateComposer();
    streamReply();
  }
  $("btnSend").addEventListener("click", send);
  input.addEventListener("keydown", function (e) {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); send(); }
  });
  function streamReply() {
    var m = { role: "assistant-streaming", text: "", cancelled: false };
    state.session.messages.push(m);
    var div = renderMsg(m); var tl = $("timeline");
    tl.appendChild(div); tl.scrollTop = tl.scrollHeight;
    var i = 0;
    state.streamTimer = setInterval(function () {
      if (m.cancelled) return;
      if (i >= F.demoReply.length) {
        clearInterval(state.streamTimer); state.streamTimer = null;
        m.role = "assistant";
        // 演示：回复结束后出现一个绑定当前连接代次的待审批工具调用
        var tool = F.demoToolAfterReply;
        var apId = "ap-live-" + Date.now();
        m.tool = { role: "tool", name: tool.name, status: "pending_approval",
          approvalId: apId, callId: "call-live-" + state.generation,
          detail: tool.detail };
        state.approvals[apId] = { name: tool.name, callId: m.tool.callId,
          detail: tool.detail, generation: state.generation };
        state.session.messages.push(m.tool);
        updateComposer(); renderTimeline();
        sysMsg("工具调用需要审批：" + tool.name);
        return;
      }
      m.text += F.demoReply[i++];
      // 重渲染后 div 可能已脱离 DOM：只更新模型，等下次 renderTimeline
      if (div.isConnected) {
        div.querySelector(".bubble").firstChild.textContent = m.text;
        tl.scrollTop = tl.scrollHeight;
      }
    }, 220);
  }
  $("btnStop").addEventListener("click", function () {
    if (!state.running) return;
    if (state.streamTimer) { clearInterval(state.streamTimer); state.streamTimer = null; }
    state.running = false; updateComposer();
    var last = state.session.messages[state.session.messages.length - 1];
    if (last && last.role === "assistant-streaming") {
      last.cancelled = true; last.role = "assistant";
    }
    // 夹具语义：clear_queue + abort；排队内容不自动重发
    sysMsg("已发送停止（clear_queue + abort）：运行中断，未执行队列不自动重发");
    renderTimeline();
  });

  /* ---------- 审批 ---------- */
  var currentApproval = null;      // {id, callId, generation} 打开时快照身份
  function approvalValid(a) {
    return !!a && state.conn === "connected" && a.generation === state.generation;
  }
  function openApproval(id) {
    var a = state.approvals[id];
    if (!approvalValid(a)) { sysMsg("审批已失效"); return; }
    currentApproval = { id: id, callId: a.callId, generation: a.generation };
    $("apprTitle").textContent = "批准执行：" + a.name;
    $("apprDetail").textContent = a.detail;
    $("approval").hidden = false;
  }
  // 连接代次变化或宿主退出：挂起审批一律失效，弹窗关闭，决不再允许提交旧身份
  function invalidateOpenApproval(reason) {
    if ($("approval").hidden || !currentApproval) return;
    closeApproval();
    sysMsg("审批已失效（" + reason + "），等待新的审批");
    renderTimeline();
  }
  function closeApproval() { $("approval").hidden = true; currentApproval = null; }
  function resolveApproval(decision) {
    var snap = currentApproval;    // 先取快照再关弹窗
    closeApproval();
    if (!snap) return;
    // 提交时复核：连接、审批仍存在、代次未变、调用身份一致——任何一项不符即拒绝生效
    var a = state.approvals[snap.id];
    if (!approvalValid(a) || a.callId !== snap.callId || a.generation !== snap.generation) {
      sysMsg("审批已失效，本次" + (decision === "approve" ? "允许" : "拒绝") + "未生效，等待新的审批");
      renderTimeline();
      return;
    }
    delete state.approvals[snap.id];
    state.session.messages.forEach(function (m) {
      if (m.approvalId === snap.id && m.callId === snap.callId)
        m.status = decision === "approve" ? "ok" : "err";
    });
    if (state.session.unreadApproval) state.session.unreadApproval = 0;
    renderTimeline();
    sysMsg(decision === "approve" ? "已允许本次执行" : "已拒绝执行");
    state.running = false; updateComposer();
  }
  $("btnApprove").addEventListener("click", function () { resolveApproval("approve"); });
  $("btnAlways").addEventListener("click", function () { resolveApproval("approve"); });
  $("btnDeny").addEventListener("click", function () { resolveApproval("deny"); });

  /* ---------- 附件 ---------- */
  $("btnAttach").addEventListener("click", function () { $("attachSheet").hidden = false; });
  document.querySelectorAll("[data-att]").forEach(function (b) {
    b.addEventListener("click", function () {
      $("attachSheet").hidden = true;
      var t = b.dataset.att;
      if (t === "cancel") return;
      var names = { photo: "IMG_20261011.jpg", file: "需求说明.pdf", library: "资料库：接口契约.md" };
      state.attachments.push({ kind: t, name: names[t] });
      renderChips();
    });
  });
  $("attachSheet").addEventListener("click", function (e) {
    if (e.target === $("attachSheet")) $("attachSheet").hidden = true;
  });
  function renderChips() {
    var c = $("chips"); c.innerHTML = "";
    c.hidden = state.attachments.length === 0;
    state.attachments.forEach(function (a, i) {
      var chip = el("span", "chip");
      chip.appendChild(el("span", null, a.name));
      var x = el("button", null, "✕");
      x.setAttribute("aria-label", "移除附件");
      x.addEventListener("click", function () { state.attachments.splice(i, 1); renderChips(); });
      chip.appendChild(x);
      c.appendChild(chip);
    });
  }

  /* ---------- 键盘适配：visualViewport 兜底 ---------- */
  if (window.visualViewport) {
    window.visualViewport.addEventListener("resize", function () {
      var vv = window.visualViewport;
      document.body.style.height = vv.height + "px";
      var tl = $("timeline");
      if (tl) tl.scrollTop = tl.scrollHeight;
    });
  }

  /* ---------- 原型夹具模拟条（真实产品由事件驱动） ---------- */
  document.querySelectorAll("[data-sim]").forEach(function (b) {
    b.addEventListener("click", function () {
      var k = b.dataset.sim;
      if (k === "drop") setConn("disconnected", HOST_TEXT.sleep + "，任务在电脑端继续执行");
      if (k === "hostExit") setConn("hostExited", "");
      if (k === "restore") reconnect();
    });
  });

  /* ---------- 初始化：登记夹具中的待审批（绑定当前代次，重连即失效） ---------- */
  function seedApprovals() {
    F.sessions.forEach(function (s) {
      s.messages.forEach(function (m) {
        if (m.status === "pending_approval" && m.approvalId) {
          state.approvals[m.approvalId] = {
            name: m.name, callId: m.callId, detail: m.detail,
            generation: 0 // 刻意使用旧代次：演示“连接后/重连后旧审批失效”
          };
        }
      });
    });
  }
  seedApprovals();
  renderDevices();
  show("connect");
  setConn("disconnected", "");

  /* 夹具截图入口：?scene=list|chat|approval|offline 直接进入对应状态，
     仅用于无头渲染证据，真实流程不受影响。 */
  var scene = new URLSearchParams(location.search).get("scene");
  if (scene && scene !== "connect") {
    state.generation = 1;
    setConn("connected", "");
    show("list"); renderLists();
    if (scene === "chat" || scene === "approval" || scene === "offline") {
      openSession(F.sessions[0]);
    }
    if (scene === "offline") {
      setConn("disconnected", HOST_TEXT.sleep + "，任务在电脑端继续执行");
    }
    if (scene === "approval") {
      // 生成一个当前代次的待审批并直接弹出审批页
      var apId = "ap-scene";
      state.approvals[apId] = { name: "edit",
        callId: "call-scene-1", detail: "edit src/Demo.cs\n+ 分组标题渲染",
        generation: state.generation };
      state.session.messages.push({ role: "tool", name: "edit",
        status: "pending_approval", approvalId: apId,
        callId: "call-scene-1", detail: "edit src/Demo.cs\n+ 分组标题渲染" });
      renderTimeline();
      openApproval(apId);
    }
  }
})();
