import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
const root = resolve('tmp/d1-ui');
const project = join(root, 'project');
const data = join(root, 'data');
mkdirSync(project, { recursive: true }); mkdirSync(data, { recursive: true });
const server = createServer((req, res) => {
  let body = ''; req.on('data', chunk => body += chunk);
  req.on('end', () => {
    const request = JSON.parse(body);
    const last = request.messages.at(-1);
    const tool = last.role !== 'tool' && JSON.stringify(last).includes('WRITE');
    const delta = tool ? { role: 'assistant', tool_calls: [{ index: 0, id: 'd1_ui_' + Date.now(), type: 'function', function: { name: 'write', arguments: JSON.stringify({ path: join(project, 'ui-approved.txt'), content: 'D1 native approved' }) } }] } : { role: 'assistant', content: 'D1 UI fixture complete' };
    const frame = (delta, finish_reason) => ({ id: 'd1-ui', object: 'chat.completion.chunk', created: 1, model: 'd1-fixture', choices: [{ index: 0, delta, finish_reason }], usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 } });
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.end(`data: ${JSON.stringify(frame(delta, null))}\n\ndata: ${JSON.stringify(frame({}, tool ? 'tool_calls' : 'stop'))}\n\ndata: [DONE]\n\n`);
    console.log('FIXTURE response ' + (tool ? 'write' : 'text'));
  });
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
writeFileSync(join(data, 'settings.json'), JSON.stringify({ version: 1, selectedModel: 'd1/d1-fixture', endpoint: { providerName: 'd1', baseUrl: 'http://127.0.0.1:' + server.address().port, modelId: 'd1-fixture', apiKeyEnvironmentVariable: 'D1_FIXTURE_KEY' } }));
const app = spawn(resolve('src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.exe'), ['--project', project, '--data-root', data,
  '--pi-node', process.env.D1_PI_NODE ?? resolve('runtime/node/node.exe'),
  '--pi-cli', process.env.D1_PI_CLI ?? resolve('runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js'),
  '--omp-exe', process.env.D1_OMP_EXE ?? resolve('runtime/omp/omp.exe')], { windowsHide: true, env: { ...process.env, D1_FIXTURE_KEY: 'local-fixture-only' } });
console.log('UI PID ' + app.pid + ' DATA ' + data);
app.stderr.on('data', () => {});
app.on('exit', code => { console.log('UI EXIT ' + code); server.close(); });
