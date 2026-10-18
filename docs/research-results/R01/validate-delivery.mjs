// Narrow document/evidence verification; no runtime experiment or Git mutation.
import {readFileSync,writeFileSync,existsSync,statSync} from 'node:fs';
import {resolve} from 'node:path';
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
const root=resolve('C:/Projects/Fufucode');
const base=resolve(root,'docs/research-results/R01');
const out=resolve(base,'evidence');
const hash=b=>createHash('sha256').update(b).digest('hex');
const manifestFile=resolve(out,'local-manifest.json');
const manifest=JSON.parse(readFileSync(manifestFile,'utf8'));
const extra=['src/Fufucode.Core/ChatSessionCoordinator.cs','src/Fufucode.Pi/PiRpcClient.cs',
 'src/Fufucode.App/Integration/ExtensionCatalog.cs','src/Fufucode.App/Integration/ExtensionPreferences.cs',
 'src/Fufucode.App/ViewModels/ExtensionsPageViewModel.cs','src/Fufucode.App/Views/DockRegionView.cs',
 'src/Fufucode.App/Infrastructure/WorkspaceLayoutPreferences.cs'];
for(const path of extra) if(!manifest.some(x=>x.path===path)){
 const b=readFileSync(resolve(root,path));manifest.push({path,bytes:b.length,sha256:hash(b)});
}
writeFileSync(manifestFile,JSON.stringify(manifest,null,2));
const checks=[];
let failures=0;
function check(name,pass,detail){checks.push({name,pass,detail});if(!pass)failures++;}
for(const x of manifest){
 if(x.missing){check('local '+x.path,false,'missing');continue;}
 const b=readFileSync(resolve(root,x.path));check('local hash '+x.path,hash(b)===x.sha256,'content remains the captured bytes');
 if(x.snapshot)check('snapshot '+x.path,hash(readFileSync(resolve(out,x.snapshot)))===x.sha256,'stored source matches');
}
for(const x of JSON.parse(readFileSync(resolve(out,'dsh-sources.json'),'utf8'))){
 check('dsh '+x.path,hash(readFileSync(resolve(out,x.filename)))===x.sha256,'fixed-commit snapshot');
}
for(const x of JSON.parse(readFileSync(resolve(out,'pi-reference-sources.json'),'utf8'))){
 check('Pi upstream '+x.path,hash(readFileSync(resolve(out,x.filename)))===x.sha256,'fixed-commit snapshot');
 if(x.matchesInstalled!==undefined)check('Pi document parity '+x.path,x.matchesInstalled,'same bytes as installed package');
}
const report=readFileSync(resolve(base,'report.md'),'utf8');
check('fenced blocks balanced',(report.match(/^```/gm)||[]).length%2===0,'Markdown');
check('seven task areas covered',[3,4,5,6,7,8,9].every(n=>new RegExp('^## '+n+'[.]','m').test(report)),'numbered research sections');
for(const m of report.matchAll(/\]\(([^)]+)\)/g)){
 if(!m[1].startsWith('https://'))check('relative artifact '+m[1],existsSync(resolve(base,m[1])),'local link');
}
const metadata=JSON.parse(readFileSync(resolve(out,'upstream-metadata.json'),'utf8'));
const git=(args)=>execFileSync('git',['--no-optional-locks',...args],{encoding:'utf8',windowsHide:true});
check('baseline HEAD unchanged',git(['rev-parse','HEAD']).trim()===metadata.gitHead,'read-only Git comparison');
check('branch main',git(['branch','--show-current']).trim()==='main','read-only Git comparison');
writeFileSync(resolve(out,'final-git-status.txt'),git(['status','--short','--untracked-files=all']));
const result={checkedAt:new Date().toISOString(),scope:'R01 documentation and source collection only; no product/runtime test',total:checks.length,failures,checks};
writeFileSync(resolve(base,'verification.json'),JSON.stringify(result,null,2));
console.log(JSON.stringify({total:checks.length,failures},null,2));
if(failures)process.exitCode=1;
