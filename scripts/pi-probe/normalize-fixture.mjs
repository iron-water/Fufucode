import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';

// Remove captured machine context, not protocol identities or conversation entries.
for (const name of ['pi-0.87.1.jsonl', 'pi-1.0.0.jsonl']) {
const file = new URL(`./fixtures/${name}`, import.meta.url);
const before = readFileSync(file, 'utf8').trim().split(/\r?\n/).map(JSON.parse);
const after = structuredClone(before);
for (const entry of after) {
  if (entry.type === 'session') entry.cwd = 'C:/fixture/project';
  if (entry.message?.role === 'system' && entry.message.sections) {
    entry.message.sections.docs = '<project_context>\nIsolated synthetic fixture project.\n</project_context>';
    entry.message.sections.project_context = '<project_context>\nIsolated synthetic fixture project.\n</project_context>';
    entry.message.sections.cwd = '<cwd>\nC:/fixture/project\n</cwd>';
  }
}
assert.deepEqual(after.map(e => [e.type, e.id, e.parentId]), before.map(e => [e.type, e.id, e.parentId]));
assert.deepEqual(after.filter(e => e.message?.role !== 'system' && e.type !== 'session'),
  before.filter(e => e.message?.role !== 'system' && e.type !== 'session'));
const serialized = after.map(e => JSON.stringify(e)).join('\n') + '\n';
assert.ok(!/[A-Za-z]:[\\/]{1,2}(Users|home)[\\/]/.test(serialized) && !serialized.includes('.codex'));
writeFileSync(file, serialized);
console.log('PASS: portable fixture context; entry identities and non-system messages unchanged');
}
