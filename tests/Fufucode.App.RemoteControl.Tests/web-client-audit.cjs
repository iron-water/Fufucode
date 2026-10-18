// Run the production embedded script, with deterministic DOM/network stand-ins.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.resolve(__dirname, '../../src/Fufucode.Plugins.RemoteControl/RemoteControlWebAssets.cs'), 'utf8');
const script = source.split('private const string AppJs = """')[1].split('""";')[0];
const nodes = new Map();
function node() {
  const n = { value: '', style: {}, dataset: {}, disabled: false, listeners: {},
    classList: { toggle() {}, add() {}, remove() {} }, appendChild() {}, setAttribute() {},
    querySelectorAll() { return []; }, addEventListener(event, fn) { this.listeners[event] = fn; },
    click() { return this.listeners.click?.(); } };
  return n;
}
const get = id => { if (!nodes.has(id)) nodes.set(id, node()); return nodes.get(id); };
let socket, pending, mode = 'reject';
class Socket {
  constructor() { socket = this; }
  close() { this.onclose?.(); }
}
const context = {
  document: { getElementById: get, createElement: node, querySelectorAll: () => [], body: node() },
  sessionStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
  location: { protocol: 'http:', host: 'localhost:1234' }, WebSocket: Socket,
  setTimeout, clearTimeout, console,
  window: { confirm: () => true },
  fetch(url) {
    if (url === '/api/pair') return Promise.resolve({ ok: true, json: async () => ({ success: true, token: 'test' }) });
    if (url === '/api/stop') return Promise.resolve({ ok: false, json: async () => ({ accepted: false, message: 'stop denied' }) });
    if (mode === 'hold') return new Promise(resolve => { pending = resolve; });
    return Promise.resolve({ ok: true, json: async () => ({ accepted: false, message: 'not accepted' }) });
  }
};
const flush = async () => { for (let i = 0; i < 15; i++) await Promise.resolve(); };
(async () => {
  vm.runInNewContext(script, context);
  get('pin').value = '1234';
  get('btnPair').click();
  await flush();
  socket.onopen();
  socket.onmessage({ data: JSON.stringify({ type: 'snapshot', session: { title: 'test', entries: [], pendingApprovals: [], canSend: true, canStop: true } }) });
  get('input').value = 'draft';
  get('btnSend').click();
  await flush();
  assert.equal(get('input').value, 'draft', 'rejected request preserves draft');
  mode = 'hold';
  get('btnSend').click();
  get('input').value = 'new draft';
  pending({ ok: true, json: async () => ({ accepted: true }) });
  await flush();
  assert.equal(get('input').value, 'new draft', 'late acceptance preserves edited draft');
  get('btnStop').click();
  await flush();
  assert.match(get('devQueue').textContent, /stop denied/, 'rejected stop is not reported successful');
  get('btnDisconnect').click();
  console.log('PASS embedded web: rejection preserves draft; stale acceptance preserves edits; stop rejection is visible');
})().catch(error => { console.error(error); process.exitCode = 1; });
