import { mkdirSync, writeFileSync, existsSync, readFileSync } from 'node:fs';
import { resolve, join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { PiRpc } from './bridge-verify/lib/pi-rpc.mjs';
import { startStubModel } from './bridge-verify/lib/stub-model.mjs';
import { runtimePaths } from './bridge-verify/lib/repo-root.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const runtime = runtimePaths(repo);
const root = join(repo, 'tmp', 'r05-runtime-' + randomUUID());
const results = [];
const imageBytes = readFileSync(join(repo, 'assets', 'icon', 'icon-small-32.png')).toString('base64');
const scenarios = [
  { mode: 'manual', tool: 'write', written: false, confirm: true },
  { mode: 'auto', tool: 'write', written: true, confirm: false },
  { mode: 'full', tool: 'write', written: true, confirm: false },
  { mode: 'plan', tool: 'write', written: false, confirm: false },
  { mode: 'auto', tool: 'bash', executed: false, confirm: true },
  { mode: 'full', tool: 'bash', executed: true, confirm: false },
  { mode: 'plan', tool: 'bash', executed: false, confirm: false },
  { mode: 'plan', tool: 'read', executed: true, confirm: false },
];
for (const [index, scenario] of scenarios.entries()) {
  const project = join(root, String(index), 'project');
  const agent = join(root, String(index), 'agent');
  mkdirSync(project, { recursive: true }); mkdirSync(agent, { recursive: true });
  const target = join(project, 'out.txt');
  if (scenario.tool === 'read') writeFileSync(target, 'read-only marker');
  const args = scenario.tool === 'write' ? { path: target, content: 'R05 written' }
    : scenario.tool === 'read' ? { path: target } : { command: 'printf R05_SHELL' };
  const server = await startStubModel({ port: 0, script: [
    { type: 'tool', name: scenario.tool, args }, { type: 'text', text: 'R05_SETTLED_OK' },
  ] });
  writeFileSync(join(agent, 'models.json'), JSON.stringify({ providers: {
    'r05-local': { baseUrl: `http://127.0.0.1:${server.port}`, api: 'openai-completions', apiKey: 'local-fixture-only',
      models: [{ id: 'vision-fixture', input: ['text', 'image'] }] },
  } }));
  const rpc = new PiRpc({ nodeExe: runtime.nodeExe, cliJs: runtime.cliJs, cwd: project,
    args: ['--mode', 'rpc', '--no-session', '--no-extensions', '--model', 'r05-local/vision-fixture', '--extension', join(repo, 'extensions', 'fufucode-bridge')],
    env: { PI_CODING_AGENT_DIR: agent },
  });
  try {
    rpc.setUiPolicy(request => request.method === 'confirm' ? { confirmed: false } : null);
    const commands = await rpc.send('get_commands', {}, 30000);
    if (!commands.commands.some(c => c.name === 'fufucode-bridge.mode')) throw new Error('Mode command did not load');
    await rpc.send('prompt', { message: '/fufucode-bridge.mode ' + scenario.mode });
    const framesPromise = rpc.collectUntil(e => e.type === 'agent_settled', 45000);
    await rpc.send('prompt', { message: 'Run the fixture tool, then reply with the acceptance marker', images: [
      { type: 'image', mimeType: 'image/png', data: imageBytes },
    ] });
    const frames = await framesPromise;
    writeFileSync(join(root, String(index), 'requests.json'), JSON.stringify(server.requests, null, 2));
    writeFileSync(join(root, String(index), 'frames.json'), JSON.stringify(frames, null, 2));
    const confirms = rpc.uiRequests.filter(r => r.method === 'confirm').length;
    const tool = frames.find(e => e.type === 'tool_execution_end');
    const state = await rpc.send('get_state');
    const terminal = frames.some(e => e.type === 'message_end' && e.message?.role === 'assistant' &&
      e.message.content?.some(c => c.type === 'text' && c.text.includes('R05_SETTLED_OK')));
    const imageDelivered = server.requests.some(r => r.body.includes('data:image/png;base64,'));
    const written = existsSync(target);
    const correctTool = scenario.tool === 'write' ? written === scenario.written
      : Boolean(tool && !tool.isError) === scenario.executed;
    const passed = correctTool && Boolean(confirms) === scenario.confirm && terminal && imageDelivered && state.isStreaming === false;
    results.push({ ...scenario, passed, confirms, written, toolError: tool?.isError, terminal, imageDelivered, idle: state.isStreaming === false });
    console.log(`${passed ? 'PASS' : 'FAIL'} ${scenario.mode}/${scenario.tool}: confirm=${confirms}, toolError=${tool?.isError}, image=${imageDelivered}, settled=${terminal}`);
  } finally { rpc.kill(); server.close(); }
}
writeFileSync(join(root, 'results.json'), JSON.stringify(results, null, 2));
console.log('R05 runtime evidence: ' + root);
if (results.some(r => !r.passed)) process.exitCode = 1;
