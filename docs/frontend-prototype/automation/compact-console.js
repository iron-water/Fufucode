// Presentation only: no execution commands or changes to workflow state.
window.addEventListener('DOMContentLoaded',()=>{
 const get=id=>document.getElementById(id),main=document.querySelector('main');
 document.body.classList.add('compact-console');
 const bar=document.createElement('header');bar.className='compact-bar';
 const title=document.createElement('strong');title.textContent='自动开发';bar.append(title);
 for(const id of ['auto-mode','manual-mode']){get(id).textContent=id==='auto-mode'?'自动':'手动';bar.append(get(id));}
 const view=document.createElement('select');view.id='view-mode';view.setAttribute('aria-label','视图');
 for(const [value,label] of [['overview','阶段总览'],['all','完整拓扑'],['running','仅运行中'],['free','自由缩放']]){const option=document.createElement('option');option.value=value;option.textContent=label;view.append(option);}bar.append(view);
 const filter=document.querySelector('.flow-filter');filter.lastChild.textContent='显示未运行';filter.hidden=true;
 get('pause-live').textContent='暂停';get('resume-live').textContent='恢复';bar.append(get('pause-live'),get('resume-live'));
 const state=get('source-status');bar.append(state);
 main.prepend(bar);
 const core=document.createElement('section');core.className='core-status';core.setAttribute('aria-label','当前推进情况');bar.after(core);
 const headline=document.createElement('strong'),nextAction=document.createElement('span');core.append(headline,nextAction);
 const diagnostics=document.createElement('details');diagnostics.className='system-details';
 const diagnosticTitle=document.createElement('summary');diagnosticTitle.textContent='状态与阻塞';
 const diagnosticBody=document.createElement('div');diagnostics.append(diagnosticTitle,diagnosticBody);core.append(diagnostics);
 document.addEventListener('automation-connection-lost',()=>{headline.textContent='连接中断 · 以下为上次快照';nextAction.textContent='不能据此确认后台或模型正在运行';});
 const panel=document.createElement('dialog');panel.className='node-panel';panel.setAttribute('aria-label','节点详情');
 const close=document.createElement('button');close.textContent='关闭';close.className='panel-close';close.onclick=()=>panel.close();panel.append(close,get('node-detail'));
 panel.append(document.querySelector('.task-live-actions'),get('edit-plan'),document.querySelector('.event-drawer'));main.append(panel);
 get('nodes').addEventListener('click',event=>{if(event.target.closest('.thread-card')&&!panel.open)panel.showModal();});
 panel.addEventListener('click',event=>{if(event.target===panel)panel.close();});
 const stages=[['main','总控与验收'],['first','基础与首轮开发'],['automation','自动开发能力'],['later','后续增强']];
 const overview=document.createElement('section');overview.className='stage-overview';overview.setAttribute('aria-label','开发阶段总览');get('viewport').before(overview);
 let stage=null;
 function phase(t){if(t.id==='M')return 'main';if(t.tasks.some(a=>/^U[0-3]/.test(a.id)))return 'automation';if(t.tasks.some(a=>/^(D8|D9|S3|O8)/.test(a.id)))return 'later';return 'first';}
 function renderOverview(){
  if(typeof liveSnapshot!=='undefined'&&liveSnapshot){
   const s=liveSnapshot,latest=new Map();for(const r of s.runs)latest.set(r.taskId,r);
   const active=[...latest.values()].filter(r=>['running','starting'].includes(r.state)&&r.agentAlive);
   const pending=[...latest.values()].filter(r=>r.state==='completed_pending_review'&&!r.decision);
   const managerActive=s.manager.status==='inflight'&&s.manager.processAlive===true;
   const hosts=['workflow','gate','core','controls'],unconfirmed=hosts.filter(k=>!s.runtime[k]?.processAlive);
   headline.textContent=active.length?'正在开发 · '+active.map(r=>r.taskId).join('、'):managerActive?'M 正在'+(s.manager.lastRunId?.startsWith('integration-')?'集成':'审核'):'当前没有已确认运行的模型';
   const stopped=s.paused.workflow||s.paused.gate;
   nextAction.textContent=stopped?'调度已暂停':unconfirmed.length?'后台进程未确认在线，请先核对监督器':pending.length?pending.length+' 项交付等待 M 审核':'等待依赖或阻塞处理；后台在线不等于任务推进';
   const lines=[];
   for(const [key,label] of [['workflow','调度'],['gate','门房'],['core','核心'],['controls','控制入口']])lines.push(label+'：'+(s.runtime[key]?.processAlive?'在线':'未确认在线'));
   lines.push('M：'+(managerActive?'工作中':s.manager.status==='ready'?'待命（没有模型调用）':s.manager.status==='inflight'?'上次调用尚未对账':s.manager.status));
   const blockers=s.blockers||[];
   for(const b of blockers)lines.push(b.task+'：'+(b.reason||b.error||b.status));
   for(const r of latest.values())if(['blocked','defer','rework'].includes(r.decision)&&!blockers.some(b=>b.task===r.taskId))lines.push(r.taskId+'：'+({blocked:'阻塞',defer:'延后',rework:'待修复'}[r.decision])+'；点击节点查看审核意见');
   const gates=Object.entries(s.plan.gates||{}).filter(([,v])=>v!=='passed').map(([k])=>k);
   if(gates.length)lines.push('未通过门禁：'+gates.join('、'));
   diagnosticBody.replaceChildren();for(const text of lines){const line=document.createElement('p');line.textContent=text;diagnosticBody.append(line);}
   diagnosticTitle.textContent='状态与阻塞'+(blockers.length?' · '+blockers.length+' 项待处理':'');
   const newest=[...latest.values()].sort((a,b)=>(b.finished||b.started||'').localeCompare(a.finished||a.started||''))[0];
   if(newest){const line=document.createElement('p');line.textContent='最近交付：'+newest.taskId+' · '+(newest.finished||newest.started||'')+' · '+(newest.decision||newest.state);diagnosticBody.append(line);}
  }else {headline.textContent='尚未连接真实开发状态';nextAction.textContent='下方为示例拓扑';}
  const isSummary=view.value==='overview'||view.value==='running';overview.hidden=!isSummary;get('viewport').hidden=isSummary;
  if(!isSummary)return;
  overview.replaceChildren();
  if(view.value==='overview'&&!stage){
   for(const [id,label] of stages){const members=threads.filter(t=>phase(t)===id),tasks=members.flatMap(t=>t.tasks),done=tasks.filter(t=>taskStatus(t)==='integrated').length;
    const button=document.createElement('button');button.className='stage-card';const title=document.createElement('strong');title.textContent=label;const text=document.createElement('span');
    const active=members.filter(t=>t.active).length,waiting=tasks.filter(t=>['blocked','rework'].includes(taskStatus(t))).length;
    text.textContent=id==='main'?'审核 · 集成 · 验收':active?active+' 个线程运行中':waiting?waiting+' 项等待处理':'点击查看线程与任务';
    const detail=document.createElement('small');detail.textContent=id==='main'?'本地调度与模型调用分开显示':done+' 项已集成 · '+tasks.length+' 个图中条目';button.append(title,text,detail);button.onclick=()=>{stage=id;renderOverview();};overview.append(button);
   }return;
  }
  const back=document.createElement('button');back.textContent=stage?'‹ 阶段总览':'运行中';back.onclick=()=>{stage=null;view.value='overview';renderOverview();};overview.append(back);
  const list=document.createElement('div');list.className='stage-threads';overview.append(list);
  const members=threads.filter(t=>view.value==='running'?t.active:phase(t)===stage);
  for(const t of members){const original=get('thread-'+t.id);if(!original)continue;const card=original.cloneNode(true);card.removeAttribute('id');card.dataset.thread=t.id;card.hidden=false;card.querySelectorAll('[id]').forEach(e=>e.removeAttribute('id'));card.style.position='relative';card.style.left='auto';card.style.top='auto';card.onclick=event=>{const row=event.target.closest('.task-row'),index=row?[...card.querySelectorAll('.task-row')].indexOf(row):0;selected={thread:t.id,task:t.tasks[index].id};render();if(!panel.open)panel.showModal();};list.append(card);}
  if(!members.length){const empty=document.createElement('p');empty.textContent='没有正在执行的线程。展开“状态与阻塞”查看原因，或切换阶段总览。';list.append(empty);}
  // Show the actual dependency pairs belonging to this second-level scope.
  const ids=new Set(members.map(t=>t.id)),edges=definition.edges.filter(e=>ids.has(e[1])&&ids.has(e[3]));
  requestAnimationFrame(()=>{
   if(!list.isConnected)return;
   const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');svg.classList.add('stage-wires');svg.setAttribute('width',list.clientWidth);svg.setAttribute('height',list.scrollHeight);
   const rect=list.getBoundingClientRect();
   for(const edge of edges){const a=list.querySelector('[data-thread="'+edge[1]+'"]'),b=list.querySelector('[data-thread="'+edge[3]+'"]');if(!a||!b||a===b)continue;const ar=a.getBoundingClientRect(),br=b.getBoundingClientRect(),x1=ar.right-rect.left,y1=ar.top-rect.top+24,x2=br.left-rect.left,y2=br.top-rect.top+24,path=document.createElementNS(ns,'path');path.setAttribute('d','M '+x1+' '+y1+' C '+(x1+35)+' '+y1+', '+(x2-35)+' '+y2+', '+x2+' '+y2);path.setAttribute('class','wire focused');svg.append(path);}
   list.prepend(svg);
  });
  if(edges.length){const links=document.createElement('div');links.className='stage-links';for(const e of edges){const item=document.createElement('span');item.textContent=e[2]+' → '+e[4];links.append(item);}overview.append(links);}
 }
 let fitted='';
 function adapt(){
  const viewport=get('viewport'),canvas=get('canvas'),key=[view.value,viewport.clientWidth,viewport.clientHeight,canvas.style.width,canvas.style.height].join(':');
  if(key===fitted)return;fitted=key;
  if(view.value==='all'&&typeof fit==='function'){fit();get('viewport').scrollTop=0;get('viewport').scrollLeft=0;}
 }
 view.onchange=()=>{stage=null;renderOverview();get('viewport').classList.toggle('free-view',view.value==='free');if(view.value==='free')readable();else adapt();};
 new ResizeObserver(adapt).observe(get('viewport'));
 new MutationObserver(adapt).observe(get('canvas'),{attributes:true,attributeFilter:['style']});
 new MutationObserver(renderOverview).observe(get('nodes'),{childList:true});
 renderOverview();
 requestAnimationFrame(adapt);
});
