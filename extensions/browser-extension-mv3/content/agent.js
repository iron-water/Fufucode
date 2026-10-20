// Fufucode external browser agent - executes DOM actions on behalf of the desktop app.
(function () {
  const SENSITIVE = /(submit|buy|pay|purchase|checkout|place order|delete|remove|confirm|submit order|提交|购买|支付|下单|删除|移除|确认)/i;

  function find(selector) {
    try { return selector ? document.querySelector(selector) : null; } catch (e) { return null; }
  }

  function selectorOf(el) {
    if (!el) return "";
    if (el.id) return "#" + CSS.escape(el.id);
    let path = [];
    while (el && el.nodeType === Node.ELEMENT_NODE && el !== document.body) {
      let part = el.tagName.toLowerCase();
      if (typeof el.className === "string" && el.className.trim()) {
        const classes = el.className.trim().split(/\s+/).filter(c => !c.startsWith("__fufucode")).slice(0, 2);
        if (classes.length) part += "." + classes.map(c => CSS.escape(c)).join(".");
      }
      let sib = el, nth = 1;
      while ((sib = sib.previousElementSibling)) {
        if (sib.tagName === el.tagName) nth++;
      }
      if (nth > 1) part += ":nth-of-type(" + nth + ")";
      path.unshift(part);
      el = el.parentElement;
    }
    return path.join(" > ");
  }

  function describe(el) {
    const text = ((el.innerText || el.value || el.getAttribute("aria-label") || "") + "").trim().slice(0, 160);
    return { selector: selectorOf(el), text: text, tag: el.tagName.toLowerCase() };
  }

  function state() {
    return {
      ok: true,
      url: location.href,
      title: document.title,
      readyState: document.readyState,
      textLength: (document.body ? document.body.innerText.length : 0)
    };
  }

  function click(args) {
    const el = find(args.selector);
    if (!el) return { ok: false, error: "未找到元素：" + args.selector };
    const info = describe(el);
    if (SENSITIVE.test(info.text) && !args.confirmed) {
      return { needsConfirmation: true, text: info.text, selector: info.selector };
    }
    try { el.scrollIntoView({ block: "center", inline: "center" }); } catch (e) { }
    el.click();
    return { ok: true, url: location.href, title: document.title, ...info };
  }

  function type(args) {
    const el = find(args.selector);
    if (!el) return { ok: false, error: "未找到输入元素：" + args.selector };
    const value = args.text == null ? "" : String(args.text);
    el.focus();
    if (args.clear) {
      if ("value" in el) el.value = "";
      else el.textContent = "";
    }
    if ("value" in el) el.value = (args.clear ? "" : el.value) + value;
    else el.textContent = (args.clear ? "" : el.textContent) + value;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    return { ok: true, ...describe(el) };
  }

  function hover(args) {
    const el = find(args.selector);
    if (!el) return { ok: false, error: "未找到元素：" + args.selector };
    const rect = el.getBoundingClientRect();
    const init = { bubbles: true, clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2 };
    el.dispatchEvent(new MouseEvent("mouseover", init));
    el.dispatchEvent(new MouseEvent("mouseenter", init));
    el.dispatchEvent(new MouseEvent("mousemove", init));
    return { ok: true, ...describe(el) };
  }

  function scroll(args) {
    const left = Number(args.x || 0);
    const top = Number(args.y || args.dy || 0);
    window.scrollBy({ left: left, top: top, behavior: "instant" });
    return { ok: true, scrollX: window.scrollX, scrollY: window.scrollY };
  }

  function key(args) {
    const key = String(args.key || "");
    const target = find(args.selector) || document.activeElement || document.body;
    if (key === "Enter" && target && target.closest && target.closest("form") && !args.confirmed) {
      return { needsConfirmation: true, text: "提交表单（Enter）" };
    }
    const init = { key: key, code: key, bubbles: true, cancelable: true, ctrlKey: !!args.ctrl, shiftKey: !!args.shift, altKey: !!args.alt, metaKey: !!args.meta };
    target.dispatchEvent(new KeyboardEvent("keydown", init));
    target.dispatchEvent(new KeyboardEvent("keypress", init));
    target.dispatchEvent(new KeyboardEvent("keyup", init));
    return { ok: true, key: key, target: describe(target).selector };
  }

  function select(args) {
    const el = find(args.selector);
    if (!el) return { ok: false, error: "未找到元素：" + args.selector };
    el.focus();
    el.value = args.value == null ? "" : String(args.value);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    return { ok: true, value: el.value, ...describe(el) };
  }

  function getDom(args) {
    const root = find(args.selector) || document.body;
    if (!root) return { ok: false, error: "页面没有可读取的 DOM。" };
    const maxText = Number(args.maxText || 8000);
    const maxHtml = Number(args.maxHtml || 20000);
    return {
      ok: true,
      url: location.href,
      title: document.title,
      text: (root.innerText || "").slice(0, maxText),
      html: (root.outerHTML || "").slice(0, maxHtml)
    };
  }

  function wait(args) {
    const selector = args.selector;
    const timeout = Math.max(100, Math.min(Number(args.timeoutMs || 5000), 30000));
    return new Promise(resolve => {
      if (!selector) { resolve({ ok: false, error: "缺少 selector。" }); return; }
      const started = Date.now();
      (function probe() {
        if (find(selector)) { resolve({ ok: true, found: true, elapsedMs: Date.now() - started }); return; }
        if (Date.now() - started >= timeout) { resolve({ ok: false, found: false, error: "等待超时：" + selector }); return; }
        setTimeout(probe, 100);
      })();
    });
  }

  async function run(action, args) {
    switch (action) {
      case "state": return state();
      case "click": return click(args);
      case "type": return type(args);
      case "hover": return hover(args);
      case "scroll": return scroll(args);
      case "key": return key(args);
      case "select": return select(args);
      case "get_dom": return getDom(args);
      case "wait": return await wait(args);
      default: return { ok: false, error: "未知的页面动作：" + action };
    }
  }

  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg && msg.type === "AGENT_RUN") {
      Promise.resolve(run(msg.action, msg.arguments || {}))
        .then(result => sendResponse(result))
        .catch(error => sendResponse({ ok: false, error: String(error) }));
      return true;
    }
  });
})();
