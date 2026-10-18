// Human requirements are stored first. Only M acknowledges their applicability.
window.addEventListener('DOMContentLoaded',()=>{
 const panel=document.querySelector('.node-panel');
 const button=document.createElement('button');button.textContent='追加要求 / 验收标准';button.id='human-requirement';panel.insertBefore(button,panel.querySelector('.task-live-actions'));
 const form=document.createElement('dialog');form.className='node-panel human-form';
 form.innerHTML='<h2>提交给 M</h2><p class="human-scope"></p><label>开发步骤 / 交付部分（可选定位）<input name="step" placeholder="例如：事件去重 / 崩溃恢复"></label><label>新要求<textarea name="requirement" required placeholder="希望怎样改变；哪些现有行为应保留"></textarea></label><label>验收条件（每行一条）<textarea name="acceptance" required placeholder="可观察、可验证的条件"></textarea></label><div><button class="template">填入模板</button><button class="submit">落盘并交给 M</button><button class="close">关闭</button></div><p class="human-status" role="status"></p>';
 document.body.append(form);let binding=null;
 const replaceLabel=document.createElement('label');replaceLabel.className='replace-requirements';replaceLabel.innerHTML='<span><input type="checkbox" name="replace"> 替代先前新增要求（保留历史）</span>';form.querySelector('div').before(replaceLabel);
 button.onclick=()=>{
  const s=liveSnapshot,task=s?.plan.tasks.find(t=>t.id===selected.task&&t.enabled!==false);
  if(!task){alert('此节点没有可控的已批准任务；请先明确任务身份。');return;}
  const thread=threads.find(t=>t.id===selected.thread),run=s.runs.filter(r=>r.taskId===task.id).at(-1),req=s.humanRequirements?.find(r=>r.taskId===task.id);
  binding={schemaVersion:1,type:'human.change.request',commandId:crypto.randomUUID(),projectId:s.plan.id,threadId:run?.session||thread.id,taskId:task.id,runId:run?.runId||null,baseHead:run?.head||null,baseRevision:req?.revision||0,action:'append_acceptance',createdAt:new Date().toISOString()};
  form.querySelector('.human-scope').textContent=task.id+' · 需求版本 '+binding.baseRevision+'；当前按整项任务审核，步骤仅定位要求。';
  form.querySelector('[name=requirement]').value='';form.querySelector('[name=acceptance]').value='';form.querySelector('[name=step]').value='';form.querySelector('.human-status').textContent='保存后排队；M确认与产品审核通过是不同状态。';form.showModal();
  form.querySelector('[name=replace]').checked=false;
 };
 form.querySelector('.template').onclick=()=>{form.querySelector('[name=requirement]').value='目标：\n新增或调整：\n必须保留：\n范围之外：';form.querySelector('[name=acceptance]').value='正常行为：\n失败与恢复：\n验证证据：';};
 form.querySelector('.close').onclick=()=>form.close();
 const expert=document.createElement('button');expert.textContent='请求路线专家评估';form.querySelector('div').append(expert);
 expert.onclick=async()=>{
  const status=form.querySelector('.human-status'),requirement=form.querySelector('[name=requirement]').value.trim();
  if(!requirement){status.textContent='先填写路线变更要求和依据。';return;}
  const key='architecture-command-'+binding.taskId,saved=localStorage.getItem(key),data=saved?JSON.parse(saved):{commandId:crypto.randomUUID(),taskId:binding.taskId,baseRevision:liveSnapshot.planRevision,requirement};
  if(data.requirement!==requirement){status.textContent='前一条路线请求待核对，不能换正文重发。';return;}
  if(!saved&&!confirm('提交给三名高能力路线专家，仅生成建议，不直接修改任务图。确认这是路线/架构变更，而非普通验收补充？'))return;
  localStorage.setItem(key,JSON.stringify(data));expert.disabled=true;
  try{liveSnapshot=await api('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Console-Token':consoleToken},body:JSON.stringify({action:'architecture_request',data})});localStorage.removeItem(key);status.textContent='路线请求已落盘；专家提案需通过受控版本入口，不能直接覆盖任务图。';}
  catch(error){status.textContent=error.message;}
  finally{expert.disabled=false;}
 };
 form.querySelector('.submit').onclick=async()=>{
  const status=form.querySelector('.human-status'),submit=form.querySelector('.submit');
  const requirement=form.querySelector('[name=requirement]').value.trim(),acceptance=form.querySelector('[name=acceptance]').value.split('\n').map(x=>x.trim()).filter(Boolean);
  if(!requirement||!acceptance.length){status.textContent='请填写要求和可验证的验收条件。';return;}
  const key='human-command-'+binding.taskId;
  const history=liveSnapshot.humanRequirements?.find(x=>x.taskId===binding.taskId)?.requests||[];
  const candidate={...binding,stepId:form.querySelector('[name=step]').value.trim()||null,partId:null,requirement,acceptance,supersedes:form.querySelector('[name=replace]').checked?history.map(x=>x.commandId):[]};
  const saved=localStorage.getItem(key);let data=candidate;
  if(saved){const prior=JSON.parse(saved);if(prior.requirement!==requirement||JSON.stringify(prior.acceptance)!==JSON.stringify(acceptance)||prior.stepId!==candidate.stepId){status.textContent='上一条提交结果待核对；请勿用不同正文重发。';return;}data=prior;}
  localStorage.setItem(key,JSON.stringify(data));submit.disabled=true;status.textContent='正在持久化请求…';
  try{liveSnapshot=await api('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Console-Token':consoleToken},body:JSON.stringify({action:'human_requirement',data})});localStorage.removeItem(key);liveGraph(liveSnapshot);status.textContent='已落盘，等待 M 确认。命令 '+data.commandId;}
  catch(error){status.textContent='未确认成功：'+error.message+'。原命令已保留，重试使用同一身份。';}
  finally{submit.disabled=false;}
 };
 const status=document.createElement('p');status.className='human-receipts';panel.append(status);
 setInterval(()=>{const s=liveSnapshot,r=s?.humanRequirements?.find(x=>x.taskId===selected.task),events=s?.coreEvents?.filter(e=>e.event.taskId===selected.task&&e.role==='architect')||[];status.textContent=r?'需求版本 '+r.revision+' · '+(r.ready?'M已确认，审核需使用此版本':'等待M确认 / 有阻塞')+' · '+r.requests.map(x=>x.commandId.slice(0,8)).join(', '):'';for(const event of events){const decision=s.coreDecisions?.find(d=>d.key===event.key),claim=s.coreClaims?.find(c=>c.key===event.key);status.textContent+=' · 路线评估 '+(decision?decision.decision+(decision.applied?' / 已应用':' / 未应用'):claim?.status||'queued');}},1000);
});
