// Read-only source collection, not a Pi/runtime experiment. All writes stay in R01.
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { resolve, relative } from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
const root = resolve('C:/Projects/Fufucode');
const out = resolve(root, 'docs/research-results/R01/evidence');
mkdirSync(out, { recursive: true });
const hash = b => createHash('sha256').update(b).digest('hex');
const gh = args => execFileSync('gh', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, windowsHide: true });
const pi = 'runtime/pi/node_modules/@earendil-works/pi-coding-agent/';
const files = [
 'AGENTS.md','docs/AI_DEVELOPMENT_PLAN.md','docs/runtime-baseline.md',
 'docs/research-tasks/README.md','docs/research-tasks/R01-plugins.md',
 'docs/design/frontend-functional-plan.md','docs/frontend-prototype/index.html',
 'runtime/pi/package.json','runtime/pi/package-lock.json',
 pi+'package.json', pi+'dist/modes/rpc/rpc-mode.js',pi+'dist/modes/rpc/rpc-types.d.ts',
 pi+'dist/core/agent-session.js',pi+'dist/core/resource-loader.js',
 pi+'dist/core/extensions/loader.js',pi+'dist/core/extensions/runner.js',pi+'dist/core/extensions/types.d.ts',
 pi+'docs/extensions.md',pi+'docs/rpc-extension-ui.md',pi+'docs/rpc-commands.md',
 pi+'docs/packages.md',pi+'docs/skills.md',pi+'docs/prompt-templates.md',pi+'docs/security.md',
 pi+'examples/extensions/reload-runtime.ts',
 'src/Fufucode.Core/PersistentSessionManager.cs','extensions/fufucode-bridge/index.ts'
];
const manifest = [];
for (const file of files) {
 const abs = resolve(root, file);
 if (!existsSync(abs)) { manifest.push({path:file, missing:true}); continue; }
 const b = readFileSync(abs);
 const entry = {path:file,bytes:b.length,sha256:hash(b)};
 if (file.startsWith(pi)) {
  entry.snapshot = 'pi-'+file.slice(pi.length).replaceAll('/','__');
  writeFileSync(resolve(out,entry.snapshot),b);
 }
 manifest.push(entry);
}
const dshCommit = '5badb15009ae1756c3afe0ae0cef1faafc290ccc';
const tree = JSON.parse(gh(['api',`repos/deepseek-ai/deepseek-harness/git/trees/${dshCommit}?recursive=1`]));
writeFileSync(resolve(out,'dsh-tree.json'),JSON.stringify(tree,null,2));
const candidates = tree.tree.filter(x => x.type==='blob' &&
 /^(docs\/|packages\/(core|loader|plugin|harness|cli))/.test(x.path) &&
 /reload|hot|plugin|architecture|lifecycle/.test(x.path) && !/tests|fixtures|\.i18n|\.zh\./.test(x.path));
writeFileSync(resolve(out,'dsh-candidate-paths.json'),JSON.stringify(candidates.map(x=>x.path),null,2));
const meta = JSON.parse(gh(['repo','view','deepseek-ai/deepseek-harness','--json','nameWithOwner,url,defaultBranchRef,description']));
const tag = JSON.parse(gh(['api','repos/earendil-works/pi/git/ref/tags/v1.0.0']));
writeFileSync(resolve(out,'upstream-metadata.json'),JSON.stringify({collectedAt:new Date().toISOString(),piTag:tag,dsh:{...meta,commit:dshCommit},gitHead:execFileSync('git',['rev-parse','HEAD'],{encoding:'utf8',windowsHide:true}).trim()},null,2));
writeFileSync(resolve(out,'local-manifest.json'),JSON.stringify(manifest,null,2));
console.log(JSON.stringify({files:manifest.length,dshTreeTruncated:tree.truncated,candidates:candidates.map(x=>x.path).slice(0,65)},null,2));
