// Real installed Pi + prebuilt CLI acceptance. The HTTP model is scripted, not a cloud model.
import { spawn } from 'node:child_process';
import { mkdir, writeFile, readFile, access } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { startStubModel } from './bridge-verify/lib/stub-model.mjs';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const cli = resolve(process.argv[2] ?? join(root, 'src/Fufucode.Cli/bin/Debug/net10.0/Fufucode.Cli.exe'));
const runRoot = join(root, 'tmp', `cli-real-${Date.now()}`);
const project = join(runRoot, 'project');
const data = join(runRoot, 'data');
await mkdir(project, { recursive: true });
const records = [];
const env = { ...process.env, FUFUCODE_A6_LOCAL_KEY: 'local-test-only' };
delete env.FUFUCODE_A6_INTENTIONALLY_MISSING_KEY;
async function run(args, { approval, appData = data, timeout = 30000, keepInput = false } = {}) {
  const full = ['--project', project, '--app-data', appData, '--json', ...args];
  const child = spawn(cli, full, { cwd: project, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let stdout = '', stderr = '', pending = '', timedOut = false;
  child.stdout.on('data', chunk => {
    stdout += chunk; pending += chunk;
    let end;
    while ((end = pending.indexOf('\n')) >= 0) {
      const line = pending.slice(0, end); pending = pending.slice(end + 1);
      try {
        const row = JSON.parse(line);
        if (row.type === 'approval_request' && approval) {
          child.stdin.write(`approve list\napprove ${approval} ${row.id}\n`);
        }
      } catch { }
    }
  });
  child.stderr.on('data', chunk => stderr += chunk);
  if (!approval && !keepInput) child.stdin.end();
  const timer = setTimeout(() => { timedOut = true; child.kill(); }, timeout);
  const exit = await new Promise((res, rej) => { child.once('error', rej); child.once('close', (code, signal) => res({ code, signal })); });
  clearTimeout(timer);
  const row = { command: [cli, ...full], ...exit, timedOut, stdout: stdout.trim(), stderr: stderr.trim() };
  records.push(row); console.log(JSON.stringify(row));
  if (timedOut) throw new Error(`Timed out: ${args.join(' ')}`);
  return row;
}
function check(condition, message) { if (!condition) throw new Error(message); }
async function exists(path) { try { await access(path); return true; } catch { return false; } }
let server;
try {
  server = await startStubModel({ port: 0, script: [
    { type: 'text', text: 'A6 local model: real Pi completed.' },
    { type: 'tool', name: 'write', args: { path: join(project, 'allowed.txt'), content: 'allowed' } },
    { type: 'text', text: 'A6 approved write completed.' },
    { type: 'tool', name: 'write', args: { path: join(project, 'denied.txt'), content: 'denied' } },
    { type: 'text', text: 'A6 denied write observed.' },
  ] });
  const endpoint = { providerName: 'a6-local', baseUrl: `http://127.0.0.1:${server.port}/v1`, modelId: 'a6-model', apiKeyEnvironmentVariable: 'FUFUCODE_A6_LOCAL_KEY' };
  check((await run(['settings', 'set', 'endpoint', JSON.stringify(endpoint)])).code === 0, 'endpoint setup failed');
  check((await run(['settings', 'set', 'selectedModel', 'a6-local/a6-model'])).code === 0, 'model setup failed');
  const chat = await run(['chat', 'Reply with the acceptance marker.']);
  check(chat.code === 0 && chat.stdout.includes('A6 local model: real Pi completed.'), 'real Pi chat failed');
  const allow = await run(['chat', 'Write allowed.txt.'], { approval: 'accept' });
  check(allow.code === 0 && await exists(join(project, 'allowed.txt')), 'approval accept did not write');
  check(await readFile(join(project, 'allowed.txt'), 'utf8') === 'allowed', 'wrong accepted content');
  const deny = await run(['chat', 'Write denied.txt.'], { approval: 'reject' });
  check(deny.code === 3 && !await exists(join(project, 'denied.txt')), 'approval reject boundary failed');
  check((await run(['models', 'list'])).stdout.includes('a6-model'), 'model unavailable');
  check((await run(['models', 'set', 'a6-local/a6-model'])).code === 0, 'models set failed');
  const sessions = await run(['sessions', 'list']);
  const id = JSON.parse(sessions.stdout)[0].id;
  check((await run(['sessions', 'show', id])).code === 0, 'session show failed');
  const fixture = join(root, 'tests/fixtures/approval-ui-fixture.ts');
  check((await run(['extensions', 'enable', fixture])).code === 0, 'extension enable failed');
  check((await run(['extensions', 'list'])).stdout.includes('approval-ui-fixture'), 'extension list failed');
  check((await run(['extensions', 'disable', fixture])).code === 0, 'extension disable failed');
  const diag = join(runRoot, 'diagnostics.jsonl');
  check((await run(['diag', 'export', diag])).code === 0 && await exists(diag), 'diagnostic export failed');
  check((await run(['models', 'set', 'a6-local/not-a-model'])).code === 3, 'missing model boundary failed');
  const noKeyData = join(runRoot, 'no-key-data');
  await run(['settings', 'set', 'endpoint', JSON.stringify({ ...endpoint, apiKeyEnvironmentVariable: 'FUFUCODE_A6_INTENTIONALLY_MISSING_KEY' })], { appData: noKeyData });
  await run(['settings', 'set', 'selectedModel', 'a6-local/a6-model'], { appData: noKeyData });
  check((await run(['chat', 'Missing credential boundary.'], { appData: noKeyData })).code === 3, 'missing credential boundary failed');
  check((await run(['sessions', 'delete', id])).code === 0, 'session delete failed');
  console.log(`PASS real Pi / scripted model; HTTP requests=${server.requests.length}; artifacts=${runRoot}`);
} finally {
  server?.close();
  await writeFile(join(runRoot, 'report.json'), JSON.stringify({ realPi: true, cloudModel: false, cli, records }, null, 2));
}
