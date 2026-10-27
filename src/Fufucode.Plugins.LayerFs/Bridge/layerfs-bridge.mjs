import { readFileSync, mkdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline';

// The bridge runs inside WSL on its Linux filesystem. Only the pinned public
// SDK/runtime APIs cross this boundary; requests are never replayed.
const config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const request = JSON.parse(await new Promise((resolve, reject) => {
  const input = createInterface({ input: process.stdin, terminal: false });
  input.once('line', line => {
    input.removeAllListeners('close');
    resolve(line);
    input.close();
    process.stdin.pause();
  });
  input.once('close', () => reject(new Error('Missing request')));
}));
let runtime;
let client;
try {
  mkdirSync(config.home, { recursive: true, mode: 0o700 });
  process.env.LAYERFS_NATIVE = config.native;
  const { LayerFSRuntime } = await import(pathToFileURL(`${config.source}/api/runtime/src/index.js`));
  const { LayerFS } = await import(pathToFileURL(`${config.source}/api/agent/sdk/src/index.js`));
  runtime = await LayerFSRuntime.open({ home: config.home, daemon: config.daemon, image: config.image,
    docker: config.docker ?? '/var/run/docker.sock', volumes: config.volumes ?? '/var/lib/docker/volumes',
    containerPrefix: 'fufucode-layerfs-', sweep: false });
  let result;
  if (request.action === 'status') {
    result = await runtime.status();
    if (result.docker.level === 'ok') {
      const api = execFileSync('docker', ['--host', `unix://${config.docker ?? '/var/run/docker.sock'}`,
        'version', '--format', '{{.Server.APIVersion}}'], { encoding: 'utf8', timeout: 10000 }).trim();
      const [major, minor] = api.split('.').map(Number);
      result.docker.apiVersion = api;
      if (!(major > 1 || (major === 1 && minor >= 54))) {
        result.docker.level = 'incompatible';
        result.docker.error = `LayerFS v1.1.0 requires Docker API >=1.54; found ${api}`;
      }
    }
  }
  else if (request.action === 'projects') result = await runtime.project.list();
  else if (request.action === 'init') {
    const job = await runtime.project.start_init(request.project, request.source, { timeout: 300000 });
    for (;;) {
      const state = await runtime.project.get_init(job.id);
      if (state.ended) {
        if (state.error) throw Object.assign(new Error(state.error.message), { code: state.error.code });
        result = state.project;
        break;
      }
      await new Promise(resolve => setTimeout(resolve, 200));
    }
  } else if (request.action === 'state') result = await runtime.project.get_state(request.project);
  else {
    const record = (await runtime.project.list()).find(item => item.name === request.project);
    if (!record) throw new Error(`Project not found: ${request.project}`);
    for (const [key, value] of Object.entries({ HOME: config.home, DAEMON: config.daemon,
      IMAGE: record.image ?? config.image, VOLUME: record.volume,
      STORE_PATH: `${config.volumes ?? '/var/lib/docker/volumes'}/${record.volume}/_data/global/store.sqlite`,
      DOCKER: config.docker ?? '/var/run/docker.sock', CONTAINER_PREFIX: 'fufucode-layerfs-' }))
      process.env[`LAYERFS_${key}`] = value;
    client = await LayerFS.open(request.project);
    const ws = { sandbox: request.sandbox, path: request.path };
    switch (request.action) {
      case 'fork': result = await client.branch.fork(request.branch, request.from ?? 'main'); break;
      case 'commits': result = await client.branch.list_commits(request.branch, 50, 0); break;
      case 'sandboxes': result = await client.project.list_sandboxes(50, 0); break;
      case 'createSandbox': result = await client.sandbox.create(); break;
      case 'closeSandbox': result = await client.sandbox.close(request.sandbox); break;
      case 'mount': result = await client.workspace.mount(request.sandbox, request.branch); break;
      case 'exec': result = await client.workspace.exec(ws, request.command, 'Fufucode 命令'); break;
      case 'commit': result = await client.workspace.commit(ws, request.message); break;
      case 'unmount': result = await client.workspace.unmount(ws); break;
      case 'read': result = await client.fs.read(request.branch, request.path, 1, 100); break;
      default: throw new Error(`Unsupported action: ${request.action}`);
    }
  }
  process.stdout.write(JSON.stringify({ ok: true, result }) + '\n');
} catch (error) {
  process.stdout.write(JSON.stringify({ ok: false, code: error.code ?? 'Failed', message: error.message, detail: error.detail }) + '\n');
  process.exitCode = 1;
} finally {
  await client?.close();
  await runtime?.close();
}
