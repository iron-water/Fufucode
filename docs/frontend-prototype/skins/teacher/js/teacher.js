// E7 教师样板交互：只切换 <html> 数据属性与示例浮层；无网络、无存储、无后端调用。
// URL 参数（截图复现用）：skin=fluent|aero theme=light|dark active=true|false backdrop=solid|glass
//   view=shell|settings|states statusbar=on|off menu=open dialog=open float=right chrome=off
(function () {
  "use strict";
  var root = document.documentElement;
  var q = new URLSearchParams(location.search);
  var keys = ["skin", "theme", "active", "backdrop", "view", "statusbar", "chrome"];
  keys.forEach(function (k) { if (q.has(k)) root.dataset[k] = q.get(k); });

  // ---- 内联 16px 线性图标（自绘，无第三方资源） ----
  var P = {
    search: '<circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5 14 14"/>',
    bell: '<path d="M4 11V7a4 4 0 0 1 8 0v4l1 1.5H3z"/><path d="M6.5 14h3"/>',
    gear: '<circle cx="8" cy="8" r="2.2"/><path d="M8 1.8v2M8 12.2v2M1.8 8h2M12.2 8h2M3.6 3.6l1.4 1.4M11 11l1.4 1.4M3.6 12.4 5 11M11 5l1.4-1.4"/>',
    min: '<path d="M1 5h8" stroke-width="1.6"/>',
    max: '<rect x="1" y="1" width="8" height="8" stroke-width="1.4"/>',
    close: '<path d="M1.5 1.5l7 7M8.5 1.5l-7 7" stroke-width="1.6"/>',
    plus: '<path d="M8 3v10M3 8h10"/>',
    more: '<circle cx="3.5" cy="8" r=".9"/><circle cx="8" cy="8" r=".9"/><circle cx="12.5" cy="8" r=".9"/>',
    pop: '<rect x="2" y="5" width="9" height="9" rx="1"/><path d="M8 2h6v6M14 2 8.5 7.5"/>',
    dock: '<rect x="1.5" y="1.5" width="7" height="7"/><path d="M5 4.5h3.5"/>',
    clip: '<path d="M10.5 5.5 6 10a1.5 1.5 0 0 1-2-2l5-5a2.5 2.5 0 0 1 3.5 3.5L7 12"/>',
    back: '<path d="M9.5 3.5 5 8l4.5 4.5M5 8h8"/>',
    brush: '<path d="M13 2 7 8M6 9c-2 0-3 1.5-3 4 2.5 0 4-1 4-3z"/>',
    term: '<rect x="1.5" y="2.5" width="13" height="11" rx="1"/><path d="M4 6l2 2-2 2M8 10h4"/>',
    plug: '<path d="M6 2v3M10 2v3M4 5h8v3a4 4 0 0 1-8 0zM8 12v2"/>',
    moon: '<path d="M12.5 10A5 5 0 0 1 6 3.5 5 5 0 1 0 12.5 10z"/>',
    glass: '<rect x="2" y="2.5" width="12" height="11" rx="1.5"/><path d="M2 6h12M5 9l2-2M7 11l4-4"/>',
    bar: '<rect x="1.5" y="2.5" width="13" height="11" rx="1"/><path d="M1.5 11h13"/>',
    warn: '<path d="M8 2 15 14H1z"/><path d="M8 6.5v3.5M8 12v.2"/>',
    down: '<path d="M4 6l4 4 4-4"/>',
    left: '<rect x="1.5" y="2.5" width="13" height="11" rx="1"/><path d="M6 2.5v11"/>',
    check: '<path d="M3 8.5 6.5 12 13 4.5"/>'
  };
  function icon(el) {
    var name = el.getAttribute("data-i"), small = name === "min" || name === "max" || name === "close" || name === "dock";
    var vb = small ? "0 0 10 10" : "0 0 16 16";
    el.innerHTML = '<svg viewBox="' + vb + '" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + (P[name] || "") + "</svg>";
  }

  // ---- 状态画廊：同一组件在 default/hover/pressed/focus/selected/disabled/error 下的静态示例 ----
  function gallery() {
    var states = ["default", "hover", "pressed", "focus", "disabled"];
    function cls(s) { return s === "default" ? "" : " is-" + s; }
    var capCells = ["normal", "hover", "pressed", "focus", "disabled"].map(function (s) {
      var c = s === "normal" ? "" : " is-" + s;
      return '<div class="g-cell"><small>' + s + '</small><div class="g-cap show-hit"><div class="cap-buttons">' +
        '<button class="capbtn min' + c + '" tabindex="-1"><span class="plate"></span><i data-i="min"></i></button>' +
        '<button class="capbtn max' + c + '" tabindex="-1"><span class="plate"></span><i data-i="max"></i></button>' +
        '<button class="capbtn close' + c + '" tabindex="-1"><span class="plate"></span><i data-i="close"></i></button></div></div></div>';
    }).join("");
    var inactive = '<div class="g-cell"><small>inactive window</small><div class="g-cap inactive show-hit" data-inactive><div class="cap-buttons inactive-demo">' +
      '<button class="capbtn min" tabindex="-1"><span class="plate" style="background:var(--t-cap-plate-inactive)"></span><i data-i="min" style="color:var(--t-cap-glyph-inactive);filter:none"></i></button>' +
      '<button class="capbtn max" tabindex="-1"><span class="plate" style="background:var(--t-cap-plate-inactive)"></span><i data-i="max" style="color:var(--t-cap-glyph-inactive);filter:none"></i></button>' +
      '<button class="capbtn close" tabindex="-1"><span class="plate" style="background:var(--t-cap-plate-inactive)"></span><i data-i="close" style="color:var(--t-cap-glyph-inactive);filter:none"></i></button></div></div></div>';
    var btns = states.map(function (s) {
      return '<div class="g-cell"><small>' + s + '</small><button class="btn' + cls(s) + '" tabindex="-1"' + (s === "disabled" ? " disabled" : "") + '>取消</button>' +
        '<button class="btn primary' + cls(s) + '" tabindex="-1"' + (s === "disabled" ? " disabled" : "") + '>发送</button></div>';
    }).join("") + '<div class="g-cell"><small>error</small><button class="btn is-error" tabindex="-1">重试连接</button></div>';
    var inputs = ["default", "hover", "focus", "disabled"].map(function (s) {
      return '<div class="g-cell"><small>' + s + '</small><input class="field' + cls(s) + '" value="gpt-6.1-sol" tabindex="-1"' + (s === "disabled" ? " disabled" : "") + '></div>';
    }).join("") + '<div class="g-cell"><small>error</small><input class="field invalid" value="#12G" tabindex="-1"></div>';
    var tabs = '<div class="g-cell"><small>default · hover · selected · focus · disabled</small><div class="g-tabs">' +
      '<button class="tab" tabindex="-1">文件</button><button class="tab is-hover" tabindex="-1">预览</button>' +
      '<button class="tab selected" tabindex="-1">变更</button><button class="tab is-focus" tabindex="-1">通知<span class="unread">2</span></button>' +
      '<button class="tab is-disabled" tabindex="-1" disabled>浏览器</button></div></div>';
    var rows = '<div class="g-cell"><small>settings row: default / hover / selected / focus / disabled / error</small>' +
      ['', ' is-hover', ' selected', ' is-focus', ' disabled', ' error'].map(function (c, i) {
        var t = ["皮肤", "明暗", "标题栏玻璃效果", "状态栏", "Liquid Glass（暂缓）", "自定义强调色"][i];
        var d = ["默认 Fluent", "跟随系统", "无模糊时自动实色", "默认关闭", "不在本轮范围", "“#12G” 无效，已保留旧值"][i];
        return '<div class="row g-row compact' + c + '"><div class="rt"><b>' + t + '</b><span>' + d + '</span></div></div>';
      }).join("") + "</div>";
    var list = '<div class="g-cell"><small>list item: default / hover / selected</small><ul class="list" style="width:240px">' +
      '<li class="item"><span class="dot"></span><span class="t">重构设置页导航</span></li>' +
      '<li class="item is-hover"><span class="dot wait"></span><span class="t">E2 浏览器下载</span></li>' +
      '<li class="item selected"><span class="dot run"></span><span class="t">修复登录回调竞态</span></li></ul></div>';
    var menu = '<div class="g-cell"><small>menu: item / hot / checked / disabled</small><div class="menu static">' +
      '<button class="mi" tabindex="-1"><i data-i="pop"></i>浮动此页</button><button class="mi hot" tabindex="-1"><i data-i="pop"></i>浮动整栏</button>' +
      '<div class="msep"></div><button class="mi" tabindex="-1"><i data-i="check"></i>停靠到右侧</button><button class="mi" tabindex="-1" disabled><i data-i="close"></i>关闭全部（禁用）</button></div></div>';
    var dialog = '<div class="g-cell"><small>dialog</small><div class="dialog static"><div class="dlg-cap"><span>放弃未发送的草稿？</span>' +
      '<button class="capbtn close small" tabindex="-1"><span class="plate"></span><i data-i="close"></i></button></div>' +
      '<div class="dlg-body"><p>切换会话会保留草稿。</p></div><div class="dlg-foot"><button class="btn" tabindex="-1">取消</button><button class="btn primary" tabindex="-1">放弃草稿</button></div></div></div>';
    var term = '<div class="g-cell"><small>terminal container（内容不透明、无玻璃）</small><div class="terminal g-term"><span class="p">PS&gt;</span> dotnet build\n  0 个警告  0 个错误\n<span class="ok">已成功生成。</span></div></div>';
    document.getElementById("gallery").innerHTML =
      '<div class="g-sec"><h2>窗口按钮（粉色虚线 = 46×32 命中区，不随皮肤变化）</h2><div class="g-grid">' + capCells + inactive + "</div></div>" +
      '<div class="g-sec"><h2>按钮</h2><div class="g-grid">' + btns + "</div></div>" +
      '<div class="g-sec"><h2>输入框</h2><div class="g-grid">' + inputs + "</div></div>" +
      '<div class="g-sec"><h2>页签</h2><div class="g-grid">' + tabs + "</div></div>" +
      '<div class="g-sec"><h2>设置行 / 列表 / 菜单</h2><div class="g-grid">' + rows + list + menu + "</div></div>" +
      '<div class="g-sec"><h2>对话框 / 终端</h2><div class="g-grid">' + dialog + term + "</div></div>";
  }

  function syncButtons() {
    document.querySelectorAll("[data-set]").forEach(function (b) {
      b.setAttribute("aria-pressed", String(root.dataset[b.dataset.set] === b.dataset.val));
    });
    var t = document.getElementById("statusToggle");
    if (t) t.checked = root.dataset.statusbar === "on";
  }

  var menu = document.getElementById("menu"), scrim = document.getElementById("scrim"), floatWin = document.getElementById("floatWin");
  function openMenu(anchor) {
    var win = document.getElementById("win").getBoundingClientRect(), r = anchor.getBoundingClientRect();
    menu.hidden = false;
    menu.style.left = Math.max(8, Math.min(r.right - win.left - menu.offsetWidth, win.width - menu.offsetWidth - 8)) + "px";
    menu.style.top = (r.bottom - win.top + 2) + "px";
  }
  function setFloat(on) {
    floatWin.hidden = !on;
    root.dataset.floatRight = on ? "true" : "false";
  }

  document.addEventListener("click", function (e) {
    var t = e.target.closest("[data-set],[data-go],[data-menu],[data-dialog],[data-float]");
    if (!t) { if (!e.target.closest(".menu")) menu.hidden = true; return; }
    if (t.dataset.set) { root.dataset[t.dataset.set] = t.dataset.val; syncButtons(); }
    if (t.dataset.go) { root.dataset.view = t.dataset.go; syncButtons(); }
    if (t.dataset.menu) { if (menu.hidden) openMenu(t); else menu.hidden = true; e.stopPropagation(); return; }
    if (t.dataset.dialog) scrim.hidden = t.dataset.dialog !== "open";
    if (t.dataset.float) setFloat(t.dataset.float === "right");
    menu.hidden = true;
  });
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape") { if (!menu.hidden) menu.hidden = true; else scrim.hidden = true; }
  });
  document.getElementById("statusToggle").addEventListener("change", function (e) {
    root.dataset.statusbar = e.target.checked ? "on" : "off";
  });
  // 菜单中的“浮动整栏”示例
  menu.querySelector(".mi.hot").addEventListener("click", function () { setFloat(true); });

  gallery();
  document.querySelectorAll("i[data-i]").forEach(icon);
  syncButtons();
  if (q.get("float") === "right") setFloat(true);
  if (q.get("dialog") === "open") scrim.hidden = false;
  if (q.get("menu") === "open") openMenu(document.querySelector("#rightDock [data-menu]"));
})();
