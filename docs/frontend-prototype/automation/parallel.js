'use strict';
const $=id=>document.getElementById(id);
let snapshot=null,token=null,busy=false,currentDecision=null;
const labels={draft:'待批准',approved:'已批准 · 待执行接入',waiting:'等待依赖',running:'开发中',stopped:'执行已停止',delivered:'待验收',accepted:'已接受 · 尚未集成',rework:'部分待修复',integrated:'已集成',integration_failed:'集成失败',unknown:'状态不明 · 待对账',superseded:'已被后继替代'};
const relations={independent:'独立开发',competitive:'同题择优',complementary:'实现与证据互补',pipeline:'依赖流水'};
const decisionLabels={plan:'批准工作包',review:'验收交付部分',integration:'批准集成',choice:'选择方案',restructure:'调整分工',knowledge:'发布公共知识',adoption:'采用契约新版本'};
const list=v=>v.split(/[,，\n]/).map(x=>x.trim()).filter(Boolean);
const rows=v=>v.split('\n').map(x=>x.trim()).filter(Boolean);
const el=(tag,text,cls)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e;};
async function api(path,options){const r=await fetch(path,options),body=await r.json();if(!r.ok){const e=new Error(body.error||'请求失败');e.definite=true;throw e;}return body;}
async function refresh(){if(busy)return;try{if(!token)token=(await api('/api/session')).token;snapshot=await api('/api/development');render();$('connection').textContent='已保存至本机';}catch(e){$('connection').textContent='连接不可用 · 不确认执行状态';$('notice').textContent=e.message;document.querySelectorAll('.package.running').forEach(n=>n.classList.remove('running'));}}
async function send(action,data,revision=snapshot.state.revision){
 if(busy)throw Error('上一操作尚未完成');busy=true;
 const key='development.pendingCommand',prior=JSON.parse(localStorage.getItem(key)||'null');
 let command={commandId:crypto.randomUUID(),baseRevision:revision,action,data};
 try{
  if(prior){if(prior.action!==action||JSON.stringify(prior.data)!==JSON.stringify(data))throw Error('上次操作结果不明，请先重试原操作以对账');command=prior;}
  localStorage.setItem(key,JSON.stringify(command));
  snapshot=await api('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Console-Token':token},body:JSON.stringify({action:'development',data:command})});
  localStorage.removeItem(key);render();$('notice').textContent='已记录 · 版本 '+snapshot.state.revision;return snapshot;
 }catch(e){if(e.definite)localStorage.removeItem(key);$('notice').textContent=e.message;throw e;}
 finally{busy=false;}
}
function open(id){if(busy||!snapshot){$('notice').textContent='请等待当前操作保存完成';return;}const d=$(id);d.querySelector('.error')?.replaceChildren();const form=d.querySelector('form');if(form)form.dataset.revision=snapshot.state.revision;d.showModal();}
for(const button of document.querySelectorAll('dialog .close'))button.onclick=()=>button.closest('dialog').close();
function wireForm(id,action,build){const form=$(id);form.onsubmit=async e=>{e.preventDefault();const submit=form.querySelector('[type=submit]');submit.disabled=true;try{await send(action,build(Object.fromEntries(new FormData(form))),Number(form.dataset.revision));form.closest('dialog').close();}catch(error){form.querySelector('.error').textContent=error.message;}finally{submit.disabled=false;}};}
function render(){
 const s=snapshot.state;$('mode').value=s.mode;
 $('mode-title').textContent={manual:'全人工：你直接掌控开发',parallel:'半自动：你做决定，系统组织协作',automatic:'全自动：保留入口，架构待定'}[s.mode];
 $('mode-note').textContent={manual:'默认不自动安排执行。可以整理工作包，或使用原有对话会话。',parallel:'批准后生成受控执行意图；交付、择优、契约与分工调整集中等你决定。',automatic:'本轮不启用新的全自动策略。已有自动门控在独立入口中保持原运行设置。'}[s.mode];
 const packages=Object.values(s.packages).filter(p=>$('show-history').checked||p.status!=='superseded');
 $('counts').textContent=packages.length+' 个工作包';
 const graph=$('graph');graph.replaceChildren();
 const covered=new Set();
 for(const g of Object.values(s.groups)){const members=packages.filter(p=>g.members.includes(p.id));if(!members.length)continue;members.forEach(p=>covered.add(p.id));renderGroup(graph,relations[g.relation]+' · '+g.id,members,g);}
 const free=packages.filter(p=>!covered.has(p.id));if(free.length)renderGroup(graph,'独立工作包',free);
 if(!packages.length)graph.append(el('p','先创建工作包，写清目标、范围和验收。多个任务可组织为独立、竞争、互补或流水关系。','empty'));
 const pending=Object.values(s.decisions).filter(d=>['pending','waiting_stop'].includes(d.status));
 $('inbox-count').textContent=pending.length;$('inbox').replaceChildren();
 for(const d of pending){const b=el('button',decisionLabels[d.kind],'decision');b.append(el('small',(d.targets.join('、')||d.key)+(d.status==='waiting_stop'?' · 等待实际停止':'')));b.onclick=()=>showDecision(d.id);$('inbox').append(b);}
 if(!pending.length)$('inbox').append(el('p','没有待处理决定。','empty'));
 $('intents').replaceChildren();for(const i of Object.values(s.intents))$('intents').append(el('p',i.packageId+' · '+i.kind+' · '+({awaiting_adapter:'待底层接入执行',reported:'已收到执行报告',superseded:'已失效'}[i.status]||i.status)));
 $('events').replaceChildren();for(const e of s.events.slice(-25).reverse())$('events').append(el('li','#'+e.sequence+' · '+e.action+' · '+e.actor));
}
function renderGroup(container,title,packages,group){
 const section=el('section',undefined,'relation-group');section.append(el('h2',title));
 if(group?.relation==='competitive')section.append(el('p','基线 '+group.baseline+' · '+group.criteria.join(' / ')+(group.winner?' · 已选 '+group.winner:' · 尚未择优'),'meta'));
 const threads=el('div',undefined,'threads'),byThread=new Map();
 for(const p of packages){const key=p.threadId||'planned-'+p.id;if(!byThread.has(key))byThread.set(key,[]);byThread.get(key).push(p);}
 for(const [thread,items] of byThread){const card=el('article',undefined,'thread');card.append(el('strong',items[0].threadId?'线程 '+thread:'待分配执行线程'));
  for(const p of items){const b=el('button',p.title,'package '+p.status);b.dataset.package=p.id;b.append(el('span',p.id+' · v'+p.version+' · '+labels[p.status]),el('span',p.harness+' / '+p.model));const done=Object.values(p.parts).filter(x=>x.status==='accepted').length;if(Object.keys(p.parts).length)b.append(el('span',done+' / '+Object.keys(p.parts).length+' 个交付部分已接受'));b.onclick=()=>showPackage(p.id);card.append(b);}threads.append(card);
 }section.append(threads);
 const deps=packages.flatMap(p=>p.dependsOn.map(d=>d+' → '+p.id));if(deps.length)section.append(el('p',deps.join('　'),'links'));
 const overlaps=[];for(let i=0;i<packages.length;i++)for(let j=i+1;j<packages.length;j++){const a=packages[i],b=packages[j];if(a.scope.some(x=>b.scope.includes(x))||a.knowledgeRefs.some(x=>b.knowledgeRefs.includes(x)))overlaps.push(a.id+' / '+b.id);}
 if(overlaps.length)section.append(el('p','可能共享范围或契约：'+overlaps.join('、')+'。仅依据声明提示，不等于已确认冲突。','meta'));
 container.append(section);
}
function showPackage(id){
 const p=snapshot.state.packages[id],body=$('detail');body.replaceChildren(el('h2',p.title),el('p',p.goal),el('p',labels[p.status]),el('p','允许范围：'+p.scope.join('；')),el('p','验收：'+p.acceptance.join('；')));
 if(p.parents.length)body.append(el('p','来源工作包：'+p.parents.join('、')));
 for(const part of Object.values(p.parts)){const section=el('section',undefined,'part');section.append(el('strong',part.id+' · 第 '+part.revision+' 版 · '+part.status),el('p','成果 '+part.head),el('p',part.evidence.join('；')));if(part.reason)section.append(el('p','修复要求：'+part.reason));body.append(section);}
 const prompt=el('details'),summary=el('summary','工作包提示词');prompt.append(summary,el('pre',snapshot.prompts[id]));body.append(prompt);
 const form=el('form');form.innerHTML='<h3>登记已有交付</h3><p>登记证据引用，不自动证明测试通过或实际集成；交付登记后由你验收。</p><label>部分 ID<input name="partId" value="whole" required></label><label>成果 HEAD / 版本<input name="head" required></label><label>证据引用 · 每行一项<textarea name="evidence" required></textarea></label><button type="submit">登记待验收</button><p class="error" role="alert"></p>';
 const rev=snapshot.state.revision;
 form.onsubmit=async e=>{e.preventDefault();const v=Object.fromEntries(new FormData(form));try{await send('delivery.record',{packageId:id,version:p.version,partId:v.partId,head:v.head,evidence:rows(v.evidence)},rev);showPackage(id);}catch(error){form.querySelector('.error').textContent=error.message;}};
 if(p.approved&&!['running','superseded','integrated'].includes(p.status))body.append(form);
 const questions=el('details');questions.append(el('summary','跨工作包问题与答复'));
 for(const q of Object.values(snapshot.state.questions).filter(q=>q.sender===id||q.target===id)){questions.append(el('p',q.sender+' → '+q.target+'：'+q.text),el('p',q.answer||'尚无答复'));
  if(!q.answer){const f=el('form');f.innerHTML='<label>登记答复<textarea name="answer" required></textarea></label><button>保存答复</button><p class="error"></p>';f.onsubmit=async e=>{e.preventDefault();try{await send('question.answer',{id:q.id,text:new FormData(f).get('answer')});showPackage(id);}catch(x){f.querySelector('.error').textContent=x.message;}};questions.append(f);}}
 const ask=el('form');ask.innerHTML='<label>对方工作包 ID<input name="to" required></label><label>问题<textarea name="text" required></textarea></label><button>记录定向问题</button><p class="error"></p>';
 ask.onsubmit=async e=>{e.preventDefault();try{const v=Object.fromEntries(new FormData(ask));await send('question.ask',{from:id,...v});showPackage(id);}catch(x){ask.querySelector('.error').textContent=x.message;}};questions.append(ask,el('p','问题与答复持久记录；原生线程投递待接入。答复不会自动成为公共事实。','meta'));body.append(questions);
 if(!$('detail-dialog').open)open('detail-dialog');
}
function showDecision(id){
 const d=snapshot.state.decisions[id];currentDecision=id;$('decision-title').textContent=decisionLabels[d.kind];const evidence=$('decision-evidence');evidence.replaceChildren();
 for(const target of d.targets){const p=snapshot.state.packages[target];evidence.append(el('h3',p.title),el('p',p.goal),el('p','验收：'+p.acceptance.join('；')));for(const part of Object.values(p.parts))evidence.append(el('p',part.id+' · '+part.head+' · '+part.status+' · '+part.evidence.join('；')));}
 if(d.kind==='knowledge')evidence.append(el('p',d.text),el('p','来源：'+d.source));
 if(d.kind==='adoption')evidence.append(el('p',d.key+' → v'+d.knowledgeVersion+'；采用后旧交付需按新契约复验'));
 if(d.kind==='restructure')for(const p of d.replacements)evidence.append(el('p','后继 '+p.id+'：'+p.goal+' · '+p.harness+'/'+p.model));
 const choices={plan:[['approve','批准']],review:[['accept','接受此部分'],['rework','打回此部分']],integration:[['approve','批准集成意图']],choice:[['select','选择方案']],restructure:[['approve','批准重组']],knowledge:[['approve','批准发布']],adoption:[['approve','批准采用']]};
 const form=$('decision-form'),select=form.elements.choice;select.replaceChildren();for(const [value,text] of [...choices[d.kind],['reject','不批准']]){const option=el('option',text);option.value=value;select.append(option);}
 $('winner-label').hidden=d.kind!=='choice';const winner=form.elements.winner;winner.replaceChildren();for(const id of d.targets){const option=el('option',id);option.value=id;winner.append(option);}form.elements.reason.value='';open('decision-dialog');
}
$('mode').onchange=async()=>{try{await send('mode',{mode:$('mode').value});}catch{if(snapshot)$('mode').value=snapshot.state.mode;}};
$('show-history').onchange=render;
$('new-package').onclick=()=>open('package-dialog');$('new-group').onclick=()=>open('group-dialog');$('restructure').onclick=()=>open('restructure-dialog');
$('knowledge').onclick=()=>{$('knowledge-list').replaceChildren();for(const [key,versions] of Object.entries(snapshot.state.knowledge)){const item=versions.at(-1);$('knowledge-list').append(el('p',key+' v'+item.version+'：'+item.text+' · '+item.source,'knowledge-item'));}open('knowledge-dialog');};
wireForm('package-form','package.create',v=>({...v,scope:rows(v.scope),acceptance:rows(v.acceptance),dependsOn:list(v.dependsOn),knowledgeRefs:list(v.knowledgeRefs)}));
wireForm('group-form','group.create',v=>({...v,members:list(v.members),criteria:rows(v.criteria)}));
wireForm('knowledge-form','knowledge.propose',v=>v);
wireForm('decision-form','decide',v=>({id:currentDecision,...v}));
wireForm('restructure-form','restructure',v=>{const sources=list(v.sources),old=sources.map(id=>{const p=snapshot.state.packages[id];if(!p)throw Error('工作包不存在：'+id);return p;});const union=key=>[...new Set(old.flatMap(p=>p[key]))];return {sources,replacements:rows(v.replacements).map(line=>{const [id,title,goal]=line.split('|').map(x=>x.trim());return {id,title,goal,harness:v.harness,model:v.model,scope:union('scope'),acceptance:union('acceptance'),knowledgeRefs:union('knowledgeRefs'),dependsOn:union('dependsOn').filter(i=>!sources.includes(i))};})};});
refresh();setInterval(refresh,5000);
