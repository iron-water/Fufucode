import { SessionManager } from '../../src/Fufucode.Plugins.Pi/runtime/node_modules/@earendil-works/pi-coding-agent/dist/core/session-manager.js';

const [project, directory, id] = process.argv.slice(2);
const session = SessionManager.create(project, directory, { id });
session.appendMessage({ role: 'user', content: [{ type: 'text', text: 'Persisted fork seed' }], timestamp: Date.now() });
session.appendMessage({
  role: 'assistant', content: [{ type: 'text', text: 'Persisted fixture reply' }],
  api: 'openai-completions', provider: 'fixture', model: 'fixture',
  usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0, totalTokens: 2,
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
  stopReason: 'stop', timestamp: Date.now()
});
console.log(JSON.stringify({ id: session.getSessionId(), file: session.getSessionFile() }));
