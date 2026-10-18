/* S1 皮肤样稿交互：皮肤/主题/缩放/视图切换 + 状态画廊渲染。
   URL 参数（皮肤、主题、缩放、视图、会话状态）让无头浏览器可复现同一画面：
   index.html?skin=metro&theme=dark&scale=1.25&view=states&chat=empty
   样稿不读写 localStorage、不调用模型、不访问网络。 */
'use strict';

const SKINS = {
  fluent: { label: 'Fluent 基线', caption: 'Fluent 基线 · 当前 UniGetUI 色调意图', accent: '#0078D4' },
  metro: { label: 'Metro', caption: 'Metro · Windows 8 现代 UI 对比样稿', accent: '#2D89EF' },
  material3: { label: 'Material 3', caption: 'Material 3 · Flutter 参考对比样稿', accent: '#6750A4' },
};

const params = new URLSearchParams(location.search);
const state = {
  skin: params.get('skin') in SKINS ? params.get('skin') : 'fluent',
  theme: params.get('theme') === 'dark' ? 'dark' : 'light',
  scale: ['1', '1.25', '1.5'].includes(params.get('scale')) ? params.get('scale') : '1',
  view: ['shell', 'settings', 'states'].includes(params.get('view')) ? params.get('view') : 'shell',
  chat: ['filled', 'empty', 'error', 'loading'].includes(params.get('chat')) ? params.get('chat') : 'filled',
};

const app = document.getElementById('app');
const icon = n => `<i data-lucide="${n}"></i>`;
const icons = () => { if (window.lucide) window.lucide.createIcons(); };

function setSkin(skin) {
  state.skin = skin;
  document.documentElement.dataset.skin = skin;
  document.getElementById('skin-caption').textContent = SKINS[skin].caption;
  document.getElementById('status-skin').textContent = '皮肤 ' + SKINS[skin].label;
  document.querySelectorAll('#skin-switch button').forEach(b => {
    const on = b.dataset.skin === skin;
    b.classList.toggle('selected', on);
    b.setAttribute('aria-checked', String(on));
  });
  const accent = document.querySelector('.settings .setting-row .tag');
  if (accent) accent.textContent = SKINS[skin].accent;
  renderGallery();
  icons();
}

function setTheme(theme) {
  state.theme = theme;
  document.documentElement.dataset.theme = theme;
  document.getElementById('theme-toggle').title = theme === 'dark' ? '切换为浅色' : '切换为深色';
}

function toggleTheme() { setTheme(state.theme === 'dark' ? 'light' : 'dark'); }

function setScale(scale) {
  state.scale = String(scale);
  document.documentElement.style.setProperty('--ui-scale', state.scale);
  document.getElementById('status-scale').textContent = '缩放 ' + Math.round(Number(state.scale) * 100) + '%';
  document.getElementById('scale-select').value = state.scale;
}

function setView(view) {
  state.view = view;
  app.dataset.view = view;
  document.querySelectorAll('#view-switch button').forEach(b => b.classList.toggle('selected', b.dataset.view === view));
  window.scrollTo(0, 0);
  icons();
}

function setChatChat(mode) { state.chat = mode; applyChat(); }

function applyChat() {
  app.dataset.chat = state.chat;
  document.getElementById('variant-empty').hidden = state.chat !== 'empty';
  document.getElementById('variant-error').hidden = state.chat !== 'error';
  document.getElementById('variant-loading').hidden = state.chat !== 'loading';
  // 变体模式隐藏示例消息，使对应状态直接出现在阅读列顶部。
  document.querySelectorAll('.timeline .reading > .msg, .timeline .reading > .banner').forEach(el => {
    el.hidden = state.chat !== 'filled';
  });
}

let toastTimer;
function toast(text) {
  const region = document.getElementById('toast-region');
  region.textContent = text;
  region.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => region.classList.remove('show'), 2400);
}

/* ---------- 状态画廊 ---------- */
const row = (name, stage, note = '') =>
  `<div class="spec"><div class="name">${name}</div><div class="stage">${stage}</div>${note ? `<div class="note">${note}</div>` : ''}</div>`;

function galleryButton() {
  return `<h3>按钮（默认 / 悬停 / 键盘焦点 / 按下 / 禁用）</h3><div class="grid cols-4">
    ${row('btn.accent', `<button class="btn accent">开始任务</button><button class="btn accent is-hover">悬停</button><button class="btn accent is-focus">焦点</button><button class="btn accent is-pressed">按下</button><button class="btn accent" disabled>禁用</button>`)}
    ${row('btn（次要）', `<button class="btn">取消</button><button class="btn is-hover">悬停</button><button class="btn is-focus">焦点</button><button class="btn is-pressed">按下</button><button class="btn" disabled>禁用</button>`)}
    ${row('btn.subtle（文字型）', `<button class="btn subtle">更多</button><button class="btn subtle is-hover">悬停</button><button class="btn subtle is-focus">焦点</button><button class="btn subtle" disabled>禁用</button>`)}
    ${row('iconbtn（图标按钮）', `<button class="iconbtn">${icon('settings')}</button><button class="iconbtn is-hover">${icon('star')}</button><button class="iconbtn is-focus">${icon('search')}</button><button class="iconbtn selected">${icon('check')}</button><button class="iconbtn" disabled>${icon('lock')}</button>`)}
  </div>`;
}

function galleryInput() {
  return `<h3>输入类（默认 / 键盘焦点 / 错误 / 禁用）</h3><div class="grid cols-4">
    ${row('field（文本框）', `<span class="field" style="width:100%">${icon('search')}<input placeholder="搜索会话" aria-label="搜索会话"></span><span class="field" style="width:100%">${icon('search')}<input class="is-focus" value="焦点样式" aria-label="焦点示例"></span><span class="field is-error" style="width:100%">${icon('circle-alert')}<input value="路径不存在" aria-label="错误示例"></span><span class="field is-disabled" style="width:100%">${icon('lock')}<input value="禁用（未接线）" disabled aria-label="禁用示例"></span>`)}
    ${row('下拉与复选', `<select aria-label="下拉"><option>跟随系统</option></select><input type="checkbox" checked aria-label="复选"><input type="radio" checked aria-label="单选"><input type="range" value="60" aria-label="滑块">`, '复选/单选/滑块沿系统原生控件，仅强调色跟随皮肤')}
    ${row('开关与分段', `<input type="checkbox" checked role="switch" aria-label="开关"><span class="segmented"><button class="selected">工作台</button><button>设置</button></span>`)}
    ${row('错误与提示文本', `<div style="width:100%"><div class="field is-error" style="border-color:var(--app-error-foreground);color:var(--app-error-foreground)">${icon('circle-alert')}<span style="font-size:12px">连接失败：进程已退出</span></div><div class="note" style="color:var(--app-error-foreground)">错误文本必须同时带图标与文字</div></div>`)}
  </div>`;
}

function galleryTabs() {
  return `<h3>页签与列表（默认 / 悬停 / 选中 / 禁用）</h3><div class="grid cols-3">
    ${row('tab（页签）', `<button class="tab">变更</button><button class="tab is-hover">预览</button><button class="tab selected">审查</button><button class="tab is-disabled" aria-disabled="true">插件</button>`)}
    ${row('row-item / list-row', `<div style="width:100%"><button class="row-item" style="width:100%">${icon('message-square')}<span>默认行</span><small>现在</small></button><button class="row-item is-hover" style="width:100%">${icon('message-square')}<span>悬停行</span></button><button class="row-item selected" style="width:100%">${icon('message-square')}<span>选中行</span></button><button class="row-item is-disabled" style="width:100%">${icon('archive')}<span>禁用行</span></button></div>`)}
    ${row('标签与状态', `<span class="tag">运行中</span><span class="tag ok">已完成</span><span class="tag warn">待审批</span><span class="tag err">失败</span><span class="tag mute">未接线</span>`)}
    ${row('分隔条', `<span class="splitter" style="width:120px;height:6px;display:block"></span><span class="splitter is-hover" style="width:120px;height:6px;display:block"></span>`, '左：默认；右：悬停 / 拖拽中（强调色）')}
  </div>`;
}

function galleryStatus() {
  return `<h3>空 / 加载 / 错误 / 禁用状态</h3><div class="grid cols-3">
    ${row('空会话', `<div class="empty-state" style="width:100%">${icon('message-square-dashed')}<strong>还没有消息</strong><p>选择项目并描述任务。</p><button class="btn accent">开始任务</button></div>`)}
    ${row('无搜索结果', `<div class="empty-state" style="width:100%">${icon('search-x')}<strong>没有匹配结果</strong><p>换一个关键词，或清除筛选条件。</p><button class="btn subtle">清除筛选</button></div>`)}
    ${row('加载中（骨架）', `<div style="width:100%"><div class="skeleton"><span></span><span></span><span></span></div><div class="event">${icon('loader-circle')}恢复会话快照中…</div></div>`, '加载不使用空态插图')}
    ${row('加载失败（可重试）', `<div class="banner error" style="width:100%;margin:0">${icon('server-off')}<div class="body"><strong>Pi 进程退出</strong>历史未受影响，可重连。</div><button class="btn">重试</button></div>`)}
    ${row('权限被拒（不可用已说明）', `<div class="banner warn" style="width:100%;margin:0">${icon('shield-alert')}<div class="body"><strong>需要审批</strong>写入请求等待批准，发送已暂停。</div><button class="btn">查看请求</button></div>`)}
    ${row('未接线（禁用 + 原因）', `<button class="btn" disabled>${icon('blocks')}扩展管理</button><span class="tag mute">S2 接线后开放</span>`, '禁用控件必须能解释为什么不可用')}
  </div>`;
}

function galleryScale() {
  return `<h3>亮暗与缩放</h3><div class="grid cols-3">
    ${row('浅色 / 深色', `<button class="btn" onclick="setTheme('light')">浅色</button><button class="btn" onclick="setTheme('dark')">深色</button>`, '同一页签、卡片、按钮在三套皮肤下均有对应明暗取值')}
    ${row('缩放 100 / 125 / 150', `<button class="btn" onclick="setScale('1')">100%</button><button class="btn" onclick="setScale('1.25')">125%</button><button class="btn" onclick="setScale('1.5')">150%</button>`, '样稿用 CSS zoom 近似 DPI 缩放；生产由 Avalonia 原生 DPI 处理，需在 V 任务用真实窗口复核')}
    ${row('状态反馈', `<div class="banner success" style="margin:0;width:100%">${icon('check-circle-2')}<div class="body"><strong>已保存</strong>即时生效</div></div><div class="banner warn" style="margin:0;width:100%">${icon('clock')}<div class="body"><strong>待重启</strong>运行环境变更</div></div>`)}
  </div>`;
}

function renderGallery() {
  const body = document.getElementById('gallery-body');
  if (!body) return;
  document.getElementById('gallery-lead').textContent =
    `当前皮肤：${SKINS[state.skin].label}（${SKINS[state.skin].caption}）· 主题：${state.theme === 'dark' ? '深色' : '浅色'} · 缩放：${Math.round(Number(state.scale) * 100)}%。` +
    '悬停/按下/焦点在样稿里以 .is-hover / .is-pressed / .is-focus 静态展示，工作台视图中的同名状态可直接交互。';
  body.innerHTML = galleryButton() + galleryInput() + galleryTabs() + galleryStatus() + galleryScale();
  icons();
}

/* 设置页导航（只切换选中态，内容为固定样稿） */
document.querySelectorAll('.settings-nav .navitem').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('.settings-nav .navitem').forEach(b => b.classList.remove('selected'));
    btn.classList.add('selected');
    toast('样稿仅展示「界面与布局」的完整形态');
  });
});

setSkin(state.skin);
setTheme(state.theme);
setScale(state.scale);
setView(state.view);
applyChat();
renderGallery();
