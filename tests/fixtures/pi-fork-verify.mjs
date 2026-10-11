import { SessionManager } from '../../runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/core/session-manager.js';
import { spawn } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createInterface } from 'node:readline';
const root=resolve('tmp/a8-protocol'); mkdirSync(root,{recursive:true});
const sessions=resolve(root,'sessions'); const sm=SessionManager.create(root,sessions);
const user1=sm.appendMessage({role:'user',content:[{type:'text',text:'Seed one'}],timestamp:Date.now()});
const assistant=sm.appendMessage({role:'assistant',content:[{type:'text',text:'Seed response'}],api:'openai-completions',provider:'test',model:'test',usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}},stopReason:'stop',timestamp:Date.now()});
const user2=sm.appendMessage({role:'user',content:[{type:'text',text:'Seed two'}],timestamp:Date.now()});
const source=sm.getSessionFile();
const branched=SessionManager.create(root,resolve(root,'branch-sessions'));
const bu=branched.appendMessage({role:'user',content:[{type:'text',text:'Branch root'}],timestamp:Date.now()});
const ba=branched.appendMessage({role:'assistant',content:[{type:'text',text:'Original child'}],api:'openai-completions',provider:'test',model:'test',usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}},stopReason:'stop',timestamp:Date.now()});
const branchBefore=branched.getSessionFile();branched.branch(bu);
const sibling=branched.appendMessage({role:'user',content:[{type:'text',text:'Sibling child'}],timestamp:Date.now()});
console.log(JSON.stringify({sdkBranch:{sameFile:branchBefore===branched.getSessionFile(),root:bu,oldChild:ba,newChild:sibling,leafId:branched.getLeafId(),tree:branched.getTree()}}));
const node=resolve('runtime/node/node.exe'),cli=resolve('runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/cli.js');
const child=spawn(node,[cli,'--mode','rpc','--session',source,'--session-dir',sessions,'--no-extensions','--no-skills','--no-prompt-templates','--no-themes'],{cwd:root,env:{...process.env,PI_CODING_AGENT_DIR:resolve(root,'agent')},windowsHide:true,stdio:['pipe','pipe','pipe']});
const pending=new Map(),transcript=[];let seq=0;
child.stderr.on('data',d=>transcript.push({stderr:d.toString()}));
createInterface({input:child.stdout}).on('line',line=>{let v;try{v=JSON.parse(line);}catch{return;}transcript.push(v);if(v.type==='response'&&pending.has(v.id)){pending.get(v.id)(v);pending.delete(v.id);}});
function rpc(type,args={}){let id=`a8-${++seq}`;return new Promise((res,rej)=>{let t=setTimeout(()=>rej(new Error(`timeout ${type}`)),20000);pending.set(id,v=>{clearTimeout(t);res(v);});child.stdin.write(JSON.stringify({id,type,...args})+'\n');});}
try{
 console.log(JSON.stringify({source,user1,assistant,user2}));
 for(const type of ['get_state','get_fork_messages','get_entries','get_tree']) console.log(JSON.stringify(await rpc(type)));
 console.log(JSON.stringify(await rpc('fork',{entryId:user2})));
 const state=await rpc('get_state');console.log(JSON.stringify(state));
 console.log(JSON.stringify({forkHeader:JSON.parse(readFileSync(state.data.sessionFile,'utf8').split('\n')[0])}));
 console.log(JSON.stringify(await rpc('get_entries')));
 console.log(JSON.stringify(await rpc('clone')));
 const cloned=await rpc('get_state');console.log(JSON.stringify(cloned));
 console.log(JSON.stringify({cloneHeader:JSON.parse(readFileSync(cloned.data.sessionFile,'utf8').split('\n')[0])}));
 console.log(JSON.stringify(await rpc('navigate_tree',{targetId:user1})));
 console.log(JSON.stringify(await rpc('fork',{entryId:'missing-entry'})));
} finally {writeFileSync(resolve(root,'rpc-transcript.json'),JSON.stringify(transcript,null,2));child.kill();}
