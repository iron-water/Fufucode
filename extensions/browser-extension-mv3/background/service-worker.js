// Fufucode Assistant - Background Service Worker (Manifest V3)
// 1) 页面引用 / 元素拾取（原有能力）
// 2) 设备浏览器控制：长轮询 /api/next，执行动作并回传 /api/result
const DEFAULT_HOST = "http://127.0.0.1:54321";
let configCache = null;
let polling = false;
let lastPollAt = 0;
let handshakeInFlight = null;
let lastHandshakeAt = 0;
const HANDSHAKE_INTERVAL_MS = 15000;
const CLIENT_ID_KEY = "fufucodeClientId";
const CAPABILITIES = [
  "tabs", "state", "navigate", "click", "type", "hover", "scroll", "key",
  "select", "wait", "get_dom", "screenshot", "evaluate", "collect", "inspect"
];

async function readPackagedConfig() {
  if (configCache) return configCache;
  try {
    const res = await fetch(chrome.runtime.getURL("config.json"), { cache: "no-store" });
    if (res.ok) configCache = await res.json();
  } catch (e) { }
  return configCache || {};
}

async function getConfig() {
  const result = await chrome.storage.local.get(["fufucodeHost", "fufucodeToken", "fufucodePin"]);
  const packaged = await readPackagedConfig();
  return {
    host: result.fufucodeHost || packaged.host || DEFAULT_HOST,
    token: result.fufucodeToken || null,
    pin: result.fufucodePin || packaged.pin || null
  };
}

async function getClientId() {
  const stored = await chrome.storage.local.get([CLIENT_ID_KEY]);
  if (stored[CLIENT_ID_KEY]) return stored[CLIENT_ID_KEY];
  const value = crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  await chrome.storage.local.set({ [CLIENT_ID_KEY]: value });
  return value;
}

async function browserSnapshot() {
  const tabs = await chrome.tabs.query({});
  const active = tabs.find(tab => tab.active) || tabs[0] || null;
  return {
    tabCount: tabs.length,
    activeUrl: active?.url || "",
    activeTitle: active?.title || "",
    activeTabId: active?.id ?? null
  };
}

async function handshake(force = false) {
  if (handshakeInFlight) return handshakeInFlight;
  if (!force && Date.now() - lastHandshakeAt < HANDSHAKE_INTERVAL_MS) return { success: true, skipped: true };
  handshakeInFlight = (async () => {
    const config = await getConfig();
    if (!config.token) return { success: false, error: "未配对" };
    try {
      const snapshot = await browserSnapshot();
      const response = await fetch(`${config.host}/api/extension/handshake`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "Authorization": `Bearer ${config.token}` },
        body: JSON.stringify({
          clientId: await getClientId(),
          browser: /Edg\//.test(navigator.userAgent) ? "Edge" : "Chrome",
          browserVersion: navigator.userAgent,
          capabilities: CAPABILITIES,
          ...snapshot
        })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) return { success: false, error: data.error || `握手失败（${response.status}）` };
      lastHandshakeAt = Date.now();
      return { success: true, data };
    } catch (error) {
      return { success: false, error: error.message };
    } finally {
      handshakeInFlight = null;
    }
  })();
  return handshakeInFlight;
}

async function checkStatus() {
  const config = await getConfig();
  try {
    const res = await fetch(`${config.host}/api/status`, {
      headers: config.token ? { "Authorization": `Bearer ${config.token}` } : {}
    });
    if (res.ok) {
      let data = await res.json();
      const hello = config.token ? await handshake() : { success: false };
      if (hello.success && !hello.skipped) {
        const refreshed = await fetch(`${config.host}/api/status`, {
          headers: { "Authorization": `Bearer ${config.token}` }
        }).catch(() => null);
        if (refreshed?.ok) data = await refreshed.json();
      }
      const ready = !!config.token && !!hello.success && data.handshake !== false;
      await setBadge(ready ? "✓" : config.token ? "!" : "?", ready ? "#1f7a3a" : "#9a6700");
      return { connected: true, paired: !!config.token, ready, needsHandshake: !!config.token && !hello.success, status: data };
    }
    if (res.status === 401) {
      await setBadge("?", "#9a6700");
      return { connected: true, needsAuth: true };
    }
  } catch (e) {
    await setBadge("×", "#a33");
    return { connected: false, error: e.message };
  }
  return { connected: false };
}

async function pair(pin) {
  const config = await getConfig();
  const value = (pin || config.pin || "").trim();
  if (value.length !== 4) return { success: false, error: "需要 4 位 PIN。" };
  try {
    const res = await fetch(`${config.host}/api/pair`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pin: value })
    });
    const data = await res.json();
    if (data.token) {
      await chrome.storage.local.set({ fufucodeToken: data.token, fufucodePin: value });
      await setBadge("✓", "#1f7a3a");
      return { success: true, token: data.token };
    }
    return { success: false, error: data.error || "配对失败" };
  } catch (e) {
    return { success: false, error: e.message };
  }
}

async function ensurePaired() {
  const config = await getConfig();
  if (config.token) return true;
  if (!config.pin) return false;
  const result = await pair(config.pin);
  return !!result.success;
}

async function sendCollect(payload) {
  const config = await getConfig();
  if (!config.token) return { success: false, error: "未配对，请在插件弹窗中配对。" };
  try {
    const res = await fetch(`${config.host}/api/extension/collect`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "Authorization": `Bearer ${config.token}` },
      body: JSON.stringify(payload)
    });
    const data = await res.json();
    return { success: res.ok, data };
  } catch (e) {
    return { success: false, error: e.message };
  }
}

async function setBadge(text, color) {
  try {
    await chrome.action.setBadgeText({ text: text || "" });
    if (color) await chrome.action.setBadgeBackgroundColor({ color: color });
  } catch (e) { }
}

function sleep(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

async function activeTab(tabId) {
  if (tabId != null) {
    try { return await chrome.tabs.get(Number(tabId)); } catch (e) { }
  }
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  return tab || null;
}

async function ensureContentScript(tabId) {
  try {
    await chrome.scripting.executeScript({ target: { tabId: tabId }, files: ["content/agent.js"] });
  } catch (e) { }
}

async function sendToContent(tabId, action, args) {
  const message = { type: "AGENT_RUN", action: action, arguments: args || {} };
  try {
    return await chrome.tabs.sendMessage(tabId, message);
  } catch (e) {
    await ensureContentScript(tabId);
    await sleep(120);
    return await chrome.tabs.sendMessage(tabId, message);
  }
}

function waitForLoad(tabId, timeoutMs) {
  return new Promise(resolve => {
    let done = false;
    const timer = setTimeout(() => { if (!done) { done = true; cleanup(); resolve(); } }, timeoutMs || 12000);
    function cleanup() {
      clearTimeout(timer);
      try { chrome.tabs.onUpdated.removeListener(onUpdated); } catch (e) { }
    }
    function onUpdated(id, info) {
      if (id === tabId && info.status === "complete" && !done) { done = true; cleanup(); resolve(); }
    }
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.get(tabId).then(tab => {
      if (tab && tab.status === "complete" && !done) { done = true; cleanup(); resolve(); }
    }).catch(() => { });
  });
}

async function executeCommand(command) {
  const action = command.action;
  const args = command.arguments || {};
  if (action === "state") {
    const tab = await activeTab(args.tabId);
    if (!tab) return { ok: false, error: "没有活动标签页。" };
    if (tab.url && /^https?:/i.test(tab.url)) {
      try {
        const page = await sendToContent(tab.id, "state", {});
        return { ok: true, tabId: tab.id, url: tab.url, title: tab.title || "", host: new URL(tab.url).host, page: page };
      } catch (e) { }
    }
    return { ok: true, tabId: tab.id, url: tab.url || "", title: tab.title || "", host: tab.url && /^https?:/i.test(tab.url) ? new URL(tab.url).host : "" };
  }

  if (action === "tabs") {
    const tabs = await chrome.tabs.query({});
    return {
      ok: true,
      tabs: tabs.map(tab => ({ id: tab.id, url: tab.url, title: tab.title, active: tab.active, windowId: tab.windowId }))
    };
  }

  if (action === "navigate") {
    if (!args.url) return { ok: false, error: "缺少 url。" };
    let tab = await activeTab(args.tabId);
    if (tab) {
      await chrome.tabs.update(tab.id, { url: args.url, active: true });
    } else {
      tab = await chrome.tabs.create({ url: args.url, active: true });
    }
    await waitForLoad(tab.id, args.timeoutMs || 15000);
    const fresh = await chrome.tabs.get(tab.id).catch(() => tab);
    return { ok: true, tabId: fresh.id, url: fresh.url || args.url, title: fresh.title || "", host: (fresh.url || args.url).startsWith("http") ? new URL(fresh.url || args.url).host : "" };
  }

  if (action === "open_tab") {
    const tab = await chrome.tabs.create({ url: args.url || "about:blank", active: true });
    await waitForLoad(tab.id, args.timeoutMs || 15000);
    return { ok: true, tabId: tab.id, url: tab.url || args.url || "about:blank", title: tab.title || "" };
  }

  if (action === "activate_tab") {
    const tab = await activeTab(args.tabId);
    if (!tab) return { ok: false, error: "没有可激活的标签页。" };
    await chrome.tabs.update(tab.id, { active: true });
    try { await chrome.windows.update(tab.windowId, { focused: true }); } catch (e) { }
    return { ok: true, tabId: tab.id, url: tab.url, title: tab.title };
  }

  if (action === "close_tab") {
    const tab = await activeTab(args.tabId);
    if (!tab) return { ok: false, error: "没有可关闭的标签页。" };
    await chrome.tabs.remove(tab.id);
    return { ok: true, tabId: tab.id };
  }

  if (action === "screenshot") {
    const tab = await activeTab(args.tabId);
    if (!tab) return { ok: false, error: "没有活动标签页。" };
    await chrome.tabs.update(tab.id, { active: true }).catch(() => { });
    await sleep(120);
    const format = args.format === "jpeg" ? "jpeg" : "png";
    const dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, { format: format, quality: format === "jpeg" ? 85 : undefined });
    return { ok: true, tabId: tab.id, format: format, dataUrl: dataUrl };
  }

  if (action === "evaluate") {
    const tab = await activeTab(args.tabId);
    if (!tab) return { ok: false, error: "没有活动标签页。" };
    if (!/^https?:/i.test(tab.url || "")) return { ok: false, error: "该页面不允许执行脚本。" };
    const script = String(args.script || "");
    const results = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      world: "MAIN",
      func: (code) => { try { return { value: eval(code) }; } catch (error) { return { error: String(error) }; } },
      args: [script]
    });
    const result = results && results[0] ? results[0].result : null;
    if (result && result.error) return { ok: false, error: result.error };
    let value = result ? result.value : null;
    try { value = JSON.parse(JSON.stringify(value)); } catch (e) { value = String(value); }
    return { ok: true, value: value };
  }

  // 其余动作交给内容脚本。
  const tab = await activeTab(args.tabId);
  if (!tab) return { ok: false, error: "没有活动标签页。" };
  if (!/^https?:/i.test(tab.url || "")) return { ok: false, error: "该页面不允许内容脚本操作。" };
  try {
    return await sendToContent(tab.id, action, args);
  } catch (e) {
    return { ok: false, error: "页面动作失败：" + e.message };
  }
}

async function postResult(id, result) {
  const config = await getConfig();
  if (!config.token) return;
  const ok = !!(result && result.ok !== false && !result.error);
  const body = { id: id, ok: ok };
  if (ok) body.result = result;
  else body.error = (result && result.error) || "扩展执行失败";
  try {
    await fetch(`${config.host}/api/result`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "Authorization": `Bearer ${config.token}` },
      body: JSON.stringify(body)
    });
  } catch (e) { }
}

async function poll() {
  if (polling) return;
  polling = true;
  lastPollAt = Date.now();
  try {
    const config = await getConfig();
    if (!config.token) {
      if (!(await ensurePaired())) { await setBadge("?", "#9a6700"); return; }
    }
    const tokenConfig = await getConfig();
    if (!tokenConfig.token) return;
    await handshake();
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 30000);
    let res;
    try {
      res = await fetch(`${tokenConfig.host}/api/next`, {
        headers: { "Authorization": `Bearer ${tokenConfig.token}` },
        signal: controller.signal
      });
    } finally { clearTimeout(timer); }
    if (res.status === 401) {
      await chrome.storage.local.remove("fufucodeToken");
      await setBadge("?", "#9a6700");
      return;
    }
    await setBadge("✓", "#1f7a3a");
    if (res.status === 200) {
      const command = await res.json();
      let result;
      try { result = await executeCommand(command); }
      catch (e) { result = { ok: false, error: String(e) }; }
      await postResult(command.id, result);
    }
  } catch (e) {
    // 长轮询超时/服务未启动都按正常重试处理。
  } finally {
    polling = false;
    setTimeout(poll, 700);
  }
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create("fufucode-poll", { periodInMinutes: 0.5 });
  poll();
});
chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create("fufucode-poll", { periodInMinutes: 0.5 });
  poll();
});
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === "fufucode-poll") poll();
});
poll();

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === "CHECK_STATUS") { checkStatus().then(sendResponse); return true; }
  if (message.type === "PAIR") { pair(message.pin).then(async result => { if (result.success) await handshake(true); sendResponse(result); }); return true; }
  if (message.type === "HANDSHAKE") { handshake(true).then(sendResponse); return true; }
  if (message.type === "EXTRACT_PAGE") {
    (async () => {
      const tab = await activeTab(message.tabId);
      if (!tab?.id || !/^https?:/i.test(tab.url || "")) return { ok: false, error: "无法读取当前页面（仅支持 http/https 页面）。" };
      try {
        const result = await sendToContent(tab.id, "extract_page", {});
        return result || { ok: false, error: "页面没有返回内容。" };
      } catch (error) {
        return { ok: false, error: `页面脚本不可用：${error.message}` };
      }
    })().then(sendResponse);
    return true;
  }
  if (message.type === "COLLECT_PAGE") { sendCollect(message.payload).then(sendResponse); return true; }
  if (message.type === "RECONNECT") { configCache = null; poll(); sendResponse({ ok: true }); return true; }
  if (message.type === "SET_HOST") {
    chrome.storage.local.set({ fufucodeHost: message.host, fufucodeToken: null }).then(() => {
      configCache = null; poll(); sendResponse({ ok: true });
    });
    return true;
  }
});
