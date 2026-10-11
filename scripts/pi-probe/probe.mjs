import assert from 'node:assert/strict';
import { existsSync, readFileSync, writeFileSync, mkdirSync, mkdtempSync, copyFileSync, readdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createServer } from 'node:http';
import { PiRpc } from '../bridge-verify/lib/pi-rpc.mjs';
import * as contract from './assertions.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '../..');
const opt = {};
for (let i = 2; i < process.argv.length; i++) {
  const key = process.argv[i].replace(/^--/, '');
  opt[key] = process.argv[i + 1]?.startsWith('--') || i + 1 === process.argv.length ? true : process.argv[++i];
}
const output = resolve(opt.output || join(repo, 'tmp', `pi-probe-${Date.now()}`));
mkdirSync(output, { recursive: true });
const report = { schemaVersion: 1, startedUtc: new Date().toISOString(), piRoot: resolve(opt['pi-root']), project: opt.project ? resolve(opt.project) : null, items: [], observations: {}, evidenceRoot: output };
let sequence = 0;
function item(id, name, fn) {
  return (async () => {
    const start = Date.now();
    try { const details = await fn(); report.items.push({ id, name, status: 'PASS', durationMs: Date.now() - start, details }); }
    catch (error) { report.items.push({ id, name, status: error.skip ? 'SKIP' : 'FAIL', durationMs: Date.now() - start, error: error.message }); }
    const result = report.items.at(-1);
    console.log(`${result.status} ${id} ${name}${result.error ? ': ' + result.error : ''}`);
  })();
}
function skip(message) { const e = new Error(message); e.skip = true; throw e; }
function locate(root) {
  for (const scope of ['@earendil-works', '@mariozechner']) {
    const pkg = join(resolve(root), 'node_modules', scope, 'pi-coding-agent');
    if (existsSync(join(pkg, 'package.json'))) {
      const cli = existsSync(join(pkg, 'dist/bundle/cli.js')) ? join(pkg, 'dist/bundle/cli.js') : join(pkg, 'dist/cli.js');
      return { pkg, cli, version: JSON.parse(readFileSync(join(pkg, 'package.json'), 'utf8')).version };
    }
  }
  throw new Error(`No Pi installation in ${root}`);
}
const target = opt['fake-pi'] ? { version: 'FakePi', cli: resolve(opt['fake-pi']) } : locate(opt['pi-root']);
report.version = target.version;
function workspace(tag) {
  const base = mkdtempSync(join(output, `${tag}-`));
  const agent = join(base, 'agent'); const project = opt.project ? resolve(opt.project) : join(base, 'project');
  mkdirSync(agent); mkdirSync(project, { recursive: true });
  return { base, agent, project };
}
async function stub(steps) {
  const requests = []; let taken = 0;
  const server = createServer((req, res) => {
    let body = ''; req.on('data', c => body += c);
    req.on('end', () => {
      if (!req.url.endsWith('/chat/completions')) { res.writeHead(404); res.end(); return; }
      requests.push(JSON.parse(body));
      const step = steps[taken++] || { text: 'probe fallback' };
      setTimeout(() => {
        if (res.destroyed) return;
        res.writeHead(200, { 'content-type': 'text/event-stream' });
        const delta = step.tool ? { role: 'assistant', tool_calls: [{ index: 0, id: `call_probe_${taken}`, type: 'function', function: { name: step.tool, arguments: JSON.stringify(step.args) } }] } : { role: 'assistant', content: step.text };
        const frame = (delta, finish, usage) => ({ id: `probe_${taken}`, object: 'chat.completion.chunk', created: 1, model: 'probe-model', choices: [{ index: 0, delta, finish_reason: finish }], ...(usage ? { usage } : {}) });
        res.write(`data: ${JSON.stringify(frame(delta, null))}\n\n`);
        res.write(`data: ${JSON.stringify(frame({}, step.tool ? 'tool_calls' : 'stop', { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 }))}\n\ndata: [DONE]\n\n`); res.end();
      }, step.delay || 0);
    });
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  return { port: server.address().port, requests, close() { server.closeAllConnections(); server.close(); } };
}
async function start(ws, steps, { extensions = [], session, runtime = target, noExtensions = false, tools } = {}) {
  const model = await stub(steps);
  writeFileSync(join(ws.agent, 'models.json'), JSON.stringify({ providers: { probe: { baseUrl: `http://127.0.0.1:${model.port}`, apiKey: 'local-probe-only', api: 'openai-completions', models: [{ id: 'probe-model', name: 'Local probe' }] } } }));
  const args = ['--mode', 'rpc', '--model', 'probe/probe-model', '--session-dir', join(ws.base, 'sessions'), '--no-skills', '--no-prompt-templates', '--no-themes'];
  if (session) args.push('--session', session);
  if (noExtensions) args.push('--no-extensions');
  if (tools) args.push('--tools', tools);
  for (const ext of extensions) args.push('--extension', ext);
  const rpc = new PiRpc({ nodeExe: opt['fake-pi'] ? 'dotnet' : process.execPath, cliJs: runtime.cli, args, cwd: ws.project, env: { PI_CODING_AGENT_DIR: ws.agent } });
  const raw = [];
  if (opt['fake-pi']) { const write = rpc._write.bind(rpc); rpc._write = frame => write({ ...frame, ...(frame.id === undefined ? {} : { id: String(frame.id) }) }); }
  const handle = rpc._handleFrame.bind(rpc);
  rpc._handleFrame = frame => { raw.push(frame); handle(opt['fake-pi'] && frame.type === 'response' ? { ...frame, id: Number(frame.id) } : frame); };
  rpc.on('malformed', line => raw.push({ malformed: line }));
  let disposed = false;
  const close = () => {
    if (disposed) return; disposed = true;
    writeFileSync(join(ws.base, `rpc-${++sequence}.json`), JSON.stringify({ frames: raw, stderr: rpc.stderrText, modelRequests: model.requests }, null, 2));
    rpc.kill(); model.close();
  };
  try { await rpc.send('get_state', {}, 30000); }
  catch (e) { close(); throw e; }
  return { rpc, raw, model, close };
}
async function turn(client, message) {
  const offset = client.raw.length;
  const done = client.rpc.waitForEvent(e => e.type === 'agent_settled', 20000);
  // Attach rejection immediately: a failed prompt must not leave an unhandled waiter.
  done.catch(() => {});
  await client.rpc.send('prompt', { message }); await done;
  return client.raw.slice(offset);
}
function entriesFile(path) {
  return readFileSync(path, 'utf8').trim().split(/\r?\n/).map(l => JSON.parse(l)).filter(e => e.type !== 'session');
}

const mainWs = workspace('main');
let main;
await item('01', 'startup state and available models', async () => {
  main = await start(mainWs, [{ text: 'probe-lifecycle-marker' }]);
  const state = await main.rpc.send('get_state'); const models = await main.rpc.send('get_available_models');
  contract.stateContract(state, models);
  report.observations.startup = { state, models };
  return { modelCount: models.models.length, startupStderr: main.rpc.stderrText };
});
await item('03', 'prompt streaming and agent_settled lifecycle', async () => {
  assert.ok(main, 'startup required'); const events = await turn(main, 'Reply with a synthetic probe marker.');
  contract.lifecycleContract(events, opt['fake-pi'] ? '' : 'probe-lifecycle-marker');
  return { eventTypes: events.map(e => e.type) };
});
await item('02', 'get_entries full, exclusive since, invalid cursor', async () => {
  assert.ok(main); const full = await main.rpc.send('get_entries'); assert.ok(full.entries.length >= 2);
  const cursor = full.entries[0].id; const suffix = await main.rpc.send('get_entries', { since: cursor });
  contract.entriesContract(full, cursor, suffix);
  await assert.rejects(main.rpc.send('get_entries', { since: 'probe-missing-id' }), /Entry not found/i);
  report.observations.entries = full;
  return { entries: full.entries.length, suffix: suffix.entries.length, leafId: full.leafId };
});
await item('05', 'set_model success and unknown model failure', async () => {
  assert.ok(main); const model = await main.rpc.send('set_model', { provider: 'probe', modelId: 'probe-model' });
  contract.modelContract(model);
  await assert.rejects(main.rpc.send('set_model', { provider: 'probe', modelId: 'does-not-exist' }), /Model not found/i);
  return { model };
});
await item('09', 'default get_commands schema and inventory', async () => {
  assert.ok(main); const commands = await main.rpc.send('get_commands'); contract.commandsContract(commands);
  report.observations.commands = commands.commands;
  if (opt['fake-pi']) return commands;
  const ws = workspace('no-extensions'); const disabled = await start(ws, [], { noExtensions: true });
  try { const noExtensions = await disabled.rpc.send('get_commands'); contract.commandsContract(noExtensions); report.observations.noExtensionsCommands = noExtensions.commands; return { defaults: commands, noExtensions }; }
  finally { disabled.close(); }
});
if (main) main.close();

await item('04', 'steer, follow_up, clear_queue and abort', async () => {
  const ws = workspace('queue'); const client = await start(ws, [{ text: 'slow probe', delay: 5000 }]);
  try {
    await client.rpc.send('prompt', { message: 'wait' });
    await client.rpc.send('steer', { message: 'probe-steer' }); await client.rpc.send('follow_up', { message: 'probe-follow-up' });
    const queue = await client.rpc.send('clear_queue'); contract.queueContract(queue, ['probe-steer'], ['probe-follow-up']);
    contract.queueContract(await client.rpc.send('clear_queue'), [], []);
    const done = client.rpc.waitForEvent(e => e.type === 'agent_settled', 10000); done.catch(() => {});
    await client.rpc.send('abort'); await done;
    const state = await client.rpc.send('get_state'); assert.equal(state.isStreaming, false); assert.equal(state.pendingMessageCount, 0);
    return { queue, state };
  } finally { client.close(); }
});
await item('06', 'real fufucode bridge allow and deny', async () => {
  if (opt['fake-pi']) skip('FakePi cannot load JavaScript extensions; real bridge is tested only with real Pi');
  const results = [];
  for (const approved of [true, false]) {
    const ws = workspace(approved ? 'allow' : 'deny'); const path = join(ws.project, `pi-probe-${Date.now()}.txt`);
    const client = await start(ws, [{ tool: 'write', args: { path, content: 'probe-approved-content' } }, { text: 'approval probe done' }], { extensions: [join(repo, 'extensions/fufucode-bridge')], noExtensions: true });
    try {
      client.rpc.setUiPolicy(() => ({ confirmed: approved }));
      const commands = await client.rpc.send('get_commands'); assert.ok(commands.commands.some(c => c.name === 'fufucode-bridge.status' && c.source === 'extension'));
      await turn(client, 'Write the synthetic probe file.');
      const result = { approved, written: existsSync(path), requests: client.rpc.uiRequests, commands: commands.commands, noExtensions: true };
      contract.approvalContract(result); if (approved) assert.equal(readFileSync(path, 'utf8'), 'probe-approved-content'); results.push(result);
    } finally { client.close(); }
  }
  return results;
});
await item('07', 'explicit extension load fail-closed and extension_error event', async () => {
  if (opt['fake-pi']) skip('FakePi does not execute extensions');
  const ws = workspace('errors');
  const broken = join(ws.base, 'broken.ts'); writeFileSync(broken, "throw new Error('probe intentional load failure');\n");
  const event = join(ws.base, 'event.ts'); writeFileSync(event, "export default function(pi) { pi.on('agent_start', () => { throw new Error('probe intentional event failure'); }); }\n");
  // A broken explicitly supplied extension must fail closed at startup (PiStartupFailure).
  await assert.rejects(start(ws, [], { extensions: [broken] }), /pi exited \(code=1/);
  const loadEvidence = JSON.parse(readFileSync(join(ws.base, readdirSync(ws.base).find(f => /^rpc-/.test(f))), 'utf8'));
  assert.match(loadEvidence.stderr, /probe intentional load failure/);
  assert.equal(loadEvidence.frames.length, 0, 'broken explicit extension must not yield a ready RPC state');
  const client = await start(ws, [{ tool: 'write', args: { path: join(ws.project, 'error-probe.txt'), content: 'synthetic' } }, { text: 'event done' }], { extensions: [event] });
  try {
    await client.rpc.send('get_commands');
    await turn(client, 'Run the synthetic write tool.');
    const errors = client.raw.filter(e => e.type === 'extension_error'); contract.diagnosticsContract(errors, 'probe intentional event failure');
    report.observations.extensionErrors = errors; return { loadError: loadEvidence.stderr, errors };
  } finally { client.close(); }
});
await item('08', 'named inline or builtin source identity', async () => {
  if (opt['fake-pi']) skip('FakePi has no resource loader');
  if (!['0.87.1', '0.99.2', '1.0.0', '1.1.0'].includes(target.version)) skip(`No reviewed builtin naming oracle for ${target.version}; raw SDK evidence requires review before acceptance`);
  const ws = workspace('sources');
  const run = spawnSync(process.execPath, [join(here, 'source-probe.mjs'), target.pkg, ws.project, ws.agent], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
  writeFileSync(join(ws.base, 'source-loader.json'), JSON.stringify({ status: run.status, stdout: run.stdout, stderr: run.stderr }, null, 2));
  assert.equal(run.status, 0, run.stderr);
  const data = JSON.parse(run.stdout.trim()); const named = data.extensions.find(e => e.commands.includes('probe-source'));
  assert.ok(named, 'named factory command loaded'); contract.sourceContract(named.path, '<inline:probe-named>');
  assert.equal(named.sourceInfo.path, named.path);
  assert.deepEqual(data.factories.map(f => f.name), target.version === '0.87.1' ? ['llama.cpp'] : ['llama.cpp', 'codemode', 'tool-search', 'mcp']);
  // Pi 1.0.0 and 1.1.0 dist/extensions/index.js: same four factories and flags.
  // Assert flags as well as names: a factory silently becoming ordinary inline is a failure.
  if (['1.0.0', '1.1.0'].includes(target.version)) assert.deepEqual(data.factories, [
    { name: 'llama.cpp', builtin: true },
    { name: 'codemode', builtin: true, replaceable: true },
    { name: 'tool-search', builtin: true, replaceable: true },
    { name: 'mcp', builtin: true, replaceable: true },
  ]);
  for (const factory of data.factories) {
    const expected = factory.builtin ? `builtin:${factory.name}` : `<inline:${factory.name}>`;
    const loaded = data.extensions.find(e => e.path === expected); assert.ok(loaded, `factory ${factory.name} has correct loaded source ${expected}`);
    // 0.87.1 hidden factory intentionally has no public sourceInfo.
    if (!factory.hidden) assert.equal(loaded.sourceInfo.path, expected);
  }
  const synthetic = data.extensions.find(e => e.commands.includes('probe-builtin-source'));
  assert.equal(synthetic.path, target.version === '0.87.1' ? '<inline:probe-builtin>' : 'builtin:probe-builtin');
  assert.ok(data.errors.some(e => e.path === '<inline:probe-broken-inline>' && e.error.includes('probe broken inline')));
  assert.ok(data.errors.some(e => e.path === (target.version === '0.87.1' ? '<inline:probe-broken-builtin>' : 'builtin:probe-broken-builtin') && e.error.includes('probe broken builtin')));
  report.observations.namedSource = named; report.observations.loaderExtensions = data.extensions;
  return data;
});
await item('10', 'fixture JSONL bidirectional cross-version read and append', async () => {
  if (opt['fake-pi']) skip('FakePi session emulation is not a cross-version runtime');
  const fixture = join(here, 'fixtures/pi-0.87.1.jsonl');
  if (opt['generate-fixture']) {
    assert.equal(target.version, '0.87.1', 'checked-in fixture must be generated by locked 0.87.1');
    assert.ok(main);
    // main is closed by now; locate its actual persisted JSONL via recorded state.
    const path = report.observations.startup.state.sessionFile;
    assert.ok(path && existsSync(path), 'baseline session was persisted');
    mkdirSync(dirname(fixture), { recursive: true }); copyFileSync(path, fixture);
    writeFileSync(join(here, 'fixtures/provenance.json'), JSON.stringify({ version: target.version, generatedUtc: new Date().toISOString(), source: 'real Pi RPC with isolated local synthetic model', report: output }, null, 2));
  }
  assert.ok(existsSync(fixture), 'run baseline with --generate-fixture first');
  if (!opt['compare-pi-root']) skip('Pass --compare-pi-root to verify the reverse consumer; fixture alone does not prove two-way compatibility');
  const compare = locate(opt['compare-pi-root']); assert.notEqual(compare.version, target.version, 'two distinct runtime versions required');
  const results = [];
  for (const [producer, consumer] of [[compare, target], [target, compare]]) {
    const ws = workspace(`compat-${producer.version}-to-${consumer.version}`);
    const file = join(ws.base, 'compat.jsonl'); copyFileSync(fixture, file);
    // The checked-in fixture uses a portable placeholder cwd. Pi 1.1.0 restores
    // the header cwd and rejects missing directories. Rebase only the copied
    // header to the isolated project; all persisted entries remain unchanged.
    const fixtureLines = readFileSync(file, 'utf8').split('\n');
    const header = JSON.parse(fixtureLines[0]);
    header.cwd = ws.project;
    fixtureLines[0] = JSON.stringify(header);
    writeFileSync(file, fixtureLines.join('\n'));
    const writer = await start(ws, [{ text: `producer-${producer.version}` }], { session: file, runtime: producer });
    try { await turn(writer, `Append producer ${producer.version}.`); } finally { writer.close(); }
    const before = entriesFile(file);
    const readerWs = workspace(`read-${consumer.version}`);
    const reader = await start(readerWs, [{ text: `consumer-${consumer.version}` }], { session: file, runtime: consumer });
    try {
      const loaded = await reader.rpc.send('get_entries');
      assert.deepEqual(loaded.entries, before, 'RPC loads every existing entry without dropping or rewriting');
      await turn(reader, `Append consumer ${consumer.version}.`);
      const after = entriesFile(file); contract.sessionContract(before, after, `consumer-${consumer.version}`);
      results.push({ producer: producer.version, consumer: consumer.version, beforeEntries: before.length, afterEntries: after.length });
    } finally { reader.close(); }
  }
  return results;
});

if (opt.features) await item('11', 'opt-in builtins: codemode, local MCP and nested approval', async () => {
  const results = [];
  for (const approved of [true, false]) {
    const ws = workspace(approved ? 'builtins-allow' : 'builtins-deny');
    const path = join(ws.project, 'nested-write.txt');
    const calls = join(ws.base, 'mcp-calls.jsonl');
    const server = join(ws.base, 'mcp-server.mjs');
    writeFileSync(server, `import { createInterface } from 'node:readline';
import { appendFileSync } from 'node:fs';
createInterface({ input: process.stdin }).on('line', line => {
  const req = JSON.parse(line);
  if (req.id === undefined) return;
  let result = {};
  if (req.method === 'initialize') result = { protocolVersion: req.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'probe', version: '1.0' } };
  if (req.method === 'tools/list') result = { tools: [{ name: 'echo', description: 'Local probe echo', inputSchema: { type: 'object', properties: { value: { type: 'string' } }, required: ['value'] } }] };
  if (req.method === 'tools/call') {
    appendFileSync(${JSON.stringify(calls)}, JSON.stringify(req.params) + '\\n');
    result = { content: [{ type: 'text', text: req.params.arguments.value }] };
  }
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: req.id, result }) + '\\n');
});
`);
    writeFileSync(join(ws.agent, 'mcp.json'), JSON.stringify({ mcpServers: { probe: { command: process.execPath, args: [server] } } }));
    const code = `const results = await Promise.allSettled([
      tools.write({ path: ${JSON.stringify(path)}, content: 'nested-approved' }),
      tools.mcp__probe__echo({ value: 'local-mcp-marker' })
    ]); text(results); store('probe', 'stored-marker');`;
    const client = await start(ws, [{ tool: 'codemode', args: { code } }, { text: 'builtins done' }], {
      noExtensions: true, tools: '+codemode',
      extensions: [join(repo, 'extensions/fufucode-bridge'), 'builtin:codemode', 'builtin:tool-search', 'builtin:mcp'],
    });
    try {
      client.rpc.setUiPolicy(req => ({ confirmed: !req.title?.includes("'write'") || approved }));
      const commands = await client.rpc.send('get_commands');
      assert.ok(commands.commands.some(c => c.name === 'mcp'));
      await turn(client, 'Run the isolated builtin test.');
      assert.ok(client.model.requests[0].tools.some(t => t.function?.name === 'codemode'));
      assert.equal(existsSync(path), approved, 'nested write follows approval');
      assert.ok(client.rpc.uiRequests.some(r => r.title?.includes("'write'")), 'nested write reaches bridge');
      assert.ok(existsSync(calls), 'MCP call reached the local stdio server');
      assert.match(readFileSync(calls, 'utf8'), /local-mcp-marker/);
      const toolResults = client.raw.filter(e => e.type === 'tool_execution_end');
      assert.ok(toolResults.some(e => e.toolName === 'codemode' && !e.isError), 'QuickJS execution succeeds');
      assert.match(JSON.stringify(toolResults), /local-mcp-marker/);
      const entries = await client.rpc.send('get_entries');
      assert.ok(entries.entries.some(e => e.customType === 'codemode-store'), 'codemode store persisted by Pi');
      results.push({ approved, written: existsSync(path), dialogs: client.rpc.uiRequests, toolResults });
    } finally { client.close(); }
  }
  return results;
});

if (opt['baseline-report']) {
  const old = JSON.parse(readFileSync(resolve(opt['baseline-report']), 'utf8'));
  const inventory = list => (list || []).map(c => ({ name: c.name, source: c.source, sourceInfo: c.sourceInfo }));
  const previous = inventory(old.observations.commands); const current = inventory(report.observations.commands);
  report.diff = { baselineVersion: old.version, version: report.version,
    statusChanges: report.items.map(i => ({ id: i.id, baseline: old.items.find(o => o.id === i.id)?.status ?? null, current: i.status })).filter(i => i.baseline !== i.current),
    namedSource: { before: old.observations.namedSource, after: report.observations.namedSource },
    loaderExtensions: { before: old.observations.loaderExtensions, after: report.observations.loaderExtensions },
    noExtensionsCommands: { before: inventory(old.observations.noExtensionsCommands), after: inventory(report.observations.noExtensionsCommands) },
    addedCommands: current.filter(c => !previous.some(p => p.name === c.name)), removedCommands: previous.filter(c => !current.some(p => p.name === c.name)),
    changedSources: current.filter(c => { const p = previous.find(p => p.name === c.name); return p && JSON.stringify(p.sourceInfo) !== JSON.stringify(c.sourceInfo); }) };
  writeFileSync(join(output, 'probe-diff.json'), JSON.stringify(report.diff, null, 2));
}
if (opt['self-test']) {
  const tests = await import('./self-test.mjs'); report.selfTest = await tests.runSelfTest({ output: join(output, 'self-test'), fakePi: opt['fake-pi'] });
}
report.finishedUtc = new Date().toISOString();
report.summary = Object.fromEntries(['PASS', 'FAIL', 'SKIP'].map(status => [status, report.items.filter(i => i.status === status).length]));
writeFileSync(join(output, 'probe-report.json'), JSON.stringify(report, null, 2));
writeFileSync(join(output, 'probe-report.md'), `# Pi ${report.version} probe\n\nUTC: ${report.startedUtc}\n\n${report.items.map(i => `- ${i.status} ${i.id} ${i.name}${i.error ? `: ${i.error}` : ''}`).join('\n')}\n\n${report.selfTest ? `Self-test: ${report.selfTest.passed} PASS / ${report.selfTest.failed} FAIL\n\n` : 'Self-test: NOT RUN\n\n'}Raw evidence: ${output}\n`);
console.log(JSON.stringify({ report: join(output, 'probe-report.json'), summary: report.summary, selfTest: report.selfTest ? { passed: report.selfTest.passed, failed: report.selfTest.failed } : 'NOT RUN' }));
process.exitCode = report.summary.FAIL || report.selfTest?.failed ? 1 : report.summary.SKIP ? 2 : 0;
