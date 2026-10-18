import { spawn } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
const root = resolve('tmp/d1-probe');
mkdirSync(root, { recursive: true });
const model = process.env.D1_MODEL ?? 'devin/swe-2';
const pi = process.argv.includes('--pi');
const args = ['--mode', 'rpc', '--model', model, '--no-extensions', '--no-skills', '--no-tools', '--session-dir', root, '-e', resolve('extensions/fufucode-bridge/index.ts')];
if (!pi) args.push('--no-title', '--no-rules', '--no-lsp');
if (pi) args.unshift(process.env.D1_PI_CLI);
const child = spawn(pi ? process.env.D1_PI_NODE : process.env.D1_OMP_EXE ?? process.env.D1_OMP ?? resolve('runtime/omp/omp.exe'), args, { cwd: root, windowsHide: true });
let buffer = '', id = 0;
const pending = new Map();
let finish;
let assistant;
const finished = new Promise(resolve => { finish = resolve; });
child.stdout.on('data', chunk => { buffer += chunk; let end; while ((end = buffer.indexOf('\n')) >= 0) { const row = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1); if (row.type === 'response') { const waiter = pending.get(row.id); if (waiter) { pending.delete(row.id); waiter(row); } } if (row.type === 'message_end' && row.message?.role === 'assistant') assistant = row.message; if (row.type === 'prompt_result') finish(row); if (pi && row.type === 'agent_settled') finish({ status: assistant?.stopReason === 'error' ? 'error' : assistant?.stopReason === 'aborted' ? 'aborted' : 'completed', agentInvoked: !!assistant, sessionSettled: true, error: { message: assistant?.errorMessage } }); } });
child.stderr.on('data', () => {});
async function query(type) { const key = String(++id); const response = new Promise(resolve => pending.set(key, resolve)); child.stdin.write(JSON.stringify({ id: key, type }) + '\n'); return await Promise.race([response, new Promise((_, reject) => setTimeout(() => reject(new Error(type + ' timeout')), 20000).unref())]); }
try {
  for (const type of ['get_state', pi ? 'get_commands' : 'get_available_commands', 'get_entries', pi ? 'clear_queue' : 'abort_and_restore_queue']) {
    const row = await query(type);
    const data = type === 'get_state' ? { model: row.data?.model?.provider + '/' + row.data?.model?.id, sessionId: row.data?.sessionId, sessionFile: row.data?.sessionFile, isSettled: row.data?.isSettled, queuedMessageCount: row.data?.queuedMessageCount } : type === 'get_available_commands' || type === 'get_commands' ? { commands: row.data?.commands?.filter(c => c.name.startsWith('fufucode')) } : row.data;
    console.log(JSON.stringify({ type, success: row.success, error: row.error, data }));
    if (type === 'get_state' && data.model !== model) throw new Error('Requested model was not selected');
  }
  if (process.argv.includes('--cloud')) {
    child.stdin.write(JSON.stringify({ id: 'cloud', type: 'prompt', message: 'Reply with exactly OK. Do not use any tools.' }) + '\n');
    const result = await Promise.race([finished, new Promise((_, reject) => setTimeout(() => reject(new Error('cloud timeout')), 60000).unref())]);
    console.log(JSON.stringify({ model, status: result.status, agentInvoked: result.agentInvoked, sessionSettled: result.sessionSettled, error: result.error?.message }));
    if (result.status !== 'completed' || !result.agentInvoked || !result.sessionSettled) process.exitCode = 1;
  }
} finally { child.stdin.end(); setTimeout(() => child.kill(), 5000).unref(); }
