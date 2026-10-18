// Fufucode Assistant - Background Service Worker (Manifest V3)
const DEFAULT_HOST = "http://127.0.0.1:54321";

async function getConfig() {
  const result = await chrome.storage.local.get(["fufucodeHost", "fufucodeToken", "fufucodePin"]);
  return {
    host: result.fufucodeHost || DEFAULT_HOST,
    token: result.fufucodeToken || null,
    pin: result.fufucodePin || null
  };
}

async function checkStatus() {
  const config = await getConfig();
  try {
    const res = await fetch(`${config.host}/api/status`, {
      headers: config.token ? { "Authorization": `Bearer ${config.token}` } : {}
    });
    if (res.ok) {
      const data = await res.json();
      return { connected: true, status: data };
    } else if (res.status === 401) {
      return { connected: true, needsAuth: true };
    }
  } catch (e) {
    return { connected: false, error: e.message };
  }
  return { connected: false };
}

async function pair(pin) {
  const config = await getConfig();
  try {
    const res = await fetch(`${config.host}/api/pair`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pin: pin.trim() })
    });
    const data = await res.json();
    if (data.token) {
      await chrome.storage.local.set({ fufucodeToken: data.token, fufucodePin: pin.trim() });
      return { success: true, token: data.token };
    }
    return { success: false, error: data.message || "配对失败" };
  } catch (e) {
    return { success: false, error: e.message };
  }
}

async function sendCollect(payload) {
  const config = await getConfig();
  if (!config.token) {
    return { success: false, error: "未配对，请在插件弹窗中输入 4 位 PIN 码。" };
  }
  try {
    const res = await fetch(`${config.host}/api/extension/collect`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Bearer ${config.token}`
      },
      body: JSON.stringify(payload)
    });
    const data = await res.json();
    return { success: res.ok, data };
  } catch (e) {
    return { success: false, error: e.message };
  }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === "CHECK_STATUS") {
    checkStatus().then(sendResponse);
    return true;
  }
  if (message.type === "PAIR") {
    pair(message.pin).then(sendResponse);
    return true;
  }
  if (message.type === "COLLECT_PAGE") {
    sendCollect(message.payload).then(sendResponse);
    return true;
  }
});
