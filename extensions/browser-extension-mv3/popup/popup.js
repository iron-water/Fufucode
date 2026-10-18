const statusBadge = document.getElementById("status-badge");
const authPanel = document.getElementById("auth-panel");
const readyPanel = document.getElementById("ready-panel");
const offlinePanel = document.getElementById("offline-panel");
const pinInput = document.getElementById("pin-input");
const pairBtn = document.getElementById("pair-btn");
const authError = document.getElementById("auth-error");
const collectBtn = document.getElementById("collect-btn");
const inspectBtn = document.getElementById("inspect-btn");
const retryBtn = document.getElementById("retry-btn");

function showPanel(panel) {
  authPanel.classList.add("hidden");
  readyPanel.classList.add("hidden");
  offlinePanel.classList.add("hidden");
  panel.classList.remove("hidden");
}

function updateStatus() {
  statusBadge.className = "badge checking";
  statusBadge.textContent = "检测中...";

  chrome.runtime.sendMessage({ type: "CHECK_STATUS" }, res => {
    if (!res || !res.connected) {
      statusBadge.className = "badge disconnected";
      statusBadge.textContent = "未连接";
      showPanel(offlinePanel);
    } else if (res.needsAuth) {
      statusBadge.className = "badge needs-auth";
      statusBadge.textContent = "需配对";
      showPanel(authPanel);
    } else {
      statusBadge.className = "badge connected";
      statusBadge.textContent = "已连接";
      showPanel(readyPanel);
    }
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

  chrome.tabs.sendMessage(tab.id, { type: "EXTRACT_PAGE" }, pageData => {
    if (!pageData) {
      alert("无法读取当前页面内容（可能为受保护页面如 chrome://）");
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

inspectBtn.addEventListener("click", async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab || !tab.id) return;
  chrome.tabs.sendMessage(tab.id, { type: "START_INSPECT" }, () => {
    window.close();
  });
});

retryBtn.addEventListener("click", updateStatus);

// 初始化检测
updateStatus();
