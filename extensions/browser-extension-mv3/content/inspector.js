// Fufucode Inspector - 网页 DOM 元素拾取器
(function () {
  let inspecting = false;
  let overlay = null;
  let tooltip = null;

  function createOverlay() {
    overlay = document.createElement("div");
    overlay.id = "__fufucode_inspector_overlay";
    overlay.style.position = "fixed";
    overlay.style.pointerEvents = "none";
    overlay.style.zIndex = "2147483647";
    overlay.style.border = "2px solid #2563EB";
    overlay.style.backgroundColor = "rgba(37, 99, 235, 0.15)";
    overlay.style.transition = "all 0.05s ease-out";
    overlay.style.display = "none";

    tooltip = document.createElement("div");
    tooltip.style.position = "absolute";
    tooltip.style.bottom = "100%";
    tooltip.style.left = "0";
    tooltip.style.background = "#2563EB";
    tooltip.style.color = "#FFFFFF";
    tooltip.style.fontSize = "11px";
    tooltip.style.padding = "2px 6px";
    tooltip.style.borderRadius = "3px";
    tooltip.style.fontFamily = "monospace";
    tooltip.style.whiteSpace = "nowrap";
    overlay.appendChild(tooltip);

    document.documentElement.appendChild(overlay);
  }

  function getUniqueSelector(el) {
    if (!el || el.nodeType !== Node.ELEMENT_NODE) return "";
    if (el.id) return `#${CSS.escape(el.id)}`;
    let path = [];
    while (el && el.nodeType === Node.ELEMENT_NODE) {
      let selector = el.nodeName.toLowerCase();
      if (el.className && typeof el.className === "string") {
        const classes = el.className.trim().split(/\s+/).filter(c => !c.startsWith("__fufucode"));
        if (classes.length > 0) {
          selector += "." + classes.slice(0, 2).map(c => CSS.escape(c)).join(".");
        }
      }
      let sib = el, nth = 1;
      while (sib = sib.previousElementSibling) {
        if (sib.nodeName.toLowerCase() === el.nodeName.toLowerCase()) nth++;
      }
      if (nth > 1) selector += `:nth-of-type(${nth})`;
      path.unshift(selector);
      el = el.parentElement;
      if (el && el.nodeName.toLowerCase() === "body") break;
    }
    return path.join(" > ");
  }

  function onMouseMove(e) {
    if (!inspecting) return;
    const target = document.elementFromPoint(e.clientX, e.clientY);
    if (!target || target === overlay || target.id === "__fufucode_inspector_overlay") return;
    const rect = target.getBoundingClientRect();
    overlay.style.top = `${rect.top}px`;
    overlay.style.left = `${rect.left}px`;
    overlay.style.width = `${rect.width}px`;
    overlay.style.height = `${rect.height}px`;
    overlay.style.display = "block";

    const sel = getUniqueSelector(target);
    tooltip.textContent = `${target.tagName.toLowerCase()}${target.id ? '#' + target.id : ''} | ${sel}`;
  }

  function onClick(e) {
    if (!inspecting) return;
    e.preventDefault();
    e.stopPropagation();
    const target = document.elementFromPoint(e.clientX, e.clientY);
    if (!target) return;
    const selector = getUniqueSelector(target);
    const text = target.innerText ? target.innerText.trim().slice(0, 300) : "";

    stopInspect();

    // 弹出确认并发送到 Fufucode
    chrome.runtime.sendMessage({
      type: "COLLECT_PAGE",
      payload: {
        title: document.title,
        url: window.location.href,
        content: `所选元素内容：\n${text}`,
        selector: selector
      }
    }, res => {
      alert(`已成功拾取元素并发送至 Fufucode！\n选择器：${selector}`);
    });
  }

  function onKeyDown(e) {
    if (inspecting && e.key === "Escape") {
      stopInspect();
    }
  }

  function startInspect() {
    if (inspecting) return;
    inspecting = true;
    if (!overlay) createOverlay();
    overlay.style.display = "none";
    document.addEventListener("mousemove", onMouseMove, true);
    document.addEventListener("click", onClick, true);
    document.addEventListener("keydown", onKeyDown, true);
  }

  function stopInspect() {
    inspecting = false;
    if (overlay) overlay.style.display = "none";
    document.removeEventListener("mousemove", onMouseMove, true);
    document.removeEventListener("click", onClick, true);
    document.removeEventListener("keydown", onKeyDown, true);
  }

  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg.type === "START_INSPECT") {
      startInspect();
      sendResponse({ inspecting: true });
    } else if (msg.type === "STOP_INSPECT") {
      stopInspect();
      sendResponse({ inspecting: false });
    }
  });
})();
