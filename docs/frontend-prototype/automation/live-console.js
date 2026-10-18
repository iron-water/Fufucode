'use strict';
let liveSnapshot=null,consoleToken=null,refreshTimer=null,requestBusy=false;
const sourceLabel=document.querySelector('.prototype');
const liveControls=['auto-mode','manual-mode','refresh-live','pause-live','resume-live','edit-plan'];
const realLabels={completed_pending_review:'待审核',running:'执行中',starting:'启动中',invalid:'交付无效',failed:'失败'};
const projectTopology=structuredClone(definition);
let showInactive=localStorage.getItem('automation.showInactive')!=='false';
$('show-inactive').checked=showInactive;

async function api(path,options){
 const response=await fetch(path,options);const result=await response.json();
 if(!response.ok)throw Error(result.error||'本地请求失败');return result;
}
function statusFor(run){
 if(run.integration?.status==='done')return 'integrated';
 if(run.decision==='accept')return 'accepted';
 if(run.decision==='rework')return 'rework';
 if(['blocked','defer'].includes(run.decision))return 'blocked';
 if(run.state==='completed_pending_review')return 'delivered';
 if(['running','starting'].includes(run.state))return run.agentAlive?'running':'blocked';
 return 'blocked';
}
function liveGraph(snapshot){
 const plan=snapshot.plan, manager=snapshot.manager;
 const managerActive=manager.status==='inflight'&&manager.processAlive===true;
 definition.width=1510;definition.height=950;
 definition.groups=[{id:'main',title:'生产主控线程',subtitle:'真实绑定 · 审核和集成分别记录',x:20,y:25,w:390,h:850},
  {id:'managed',title:'已批准执行线程',subtitle:'仅当前workflow管理的运行；不接管未知会话',x:440,y:25,w:1050,h:850}];
 const gates=Object.entries(plan.gates||{}).map(([id,status])=>[id,'门禁 '+id,status==='passed'?'integrated':'blocked']);
 definition.threads=[{id:'M',title:'M · 审核与集成',active:managerActive,harness:'Codex / '+manager.model,session:manager.threadId,group:'main',x:40,y:110,
  tasks:[['REVIEW','交付审核',managerActive&&!manager.lastRunId?.startsWith('integration-')?'running':['needs_attention','inflight'].includes(manager.status)&&!managerActive?'blocked':'waiting'],['WIRE','共享接线与独立集成',managerActive&&manager.lastRunId?.startsWith('integration-')?'running':'waiting'],...gates]}];
 definition.edges=[];
 const groups=new Map();
 for(const taskPlan of plan.tasks){
  const runs=snapshot.runs.filter(r=>r.taskId===taskPlan.id);
  const newest=runs.at(-1),session=[...runs].reverse().find(r=>r.session)?.session;
  const key=session||taskPlan.worktree||taskPlan.id;
  if(!groups.has(key))groups.set(key,{id:'live-'+groups.size,title:taskPlan.harness+' · '+taskPlan.id,harness:taskPlan.harness+' / '+taskPlan.model,session:session||'待派发 · 尚无原生线程',roadmap:!newest,approved:true,group:'managed',x:470+(groups.size%2)*470,y:110+Math.floor(groups.size/2)*330,tasks:[]});
  const thread=groups.get(key);thread.active ||= ['running','starting'].includes(newest?.state)&&newest.agentAlive;thread.tasks.push([taskPlan.id,taskPlan.id,newest?statusFor(newest):'waiting']);
  if(newest){definition.edges.push(['review-'+taskPlan.id,thread.id,taskPlan.id,'M','REVIEW','delivery']);if(runs.some(r=>r.decision==='rework'))definition.edges.push(['rework-'+taskPlan.id,'M','REVIEW',thread.id,taskPlan.id,'rework']);if(newest.integration?.status==='done')definition.edges.push(['integrated-'+taskPlan.id,thread.id,taskPlan.id,'M','WIRE','delivery']);}
 }
 definition.threads.push(...groups.values());
 for(const taskPlan of plan.tasks)for(const dep of [...(taskPlan.dependsOn||[]),...(taskPlan.integrationDependsOn||[])]){
  const from=definition.threads.find(t=>t.tasks.some(x=>x[0]===dep)),to=definition.threads.find(t=>t.tasks.some(x=>x[0]===taskPlan.id));
  if(from&&to)definition.edges.push(['dep-'+dep+'-'+taskPlan.id,from.id,dep,to.id,taskPlan.id]);
 }
 if(showInactive){
  definition.width=projectTopology.width;definition.height=Math.max(2110,1750+Math.ceil(groups.size/2)*330+280);
  definition.groups=structuredClone(projectTopology.groups);
  definition.groups.find(g=>g.id==='main').h=definition.height-50;
  definition.groups.push({id:'managed',title:'批准项目任务图',subtitle:'待派发契约与真实保存线程 · 自动按依赖续派',x:420,y:1650,w:1670,h:definition.height-1690});
  const main=definition.threads[0];main.y=120;
  for(const [id,name] of projectTopology.threads.find(t=>t.id==='M').tasks)if(!main.tasks.some(t=>t[0]===id))main.tasks.push([id,name,'waiting']);
  for(const thread of definition.threads.slice(1))thread.y+=1620;
  const aliases=new Map([['D4-S','D4-SNAPSHOT']]),bound=new Map();
  for(const thread of definition.threads)for(const t of thread.tasks)bound.set(t[0],[thread.id,t[0]]);
  for(const p of projectTopology.threads)for(const t of p.tasks){const canonical=t[0].split('-')[0];if(bound.has(canonical)&&!aliases.has(t[0]))aliases.set(t[0],canonical);}
  for(const planned of projectTopology.threads.filter(t=>t.id!=='M')){
   const tasks=planned.tasks.filter(t=>!bound.has(aliases.get(t[0])||t[0])).map(([id,name,status])=>[id,name,status==='blocked'?'blocked':'waiting']);
   if(!tasks.length)continue;
   const thread={...planned,roadmap:true,session:'规划归组 · 未绑定当前 workflow，非实时状态',tasks};
   definition.threads.push(thread);
   for(const t of tasks)bound.set(t[0],[thread.id,t[0]]);
  }
  for(const [id,,fromTask,,toTask,type] of projectTopology.edges){
   if(type)continue;
   const from=bound.get(aliases.get(fromTask)||fromTask),to=bound.get(aliases.get(toTask)||toTask);
   if(from&&to)definition.edges.push(['planned-'+id,...from,...to]);
  }
 }else{
  definition.threads=definition.threads.filter(t=>t.id==='M'||t.active);
  const visible=new Set(definition.threads.map(t=>t.id));
  definition.edges=definition.edges.filter(e=>visible.has(e[1])&&visible.has(e[3]));
 }
 $('canvas').style.width=definition.width+'px';$('canvas').style.height=definition.height+'px';
 $('connections').style.width=definition.width+'px';$('connections').style.height=definition.height+'px';
 threads=definition.threads.map(t=>({...t,tasks:t.tasks.map(([id,name,status])=>({id,name,parts:[{id:'whole-v1',title:'v1 整项交付（未支持部分审核）',status,round:1}]}))}));
 events=snapshot.runs.map(r=>({time:r.finished||r.started||'未知时间',text:r.taskId+' / 第 '+r.attempt+' 轮 · '+(r.decision||realLabels[r.state]||r.state)+(r.integration?.status==='done'?' · 主线 '+r.integration.mainSha.slice(0,8):'')})).reverse();
 if(!events.length)events=[{time:snapshot.at,text:'当前批准计划尚无执行记录'}];
 selected=threads.some(t=>t.id===selected.thread&&t.tasks.some(a=>a.id===selected.task))?selected:{thread:threads[1]?.id||'M',task:threads[1]?.tasks[0].id||'REVIEW'};
 collapsed=new Set([...collapsed].filter(id=>definition.groups.some(g=>g.id===id)));activeEdge=null;pending=snapshot.runs.filter(r=>r.state==='completed_pending_review'&&!r.decision).length;calls=snapshot.runs.filter(r=>r.decision).length;step=0;
 draw();renderLive();setScale(scale);
}
const demoRender=render;
render=function(){demoRender();if(liveSnapshot)renderLiveExtras();};
function renderLive(){demoRender();renderLiveExtras();}
function renderLiveExtras(){
 const s=liveSnapshot,thread=threads.find(t=>t.id===selected.thread),taskId=selected.task;
 for(const plannedThread of threads.filter(t=>t.roadmap)){
  const card=$('thread-'+plannedThread.id);
  card.classList.add('roadmap-thread');
  for(const t of plannedThread.tasks){const row=$('task-'+plannedThread.id+'-'+t.id);row.querySelector('.task-status').textContent='非实时';row.setAttribute('aria-label',plannedThread.title+' / '+t.id+' '+t.name+'：规划，实时状态未绑定');}
  card.querySelector('.thread-footer').children[0].textContent=plannedThread.tasks.length+' 项规划任务';
  card.querySelector('.thread-footer').children[1].textContent='未绑定实时会话';
 }
 if(thread.roadmap)$('detail-state').textContent='规划 · 状态未同步';
 $('caption').textContent='真实来源：'+s.repository+' · '+s.plan.id;
 const planned=threads.filter(t=>t.roadmap).length,active=threads.filter(t=>t.active).length;
 $('counts').textContent=(threads.length-planned)+' 个真实绑定线程 · '+s.plan.tasks.filter(t=>t.enabled!==false).length+' 个批准任务 · '+planned+' 个非运行节点 · 执行中 '+active+' · 待审 '+pending;
 $('current').textContent='调度 '+s.runtime.workflow.status+' · M '+s.manager.status+' · '+(s.paused.workflow||s.paused.gate?'已暂停':'事件监督中');
 $('next').textContent='G1/G2未通过 · 不提前启动D5；没有待审则零模型请求';
 $('return-parts').disabled=true;
 $('play').disabled=$('step').disabled=$('reset').disabled=true;
 $('replay').disabled=$('disconnect').disabled=$('restart').disabled=$('reconcile').disabled=true;
 const traceOpen=$('detail-body').querySelector('details')?.open||false;
 $('detail-body').replaceChildren();
 addField(thread.roadmap?'规划归组 / Harness':'真实线程 / 模型',thread.session+' / '+thread.harness);
 if(thread.id==='M')for(const member of s.team?.members||[])addField(member.code+' · '+member.name,member.responsibility);
 const related=s.runs.filter(r=>r.taskId===taskId),latest=related.at(-1);
 if(latest){addField('工作树',latest.worktree);addField('HEAD',latest.head||'未记录');
  for(const run of related){addField('运行第 '+run.attempt+' 轮',run.runId+' · '+(run.decision||run.state));if(run.summary)addField('交付摘要',run.summary);}
  for(const finding of latest.findings||[])addField('审核意见',typeof finding==='string'?finding:JSON.stringify(finding));
  if(latest.progress?.updated)addField('最近执行记录',new Date(latest.progress.updated).toLocaleString());
  const trace=document.createElement('details'),heading=document.createElement('summary');trace.open=traceOpen;heading.textContent='执行轨迹 · 最近 '+(latest.progress?.events?.length||0)+' 条';trace.append(heading);
  for(const event of latest.progress?.events||[]){const line=document.createElement('div');line.textContent=event.label+' · '+event.status;trace.append(line);for(const step of event.steps||[]){const item=document.createElement('div');item.textContent=(step.completed?'✓ ':'○ ')+step.text;trace.append(item);}}
  $('detail-body').append(trace);
  if(latest.integration){addField('主线集成',latest.integration.mainSha);addField('独立验证证据',latest.integration.evidence);}
 }else addField('任务状态',thread.roadmap?'未接入当前批准workflow；进度未实时核实，不表示尚未开发或正在运行':taskId in (s.plan.gates||{})?'遵守plan.json实际门禁':'规划任务：当前批准workflow没有本任务运行记录');
 for(const run of s.runs){const owner=threads.find(t=>t.tasks.some(a=>a.id===run.taskId)),card=owner&&$('thread-'+owner.id);if(!card||card.querySelector('.thread-activity'))continue;const line=document.createElement('div');line.className='thread-activity';const last=run.progress?.events?.at(-1);line.textContent=last?last.label+' · '+last.status:'尚无执行记录';card.append(line);}
 const note=document.createElement('p');note.className='detail-note';note.textContent='当前真实CLI协议为整项交付。多任务线程/部分打回演示尚未转成生产协议，页面不会直接写审核决定或修改历史。';$('detail-body').append(note);
 const mode=s.policy.mode;
 $('auto-mode').setAttribute('aria-pressed',String(mode==='auto'));$('manual-mode').setAttribute('aria-pressed',String(mode==='manual'));
 $('mode-description').textContent=mode==='auto'?'自动模式：按已批准依赖推进，最多2个worker；暂停与恢复由独立按钮控制。':'手动模式：允许编辑依赖、模型及任务范围；模式切换不暂停调度，暂停需明确操作。';
 sourceLabel.textContent='已接入真实开发流程 · 本地数据';$('source-status').textContent='真实连接 · '+new Date(s.at).toLocaleTimeString();
 $('edit-plan').disabled=mode!=='manual';
 const controllable=s.plan.tasks.some(t=>t.id===taskId);
 $('revoke-task').disabled=!controllable;$('rebuild-task').disabled=!controllable;
 $('show-inactive').title='关闭时隐藏规划、等待和已完成的执行流程；M监督节点始终保留。';
}
async function refreshLive(){
 if(requestBusy)return;requestBusy=true;
 try{const auth=await api('/api/session');consoleToken=auth.token;const s=await api('/api/state');liveSnapshot=s;document.body.classList.remove('connection-lost');liveGraph(s);}
 catch(error){$('source-status').textContent='连接失败：'+error.message;document.body.classList.add('connection-lost');document.dispatchEvent(new Event('automation-connection-lost'));}
 finally{requestBusy=false;}
}
async function command(action,data={}){
 if(requestBusy)return;requestBusy=true;for(const id of liveControls)$(id).disabled=true;
 try{liveSnapshot=await api('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Console-Token':consoleToken},body:JSON.stringify({action,data})});liveGraph(liveSnapshot);}
 catch(error){$('source-status').textContent='命令失败：'+error.message;}
 finally{requestBusy=false;for(const id of liveControls)$(id).disabled=false;if(liveSnapshot)$('edit-plan').disabled=liveSnapshot.policy.mode!=='manual';}
}
function openEditor(){
 if(!liveSnapshot)return;
 const dialog=document.createElement('dialog');dialog.className='plan-editor';
 dialog.innerHTML='<h2>手动计划 · 拓扑 / 模型 / 执行范围</h2><p>dependsOn / integrationDependsOn 是连线；harness / model 是执行器。已启动任务、门禁和运行身份不可改。新增任务须完整提示词、验证脚本与集成契约。保存草稿不派工；应用经现有Approve入口校验后保持暂停，恢复流程才执行。</p><textarea aria-label="工作流计划JSON" spellcheck="false"></textarea><p class="editor-status" role="status"></p><div><button class="optimize">默认调优未启动任务</button><button class="save primary">保存待审计划</button><button class="apply">应用已保存计划</button><button class="close">关闭</button></div>';
 const textarea=dialog.querySelector('textarea');textarea.value=JSON.stringify(liveSnapshot.draft?.plan||liveSnapshot.plan,null,2);
 const controls=document.createElement('div');controls.className='task-plan-controls';
 const select=document.createElement('select');select.setAttribute('aria-label','选择编辑任务');
 for(const t of liveSnapshot.plan.tasks){const option=document.createElement('option');option.value=t.id;option.textContent=t.id;select.append(option);}
 const model=document.createElement('input');model.setAttribute('aria-label','任务模型');
 const deps=document.createElement('input');deps.setAttribute('aria-label','执行依赖（逗号分隔）');
 const integrationDeps=document.createElement('input');integrationDeps.setAttribute('aria-label','集成依赖（逗号分隔）');
 const update=document.createElement('button');update.textContent='更新此任务连线与模型';
 const note=document.createElement('span');
 const loadTask=()=>{try{const t=JSON.parse(textarea.value).tasks.find(t=>t.id===select.value);model.value=t.model;deps.value=(t.dependsOn||[]).join(',');integrationDeps.value=(t.integrationDependsOn||[]).join(',');note.textContent='可修改运行中任务的后续版本；应用新版本会撤销旧执行并保留其历史，原版本不被覆写。';}catch(e){note.textContent=e.message;}};
 select.onchange=loadTask;update.onclick=()=>{try{const p=JSON.parse(textarea.value),t=p.tasks.find(t=>t.id===select.value);t.model=model.value.trim();t.dependsOn=deps.value.split(',').map(x=>x.trim()).filter(Boolean);t.integrationDependsOn=integrationDeps.value.split(',').map(x=>x.trim()).filter(Boolean);textarea.value=JSON.stringify(p,null,2);note.textContent='已更新编辑草稿，尚未保存或应用。';}catch(e){note.textContent=e.message;}};
 for(const [label,input] of [['任务',select],['模型',model],['执行依赖',deps],['集成依赖',integrationDeps]]){const field=document.createElement('label');field.textContent=label+' ';field.append(input);controls.append(field);}
 controls.append(update,note);textarea.before(controls);loadTask();
 const revise=document.createElement('button');revise.textContent='应用此任务新版本';revise.onclick=async()=>{try{const t=JSON.parse(textarea.value).tasks.find(t=>t.id===select.value);await taskCommand('update_task',t.id,{model:t.model,harness:t.harness,dependsOn:t.dependsOn||[],integrationDependsOn:t.integrationDependsOn||[],allowedPaths:t.allowedPaths,integrationPaths:t.integrationPaths||[],promptFile:t.promptFile,integrationPromptFile:t.integrationPromptFile,verificationFile:t.verificationFile});dialog.querySelector('.editor-status').textContent=$('task-command-status').textContent;}catch(e){dialog.querySelector('.editor-status').textContent=e.message;}};controls.append(revise);
 dialog.querySelector('.optimize').onclick=()=>{try{const p=JSON.parse(textarea.value),started=new Set(liveSnapshot.runs.map(r=>r.taskId));for(const t of p.tasks)if(!started.has(t.id)){t.model='devin/swe-2';t.integrationDependsOn=[...new Set(t.integrationDependsOn||[])];}textarea.value=JSON.stringify(p,null,2);dialog.querySelector('.editor-status').textContent='默认调优：未启动任务优先免费模型、依赖去重；保留原会话及门禁。';}catch(e){dialog.querySelector('.editor-status').textContent=e.message;}};
 dialog.querySelector('.save').onclick=async()=>{try{const plan=JSON.parse(textarea.value);const result=await api('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Console-Token':consoleToken},body:JSON.stringify({action:'draft',data:{plan,baseRevision:liveSnapshot.planRevision}})});liveSnapshot=result;dialog.querySelector('.editor-status').textContent='已持久保存至tmp/automation-console/draft.json，状态pending_manager_review；未改生产批准计划，未启动模型。';}catch(e){dialog.querySelector('.editor-status').textContent='未保存：'+e.message;}};
 dialog.querySelector('.close').onclick=()=>{dialog.close();dialog.remove();};document.body.append(dialog);dialog.showModal();
 dialog.querySelector('.apply').onclick=async()=>{try{const result=await api('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Console-Token':consoleToken},body:JSON.stringify({action:'apply'})});liveSnapshot=result;dialog.querySelector('.editor-status').textContent='计划已通过现有Approve入口并应用，仍保持暂停；点击恢复已批准流程才开始执行。';}catch(e){dialog.querySelector('.editor-status').textContent='未应用：'+e.message;}};
}
$('refresh-live').onclick=refreshLive;$('auto-mode').onclick=()=>command('mode',{mode:'auto'});$('manual-mode').onclick=()=>command('mode',{mode:'manual'});
$('pause-live').onclick=()=>command('pause');$('resume-live').onclick=()=>command('resume');$('edit-plan').onclick=openEditor;
async function taskCommand(action,taskId,changes={}){
 const key='automation.pending.'+action+'.'+taskId;
 const commandId=localStorage.getItem(key)||crypto.randomUUID();localStorage.setItem(key,commandId);
 $('task-command-status').textContent='提交控制请求 '+commandId+'；旧任务将停止自动派工/集成。';
 try{const result=await api('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Console-Token':consoleToken},body:JSON.stringify({action,data:{taskId,commandId,changes}})});liveSnapshot=result;liveGraph(result);const claim=result.taskControls.find(c=>c.commandId===commandId);$('task-command-status').textContent=claim?.status==='queued'?'请求已持久排队，等待M本次主线写入结束后自动应用。':'控制已落盘 · '+(claim?.replacementTask||taskId)+' · 历史保留';localStorage.removeItem(key);}catch(e){$('task-command-status').textContent='请求未确认：'+e.message+'；保留原commandId，禁止盲目重发。';throw e;}
}
$('revoke-task').onclick=()=>taskCommand('revoke',selected.task).catch(()=>{});
$('rebuild-task').onclick=()=>taskCommand('rebuild',selected.task).catch(()=>{});
$('show-inactive').onchange=()=>{showInactive=$('show-inactive').checked;localStorage.setItem('automation.showInactive',String(showInactive));if(liveSnapshot)liveGraph(liveSnapshot);};
(async()=>{try{const auth=await api('/api/session');consoleToken=auth.token;await refreshLive();refreshTimer=setInterval(refreshLive,5000);}catch(e){$('source-status').textContent='未接入真实工作流 · 当前为示例';$('mode-description').textContent='真实入口由scripts/automation-console-control.ps1启动；静态HTML只展示示例，不操作生产。';for(const id of liveControls)$(id).disabled=true;}})();
