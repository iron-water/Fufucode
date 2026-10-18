'use strict';
(() => {
  const style = document.createElement('style');
  style.textContent = `
    .selection-tools{position:fixed;z-index:40;display:flex;flex-wrap:wrap;gap:3px;max-width:calc(100vw - 16px);padding:5px;background:var(--bg);border:1px solid var(--line);border-radius:6px;box-shadow:0 4px 18px #0002}
    .selection-tools[hidden]{display:none}.user-actions{justify-content:flex-end}.style-picker{max-width:130px;font-size:11px;background:var(--panel);border:1px solid var(--line);border-radius:4px;padding:5px}
    html[data-surface=glass] .topbar,html[data-surface=glass] .dock,html[data-surface=glass] .composer{background:color-mix(in srgb,var(--bg) 78%,transparent);backdrop-filter:blur(18px);box-shadow:inset 0 1px 0 #ffffff80}
    html[data-surface=glass] .workspace{background:linear-gradient(125deg,#e4f2ed,#e9edf7 55%,#f5e7ef)}
    html.dark[data-surface=glass] .workspace{background:linear-gradient(125deg,#243d35,#333745 55%,#45323f)}
    html[data-surface=flat] button,html[data-surface=flat] .user-message,html[data-surface=flat] .summary,html[data-surface=flat] select{border-radius:0!important;box-shadow:none!important}
    html[data-surface=flat] .tab.selected{border-bottom-width:4px}html[data-surface=flat] .topbar{border-bottom:3px solid var(--green)}
    html[data-surface=classic] .topbar,html[data-surface=classic] .dock-head{background:linear-gradient(var(--bg),var(--hover));box-shadow:inset 0 1px 0 #ffffff80}
    html[data-surface=classic] .btn,html[data-surface=classic] .icon,html[data-surface=classic] select{border:1px solid var(--line);background:linear-gradient(var(--bg),var(--hover));box-shadow:inset 0 1px 0 #ffffff80,0 1px 2px #0002}
    html[data-surface=classic] .user-message{border:1px solid var(--line);box-shadow:inset 0 1px 2px #0001}
    @media(max-width:880px){.topbar{gap:4px}.topbar>.row{display:none}.style-picker{max-width:95px}.selection-tools .icon{width:32px;height:32px}}
  `;
  document.head.append(style);
  const picker = document.createElement('select');
  picker.className = 'style-picker';
  picker.setAttribute('aria-label', '视觉风格');
  picker.innerHTML = '<option value="fluent">当前风格</option><option value="glass">液态玻璃探索</option><option value="flat">纯平面探索</option><option value="classic">拟物探索</option>';
  document.querySelector('.topbar').insertBefore(picker, document.querySelector('.topbar button[onclick="toggleTheme()"]'));
  picker.onchange = () => { document.documentElement.dataset.surface = picker.value; };

  const tools = document.createElement('div');
  tools.className = 'selection-tools';
  tools.hidden = true;
  tools.setAttribute('role', 'toolbar');
  tools.setAttribute('aria-label', '选中文字操作');
  const actions = [
    ['copy', '复制选中文字', 'copy'],
    ['quote', '添加到上下文', 'context'],
    ['message-circle-question', '在侧边栏提问', 'ask'],
    ['text-search', '更多详情', 'detail'],
    ['list-plus', '添加到当前任务草稿', 'task']
  ];
  tools.innerHTML = actions.map(([ic, title, action]) => `<button type="button" class="icon" title="${title}" aria-label="${title}" data-selection-action="${action}">${icon(ic)}</button>`).join('');
  document.body.append(tools);
  let selectedText = '', selectedSource = '';
  async function copyText(text) {
    try { await navigator.clipboard.writeText(text); notice('已复制'); }
    catch { actionModal('复制文本', '<textarea readonly aria-label="待复制文本" style="width:100%;min-height:120px">' + esc(text) + '</textarea>', () => {}, '关闭'); }
  }
  function captureSelection() {
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || !selection.rangeCount) { tools.hidden = true; return; }
    const node = selection.anchorNode?.parentElement;
    const end = selection.focusNode?.parentElement;
    const container = node?.closest('.message,.document,.diff,.ask-content');
    if (!container || !container.contains(end) || node.closest('button,input,textarea,select')) { tools.hidden = true; return; }
    selectedText = selection.toString().trim();
    if (!selectedText) { tools.hidden = true; return; }
    selectedSource = container.matches('.message') ? '会话 · ' + sessionTitle : '页面 · ' + fileName;
    const rect = selection.getRangeAt(0).getBoundingClientRect();
    tools.hidden = false;
    tools.style.left = Math.max(8, Math.min(rect.left, innerWidth - tools.offsetWidth - 8)) + 'px';
    tools.style.top = Math.max(8, Math.min(rect.bottom + 7, innerHeight - tools.offsetHeight - 8)) + 'px';
  }
  document.addEventListener('pointerup', e => { if (!tools.contains(e.target)) setTimeout(captureSelection, 0); });
  document.addEventListener('keyup', e => { if (e.key === 'Shift' || e.key.startsWith('Arrow')) captureSelection(); });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') tools.hidden = true; });
  document.addEventListener('scroll', () => { tools.hidden = true; }, true);
  window.addEventListener('resize', () => { tools.hidden = true; });
  tools.addEventListener('pointerdown', e => e.preventDefault());
  tools.addEventListener('click', e => {
    const action = e.target.closest('[data-selection-action]')?.dataset.selectionAction;
    if (!action) return;
    const quote = selectedSource + '\n' + selectedText;
    tools.hidden = true;
    if (action === 'copy') copyText(selectedText);
    else if (action === 'ask' || action === 'detail') {
      setAskContext(quote);
      const input = document.getElementById('ask-input');
      input.value = action === 'detail' ? '请结合原文解释这段内容：\n' + selectedText : '';
      input.focus();
    } else {
      mainContext = quote;
      updateContext();
      mobileView('chat');
      const input = document.getElementById('main-input');
      if (action === 'task') input.value += (input.value ? '\n\n' : '') + '任务补充：\n' + selectedText;
      input.focus();
    }
  });
  function addPromptActions() {
    document.querySelectorAll('.user-message').forEach(message => {
      if (message.parentElement.querySelector('.user-actions')) return;
      const bar = document.createElement('div');
      bar.className = 'actions user-actions';
      const buttons = [['copy','复制用户消息','copy'],['pencil','编辑为新草稿','edit'],['git-branch','从此消息创建分支','fork'],['quote','引用用户消息','quote']];
      bar.innerHTML = buttons.map(([ic,title,action]) => `<button type="button" class="icon" title="${title}" aria-label="${title}" data-prompt-action="${action}">${icon(ic)}</button>`).join('');
      bar.onclick = e => {
        const action = e.target.closest('[data-prompt-action]')?.dataset.promptAction;
        if (action === 'copy') copyText(message.textContent);
        else if (action === 'fork') messageAction('fork', e.target);
        else if (action === 'edit' || action === 'quote') {
          if (action === 'edit') document.getElementById('main-input').value = message.textContent;
          else { mainContext = '用户消息 · ' + sessionTitle + '\n' + message.textContent; updateContext(); }
          mobileView('chat');
          document.getElementById('main-input').focus();
        }
      };
      message.after(bar);
    });
    icons();
  }
  addPromptActions();
  new MutationObserver(addPromptActions).observe(document.getElementById('new-messages'), { childList:true });
})();
