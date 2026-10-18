// Pi backend plugin diagnostic — not part of the default native package.
import assert from 'node:assert/strict';

export function stateContract(state, models) {
  assert.equal(typeof state.isStreaming, 'boolean');
  assert.equal(typeof state.sessionId, 'string');
  assert.ok(Array.isArray(models.models) && models.models.length > 0, 'available models must be nonempty');
}
export function entriesContract(full, cursor, suffix) {
  assert.ok(Array.isArray(full.entries));
  const index = full.entries.findIndex(e => e.id === cursor);
  assert.ok(index >= 0, 'cursor must be a real entry');
  assert.deepEqual(suffix.entries, full.entries.slice(index + 1), 'since excludes the cursor');
  assert.equal(suffix.leafId, full.leafId);
}
export function lifecycleContract(events, marker) {
  const types = events.map(e => e.type);
  const start = types.indexOf('agent_start');
  const end = types.lastIndexOf('agent_end');
  const settled = types.lastIndexOf('agent_settled');
  assert.ok(start >= 0 && end > start && settled > end, 'start < end < settled');
  assert.ok(events.some(e => e.type === 'message_update' && e.assistantMessageEvent?.type === 'text_delta'), 'ordinary text streaming delta');
  assert.ok(JSON.stringify(events).includes(marker), 'synthetic model reply present');
}
export function queueContract(queue, steering, followUp) {
  assert.ok(Array.isArray(queue.steering) && Array.isArray(queue.followUp));
  assert.deepEqual(queue.steering, steering);
  assert.deepEqual(queue.followUp, followUp);
}
export function commandsContract(commands) {
  assert.ok(Array.isArray(commands.commands));
  for (const c of commands.commands) {
    assert.equal(typeof c.name, 'string');
    assert.ok(['extension', 'prompt', 'skill'].includes(c.source));
    assert.equal(typeof c.sourceInfo?.path, 'string');
  }
}
export function approvalContract({ approved, written, requests }) {
  assert.ok(requests.some(r => r.method === 'confirm'), 'real confirmation dialog');
  assert.equal(written, approved, 'filesystem effect matches confirmation');
}
export function sessionContract(before, after, marker) {
  assert.ok(before.length > 0);
  for (const entry of before) assert.deepEqual(after.find(e => e.id === entry.id), entry, `preserve original entry ${entry.id}`);
  assert.ok(after.length > before.length, 'consumer appends new entries');
  assert.ok(JSON.stringify(after).includes(marker), 'consumer reply persisted');
}
export function modelContract(model) {
  assert.equal(model.provider, 'probe'); assert.equal(model.id, 'probe-model');
}
export function diagnosticsContract(errors, marker) {
  assert.ok(errors.some(e => e.type === 'extension_error' && e.error.includes(marker)));
  assert.ok(errors.every(e => typeof e.extensionPath === 'string' && typeof e.event === 'string'));
}
export function sourceContract(path, expected) { assert.equal(path, expected); }
