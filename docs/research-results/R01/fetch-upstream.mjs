import {readFileSync,writeFileSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
const out='docs/research-results/R01/evidence/';
const commit='5badb15009ae1756c3afe0ae0cef1faafc290ccc';
const tree=JSON.parse(readFileSync(out+'dsh-tree.json','utf8')).tree;
const wanted=['README.md','package.json','docs/architecture.md','docs/cordis-tutorial/02-lifecycle-and-effects.md',
 '.agents/notes/implemented/simplification/2026-09-09-nontransactional-loader.md',
 'vendor/loader/src/config/entry.ts','vendor/loader/src/index.ts'];
for(const x of tree) if(x.type==='blob' &&
 /^(packages\/boot\/(hmr|plugin-manager)\/)(README\.md|src\/[^/]+\.ts)$/.test(x.path)) wanted.push(x.path);
const manifest=[];
for(const path of [...new Set(wanted)]){
 const raw=execFileSync('gh',['api',`repos/deepseek-ai/deepseek-harness/contents/${path}?ref=${commit}`],{encoding:'utf8',maxBuffer:16*1024*1024,windowsHide:true});
 const obj=JSON.parse(raw); const b=Buffer.from(obj.content,'base64');
 const filename='dsh-'+path.replaceAll('/','__'); writeFileSync(out+filename,b);
 manifest.push({path,commit,filename,bytes:b.length,sha256:createHash('sha256').update(b).digest('hex'),url:`https://github.com/deepseek-ai/deepseek-harness/blob/${commit}/${path}`});
}
writeFileSync(out+'dsh-sources.json',JSON.stringify(manifest,null,2));
console.log(manifest.map(x=>x.path+' '+x.bytes).join('\n'));
