import assert from 'node:assert/strict';
import { existsSync, mkdirSync, copyFileSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PiRpc } from '../bridge-verify/lib/pi-rpc.mjs';
import * as c from './assertions.mjs';
const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '../..');

export async function runSelfTest({ output = join(repo, 'tmp/pi-probe-self-test'), fakePi } = {}) {
  output = resolve(output);
  mkdirSync(output, { recursive: true });
  const cases = []; let rpc; const raw = [];
  const check = async (id, name, fn) => {
    try { await fn(); cases.push({ id, name, status: 'PASS' }); }
    catch (error) { cases.push({ id, name, status: 'FAIL', error: error.message }); }
  };
  const dll = resolve(fakePi || join(repo, 'tests/Fufucode.FakePi/bin/Debug/net10.0/Fufucode.FakePi.dll'));
  await check('F0', 'actual FakePi transport startup', async () => {
    assert.ok(existsSync(dll), `Build FakePi first: ${dll}`);
    const session = join(output, 'fake-session.jsonl'); copyFileSync(join(here, 'fixtures/pi-0.87.1.jsonl'), session);
    rpc = new PiRpc({ nodeExe: 'dotnet', cliJs: dll, args: ['--session-dir', output, '--session', session], cwd: output });
    // FakePi's .NET fixture ID contract is a string. PiRpc helper uses numbers.
    const write = rpc._write.bind(rpc); rpc._write = frame => write({ ...frame, ...(frame.id === undefined ? {} : { id: String(frame.id) }) });
    const handle = rpc._handleFrame.bind(rpc); rpc._handleFrame = frame => { raw.push(frame); handle(frame.type === 'response' ? { ...frame, id: Number(frame.id) } : frame); };
    const state = await rpc.send('get_state'); assert.equal(state.isStreaming, false); assert.equal(typeof state.sessionId, 'string');
  });
  await check('F2', 'FakePi real fixture full and since satisfy same entries assertion', async () => {
    assert.ok(rpc); const full = await rpc.send('get_entries'); const cursor = full.entries[0].id;
    c.entriesContract(full, cursor, await rpc.send('get_entries', { since: cursor }));
  });
  await check('F3', 'FakePi actual stream satisfies lifecycle assertion', async () => {
    assert.ok(rpc); const offset = raw.length; const settled = rpc.waitForEvent(e => e.type === 'agent_settled', 5000); settled.catch(() => {});
    await rpc.send('prompt', { message: 'stream' }); await settled; c.lifecycleContract(raw.slice(offset), 'Final answer');
  });
  await check('F4', 'FakePi actual clear_queue uses same queue assertion', async () => { assert.ok(rpc); c.queueContract(await rpc.send('clear_queue'), [], []); });
  const validEvents = [{ type: 'agent_start' }, { type: 'message_update', assistantMessageEvent: { type: 'text_delta', delta: 'marker' } }, { type: 'agent_end' }, { type: 'agent_settled' }];
  const entries = { entries: [{ id: 'a' }, { id: 'b' }], leafId: 'b' };
  const mutations = [
    ['01', () => c.stateContract({ isStreaming: false, sessionId: 'x' }, { models: [] })],
    ['02', () => c.entriesContract(entries, 'a', { entries: entries.entries, leafId: 'b' })],
    ['03', () => c.lifecycleContract(validEvents.filter(e => e.type !== 'agent_settled'), 'marker')],
    ['04', () => c.queueContract({ steering: [], followUp: ['queued'] }, [], [])],
    ['05', () => c.modelContract({ provider: 'probe', id: 'wrong' })],
    ['06', () => c.approvalContract({ approved: false, written: true, requests: [{ method: 'confirm' }] })],
    ['07', () => c.diagnosticsContract([{ type: 'other', error: 'marker' }], 'marker')],
    ['08', () => c.sourceContract('builtin:probe-named', '<inline:probe-named>')],
    ['09', () => c.commandsContract({ commands: [{ name: 'unsafe', source: 'extension' }] })],
    ['10', () => c.sessionContract([{ id: 'a', payload: 'preserve' }], [{ id: 'b', payload: 'marker' }], 'marker')],
  ];
  for (const [id, mutate] of mutations) await check(`N${id}`, `shared assertion ${id} rejects negative mutation`, () => assert.throws(mutate, { name: 'AssertionError' }));
  if (rpc) { writeFileSync(join(output, 'fake-rpc.json'), JSON.stringify({ frames: raw, stderr: rpc.stderrText }, null, 2)); rpc.kill(); }
  const result = { scope: 'Actual FakePi: transport, session cursor, streaming lifecycle, empty queue. All ten groups: negative mutations against shared assertions. SDK/bridge/model inventory/cross-version claims require real Pi.', cases, passed: cases.filter(i => i.status === 'PASS').length, failed: cases.filter(i => i.status === 'FAIL').length };
  writeFileSync(join(output, 'self-test-report.json'), JSON.stringify(result, null, 2));
  for (const test of cases) console.log(`${test.status} self-test ${test.id} ${test.name}${test.error ? ': ' + test.error : ''}`);
  return result;
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = await runSelfTest({ output: process.argv[2], fakePi: process.argv[3] }); process.exitCode = result.failed ? 1 : 0;
}
