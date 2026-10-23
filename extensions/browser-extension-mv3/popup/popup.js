const statusBadge = document.getElementById("status-badge");
const authPanel = document.getElementById("auth-panel");
const readyPanel = document.getElementById("ready-panel");
const offlinePanel = document.getElementById("offline-panel");
const pinInput = document.getElementById("pin-input");
const pairBtn = document.getElementById("pair-btn");
const authError = document.getElementById("auth-error");
const collectBtn = document.getElementById("collect-btn");
const inspectBtn = document.getElementById("inspect-btn");
const workbenchBtn = document.getElementById("workbench-btn");
const retryBtn = document.getElementById("retry-btn");
const hostInput = document.getElementById("host-input");
const saveHostBtn = document.getElementById("save-host-btn");
const hostHint = document.getElementById("host-hint");
const browserDetail = document.getElementById("browser-detail");
const pageDetail = document.getElementById("page-detail");
const tabsDetail = document.getElementById("tabs-detail");
const capabilitiesDetail = document.getElementById("capabilities-detail");
const handshakeBtn = document.getElementById("handshake-btn");
const readyError = document.getElementById("ready-error");

function showPanel(panel) {
  authPanel.classList.add("hidden");
  readyPanel.classList.add("hidden");
  offlinePanel.classList.add("hidden");
  panel.classList.remove("hidden");
}

function updateDiagnostics(status) {
  const data = status || {};
  if (browserDetail) browserDetail.textContent = [data.browser, data.browserVersion].filter(Boolean).join(" ") || "未识别";
  if (pageDetail) pageDetail.textContent = data.activeUrl || "没有活动页面";
  if (tabsDetail) tabsDetail.textContent = data.tabCount == null ? "未知" : `${data.tabCount} 个`;
  if (capabilitiesDetail) capabilitiesDetail.textContent = Array.isArray(data.capabilities) && data.capabilities.length
    ? data.capabilities.join(", ") : "未上报";
}

function updateStatus() {
  statusBadge.className = "badge checking";
  statusBadge.textContent = "检测中...";

  chrome.runtime.sendMessage({ type: "CHECK_STATUS" }, res => {
    if (!res || !res.connected) {
      statusBadge.className = "badge disconnected";
      statusBadge.textContent = "未连接";
      showPanel(offlinePanel);
      return;
    }
    if (res.needsAuth || !res.paired) {
      statusBadge.className = "badge needs-auth";
      statusBadge.textContent = "需配对";
      showPanel(authPanel);
      return;
    }
    statusBadge.className = res.ready ? "badge connected" : "badge needs-auth";
    statusBadge.textContent = res.ready ? "已就绪" : "等待握手";
    updateDiagnostics(res.status);
    if (readyError) {
      readyError.textContent = res.ready ? "" : "桥已连接，但扩展握手尚未完成。点击“重新握手”或检查桌面端日志。";
      readyError.classList.toggle("hidden", !!res.ready);
    }
    showPanel(readyPanel);
  });
}

pairBtn.addEventListener("click", () => {
  const pin = pinInput.value.trim();
  if (pin.length !== 4) {
    authError.textContent = "请输入 4 位纯数字 PIN 码";
    authError.classList.remove("hidden");
    return;
  }
  pairBtn.disabled = true;
  pairBtn.textContent = "配对中...";
  chrome.runtime.sendMessage({ type: "PAIR", pin: pin }, res => {
    pairBtn.disabled = false;
    pairBtn.textContent = "配对";
    if (res && res.success) {
      authError.classList.add("hidden");
      updateStatus();
    } else {
      authError.textContent = res ? res.error : "配对失败";
      authError.classList.remove("hidden");
    }
  });
});

collectBtn.addEventListener("click", async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab || !tab.id) return;

  chrome.runtime.sendMessage({ type: "EXTRACT_PAGE", tabId: tab.id }, pageData => {
    if (!pageData || pageData.ok === false) {
      alert(pageData?.error || "无法读取当前页面内容（可能为受保护页面如 chrome://）");
      return;
    }
    collectBtn.disabled = true;
    chrome.runtime.sendMessage({
      type: "COLLECT_PAGE",
      payload: {
        title: pageData.title,
        url: pageData.url,
        content: pageData.content
      }
    }, res => {
      collectBtn.disabled = false;
      if (res && res.success) {
        alert("已成功将网页引用发送至 Fufucode 对话！");
        window.close();
      } else {
        alert("发送失败：" + (res ? res.error : "未知错误"));
      }
    });
  });
});

if (workbenchBtn) workbenchBtn.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "OPEN_WORKBENCH" }, result => {
    if (!result?.ok) alert(result?.error || "无法打开工作台");
    else window.close();
  });
});

inspectBtn.addEventListener("click", async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab || !tab.id) return;
  chrome.tabs.sendMessage(tab.id, { type: "START_INSPECT" }, () => {
    window.close();
  });
});

retryBtn.addEventListener("click", updateStatus);

if (handshakeBtn) handshakeBtn.addEventListener("click", () => {
  handshakeBtn.disabled = true;
  handshakeBtn.textContent = "握手中...";
  chrome.runtime.sendMessage({ type: "HANDSHAKE" }, res => {
    handshakeBtn.disabled = false;
    handshakeBtn.textContent = "重新握手";
    if (!res || !res.success) {
      if (readyError) {
        readyError.textContent = res?.error || "握手失败";
        readyError.classList.remove("hidden");
      }
    }
    updateStatus();
  });
});

saveHostBtn.addEventListener("click", () => {
  let host = (hostInput.value || "").trim();
  if (!host) return;
  if (!/^https?:\/\//i.test(host)) host = "http://" + host;
  chrome.runtime.sendMessage({ type: "SET_HOST", host: host }, () => {
    hostHint.textContent = "已保存，正在重新连接…";
    updateStatus();
  });
});

chrome.runtime.sendMessage({ type: "CHECK_STATUS" }, res => {
  const config = res && res.status ? res.status : null;
  if (config && config.port) hostInput.value = "http://127.0.0.1:" + config.port;
  updateDiagnostics(config);
});

// 初始化检测
updateStatus();
