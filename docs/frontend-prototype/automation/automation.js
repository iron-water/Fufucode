'use strict';
const $=id=>document.getElementById(id), ns='http://www.w3.org/2000/svg', definition=window.threadFlow;
const labels={waiting:'等待',running:'执行中',delivered:'待审核',accepted:'待接线',integrated:'已集成',blocked:'待门禁',rework:'部分打回'};
let threads,events,selected,collapsed,step,calls,pending,activeEdge,unknown=false,host=true,timer=null,scale=.65;
const timeline=[
 {text:'M 按范围批准多个任务；线程内分别记录进度。',edge:'scope-core'},
 {text:'核心线程交付 D1-P 的 stop 部分；message 部分已经集成。',edge:'core-review',pending:1},
 {text:'M 只打回 D1-P / stop，不改变 message、D1-A 或其他线程。',edge:'review-core',calls:1,pending:-1,target:['core','D1-P','stop','rework']},
 {text:'原 Codex 线程继续修复 stop 部分，沿用原模型与工作树。',edge:'review-core',target:['core','D1-P','stop','running']},
 {text:'stop 修复后重新交付，第 2 轮运行身份与 HEAD 独立绑定。',edge:'core-review',pending:1,round:true,target:['core','D1-P','stop','delivered']},
 {text:'M 独立复审 stop 并接受；该部分仍待主线接线。',edge:'core-review',calls:1,pending:-1,target:['core','D1-P','stop','accepted']},
 {text:'M 单写入收编 stop 部分；D1-A 仍执行，D5 仍等 G1。',edge:'core-review',target:['core','D1-P','stop','integrated']},
 {text:'OMP 线程交付 O1 / conflicts；已集成的动作列表不重复提交。',edge:'management-review',pending:1},
 {text:'M 接受 O1 / conflicts 部分；同线程存储与资料任务不受影响。',edge:'management-review',calls:1,pending:-1,target:['management','O1','conflicts','accepted']},
 {text:'主线收编 conflicts；O2 / O3 保留各自进度。',edge:'management-review',target:['management','O1','conflicts','integrated']},
 {text:'本次部分交付闭环结束。G1 / G2 / G3 未满足，后续线程仍待解锁。'}
];
function task(threadId,taskId){return threads.find(t=>t.id===threadId).tasks.find(t=>t.id===taskId);}
function taskStatus(t){
 if(t.parts.every(p=>p.status==='integrated'))return 'integrated';
 if(t.parts.some(p=>p.status==='rework'))return 'rework';
 if(t.parts.some(p=>p.status==='running'))return 'running';
 if(t.parts.some(p=>p.status==='delivered'))return 'delivered';
 if(t.parts.every(p=>['accepted','integrated'].includes(p.status)))return 'accepted';
 if(t.parts.some(p=>p.status==='blocked'))return 'blocked';
 return 'waiting';
}
function progress(t){const map={integrated:100,accepted:100,delivered:100,running:45,rework:30,blocked:0,waiting:0};return Math.round(t.parts.reduce((s,p)=>s+map[p.status],0)/t.parts.length);}
function log(text){events.unshift({time:new Date().toLocaleTimeString('zh-CN',{hour12:false}),text});}
function stop(){if(timer!==null)clearInterval(timer);timer=null;$('play').textContent='▶ 播放部分打回';$('canvas').classList.add('paused');}
function reset(){
 stop();threads=definition.threads.map(t=>({...t,tasks:t.tasks.map(([id,name,status,parts])=>({id,name,parts:(parts||[['module','模块交付',status]]).map(([partId,title,partStatus])=>({id:partId,title,status:partStatus,round:1}))}))}));
 events=[];selected={thread:'core',task:'D1-P'};collapsed=new Set();step=0;calls=0;pending=0;activeEdge=null;unknown=false;host=true;
 log('示例层级图就绪：9 个线程，多任务进度分别保存。');draw();render();readable();
}
function visible(thread){const group=definition.groups.find(g=>g.id===thread.group);return !collapsed.has(thread.group)&&(!group.parent||!collapsed.has(group.parent));}
function anchor(threadId,taskId,side){const t=threads.find(t=>t.id===threadId);return {x:t.x+(side==='right'?340:0),y:t.y+71+t.tasks.findIndex(a=>a.id===taskId)*36+18};}
function edgePath(edge){
 const [,from,fromTask,to,toTask,type]=edge,a=threads.find(t=>t.id===from),b=threads.find(t=>t.id===to);
 const direction=b.x>=a.x, p=anchor(from,fromTask,direction?'right':'left'),q=anchor(to,toTask,direction?'left':'right');
 if(from===to){const gutter=a.x+365;return {d:'M '+p.x+' '+p.y+' H '+gutter+' V '+q.y+' H '+q.x,label:{x:gutter+6,y:(p.y+q.y)/2}};}
 const offset=type==='rework'?-25:0, dx=Math.max(40,Math.min(140,Math.abs(q.x-p.x)/3));
 return {d:'M '+p.x+' '+p.y+' C '+(p.x+(direction?dx:-dx))+' '+(p.y+offset)+', '+(q.x-(direction?dx:-dx))+' '+(q.y+offset)+', '+q.x+' '+q.y,label:{x:(p.x+q.x)/2,y:(p.y+q.y)/2+offset-8}};
}
function draw(){
 $('groups').replaceChildren();$('nodes').replaceChildren();
 for(const group of definition.groups){
  const box=document.createElement('section');box.id='group-'+group.id;box.className='group-box'+(group.parent?' child':'');
  Object.assign(box.style,{left:group.x+'px',top:group.y+'px',width:group.w+'px',height:group.h+'px'});
  const header=document.createElement('div');header.className='group-label';
  const title=document.createElement('div');const strong=document.createElement('strong');strong.textContent=group.title;const sub=document.createElement('span');sub.textContent=group.subtitle;title.append(strong,sub);
  const toggle=document.createElement('button');toggle.textContent='折叠';toggle.setAttribute('aria-label','折叠'+group.title);toggle.onclick=()=>{if(collapsed.has(group.id))collapsed.delete(group.id);else collapsed.add(group.id);render();};
  header.append(title,toggle);box.append(header);$('groups').append(box);
 }
 for(const thread of threads){
  const card=document.createElement('article');card.id='thread-'+thread.id;card.className='thread-card'+(thread.id==='M'?' manager':'');card.style.left=thread.x+'px';card.style.top=thread.y+'px';
  const heading=document.createElement('button');heading.className='thread-heading';const strong=document.createElement('strong');strong.textContent=thread.title;const label=document.createElement('span');label.textContent=thread.harness+' · '+thread.session;heading.append(strong,label);heading.onclick=()=>{selected={thread:thread.id,task:thread.tasks[0].id};render();};card.append(heading);
  for(const t of thread.tasks){
   const row=document.createElement('button');row.id='task-'+thread.id+'-'+t.id;row.className='task-row';row.innerHTML='<span class="task-id"></span><span class="task-name"></span><span class="task-status"></span><i class="task-progress"></i>';row.querySelector('.task-id').textContent=t.id;row.querySelector('.task-name').textContent=t.name;row.onclick=()=>{selected={thread:thread.id,task:t.id};render();};card.append(row);
  }
  const footer=document.createElement('div');footer.className='thread-footer';footer.innerHTML='<span></span><span></span>';card.append(footer);$('nodes').append(card);
 }
}
function drawEdges(){
 $('connections').innerHTML='<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z" fill="context-stroke"/></marker></defs>';
 for(const edge of definition.edges){
  const [id,from,fromTask,to,toTask,type]=edge;if(!visible(threads.find(t=>t.id===from))||!visible(threads.find(t=>t.id===to)))continue;
  const {d,label}=edgePath(edge),path=document.createElementNS(ns,'path');path.id='edge-'+id;
  const focused=(selected.thread===from&&selected.task===fromTask)||(selected.thread===to&&selected.task===toTask);
  path.setAttribute('d',d);path.setAttribute('class','wire '+(type||'')+(focused?' focused':'')+(activeEdge===id?' active':''));path.setAttribute('marker-end','url(#arrow)');$('connections').append(path);
  if(id===activeEdge){const text=document.createElementNS(ns,'text');text.setAttribute('x',String(label.x));text.setAttribute('y',String(label.y));text.setAttribute('class','edge-label '+(type==='rework'?'return':''));const scope=type==='rework'?toTask:fromTask;text.textContent=scope+(scope==='D1-P'?' / stop':scope==='O1'?' / conflicts':'')+(type==='rework'?' · 部分打回':' · 交付/依赖');$('connections').append(text);}
 }
}
function addField(title,value){const div=document.createElement('div');div.className='detail-field';const label=document.createElement('span');label.textContent=title;const strong=document.createElement('strong');strong.textContent=value;div.append(label,strong);$('detail-body').append(div);}
function render(){
 for(const group of definition.groups){const box=$('group-'+group.id);box.hidden=!!group.parent&&collapsed.has(group.parent);box.classList.toggle('collapsed',collapsed.has(group.id));const button=box.querySelector('button');button.textContent=collapsed.has(group.id)?'展开':'折叠';button.setAttribute('aria-label',(collapsed.has(group.id)?'展开':'折叠')+group.title);button.setAttribute('aria-expanded',String(!collapsed.has(group.id)));}
 for(const thread of threads){
  const card=$('thread-'+thread.id);card.hidden=!visible(thread);card.classList.toggle('selected',thread.id===selected.thread);
  for(const t of thread.tasks){const row=$('task-'+thread.id+'-'+t.id),status=taskStatus(t);row.className='task-row '+status+(selected.thread===thread.id&&selected.task===t.id?' selected':'');row.style.setProperty('--progress',progress(t)+'%');row.querySelector('.task-status').textContent=labels[status];row.setAttribute('aria-label',thread.title+' / '+t.id+' '+t.name+'：'+labels[status]);}
  const footer=card.querySelector('.thread-footer');footer.children[0].textContent=thread.tasks.length+' 项任务 · '+thread.tasks.filter(t=>taskStatus(t)==='integrated').length+' 项已集成';footer.children[1].textContent=thread.tasks.some(t=>taskStatus(t)==='rework')?'局部修复 · 其他任务保留':'线程身份固定';
 }
 drawEdges();
 const thread=threads.find(t=>t.id===selected.thread), t=task(selected.thread,selected.task);
 $('detail-title').textContent=thread.title+' / '+t.id;$('detail-state').textContent=labels[taskStatus(t)];$('detail-body').replaceChildren();
 addField('保存线程 / Harness',thread.session+' / '+thread.harness);addField('当前任务',t.name);addField('当前任务 / 原模型','部分轮次分别记录 / devin/swe-2');
 const parts=document.createElement('div');parts.className='part-selector';parts.id='part-selector';
 for(const part of t.parts){const label=document.createElement('label');const input=document.createElement('input');input.type='checkbox';input.value=part.id;input.dataset.part=part.id;input.disabled=!['delivered','running','rework'].includes(part.status);input.checked=part.status==='delivered';input.setAttribute('aria-label',part.title+'交付部分');const span=document.createElement('span');span.textContent=labels[part.status]+' · 第 '+part.round+' 轮';label.append(input,document.createTextNode(' '+part.title),span);parts.append(label);}
 const back=document.createElement('button');back.id='return-parts';back.textContent='仅打回勾选部分';back.disabled=unknown||!t.parts.some(p=>['delivered','running','rework'].includes(p.status));back.onclick=()=>partialRework([...parts.querySelectorAll('input:checked:not(:disabled)')].map(i=>i.value));parts.append(back);$('detail-body').append(parts);
 const note=document.createElement('p');note.className='detail-note';note.textContent='线程不等于任务；打回仅改变选中的交付部分，已集成部分和同线程其他任务保持原状态。原线程、原模型、原工作树继续。';$('detail-body').append(note);
 $('caption').textContent='层级：项目 → 主控 / 首轮 / 后续阶段 → Harness 分组 → 对话线程 → 多个任务 → 交付部分';
 $('counts').textContent='9 个线程 · '+threads.reduce((s,a)=>s+a.tasks.length,0)+' 项任务 · 待审 '+pending+' · 模拟审核 '+calls;
 $('current').textContent=events[0].text;$('next').textContent=step<timeline.length?'下一事件：'+timeline[step].text:'部分交付闭环结束 · 未完成任务保持原进度';
 $('event-count').textContent=events.length+' 条';$('event-list').replaceChildren(...events.map(e=>{const li=document.createElement('li'),time=document.createElement('time'),span=document.createElement('span');time.textContent=e.time;span.textContent=e.text;li.append(time,span);return li;}));
 $('play').disabled=unknown||step>=timeline.length;$('step').disabled=unknown||step>=timeline.length;$('reconcile').disabled=!unknown;
}
function partialRework(ids){
 stop();if(unknown)return;if(!ids.length){log('先勾选需要修复的交付部分。');render();return;}
 const t=task(selected.thread,selected.task);for(const id of ids)t.parts.find(p=>p.id===id).status='rework';calls++;
 activeEdge=selected.thread==='core'?'review-core':selected.thread==='management'?'review-management':null;
 log('M 仅打回 '+selected.task+' / '+ids.join(', ')+'；其他部分与其他任务未改变。');$('canvas').classList.remove('paused');render();
}
function advance(){
 if(unknown||!host||step>=timeline.length){stop();return;}const event=timeline[step++];activeEdge=event.edge||null;calls+=event.calls||0;pending+=event.pending||0;
 if(event.target){const [threadId,taskId,partId,status]=event.target;task(threadId,taskId).parts.find(p=>p.id===partId).status=status;selected={thread:threadId,task:taskId};}
 else if(activeEdge){const edge=definition.edges.find(e=>e[0]===activeEdge);selected={thread:edge[1]==='M'?edge[3]:edge[1],task:edge[1]==='M'?edge[4]:edge[2]};}
 if(event.round)task('core','D1-P').parts.find(p=>p.id==='stop').round=2;
 log(event.text);if(step===timeline.length)stop();render();
}
function setScale(value){scale=Math.max(.01,Math.min(1.4,value));$('canvas').style.transform='scale('+scale+')';$('canvas-wrap').style.width=definition.width*scale+'px';$('canvas-wrap').style.height=definition.height*scale+'px';$('zoom-level').textContent=Math.round(scale*100)+'%';}
function fit(){setScale(Math.min(($('viewport').clientWidth-20)/definition.width,($('viewport').clientHeight-20)/definition.height));}
function readable(){setScale(.8);}
$('play').onclick=()=>{if(timer!==null){stop();return;}advance();if(step===timeline.length)return;$('canvas').classList.remove('paused');$('play').textContent='Ⅱ 暂停动画';timer=setInterval(advance,Number($('speed').value));};
$('step').onclick=()=>{stop();advance();if(step<timeline.length)$('canvas').classList.remove('paused');};
$('speed').onchange=()=>{if(timer!==null){clearInterval(timer);timer=setInterval(advance,Number($('speed').value));}};
$('reset').onclick=reset;$('theme').onclick=()=>document.body.classList.toggle('dark');
$('zoom-in').onclick=()=>setScale(scale+.1);$('zoom-out').onclick=()=>setScale(scale-.1);$('fit').onclick=fit;$('readable').onclick=readable;
$('replay').onclick=()=>{log('同一线程 / 任务 / 部分 / 轮次的重复交付，本地去重；审核调用不增加。');render();};
$('disconnect').onclick=()=>{stop();unknown=true;host=false;log('示例审核投递状态不明：冻结新自动投递，保留所有任务与部分身份。');render();};
$('restart').onclick=()=>{host=true;log('本地门房恢复；状态不明的任务部分不自动重发。');render();};
$('reconcile').onclick=()=>{if(!host){log('先恢复门房，再核对已完成决定。');render();return;}unknown=false;log('本地核对原线程 / 任务 / 部分 / HEAD / 决定一致，没有新模型调用。');render();};
reset();
