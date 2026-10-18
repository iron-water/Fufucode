import {readFileSync,writeFileSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
const base='docs/research-results/R01/evidence/';
const commit='a13d35a742c6ef8462812a28fbe1d8c8b7431c32';
const paths=['docs/extensions.md','docs/rpc-extension-ui.md','src/modes/rpc/rpc-mode.ts','src/core/agent-session.ts','src/core/extensions/loader.ts'];
const records=[];
for(const path of paths){
 const result=JSON.parse(execFileSync('gh',['api',`repos/earendil-works/pi/contents/packages/coding-agent/${path}?ref=${commit}`],{encoding:'utf8',maxBuffer:16*1024*1024,windowsHide:true}));
 const b=Buffer.from(result.content,'base64');
 const filename='pi-upstream-'+path.replaceAll('/','__');writeFileSync(base+filename,b);
 const record={path,commit,filename,sha256:createHash('sha256').update(b).digest('hex')};
 if(path.startsWith('docs/')) record.matchesInstalled=readFileSync('runtime/pi/node_modules/@earendil-works/pi-coding-agent/'+path).equals(b);
 records.push(record);
}
writeFileSync(base+'pi-reference-sources.json',JSON.stringify(records,null,2));
console.log(JSON.stringify(records,null,2));
