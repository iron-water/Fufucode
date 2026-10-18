import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { startStubModel } from './bridge-verify/lib/stub-model.mjs';

const server = await startStubModel({ port: 0, script: Array.from({ length: 4 }, () => ({ type: 'text', text: 'WORKBENCH_REAL_PI_OK' })) });
try {
  const child = spawn('dotnet', [resolve(process.argv[2] ?? 'src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll'), '--selftest', '--workbench-only'], {
    windowsHide: true, stdio: 'inherit', env: { ...process.env,
      FUFUCODE_WORKBENCH_TEST_URL: `http://127.0.0.1:${server.port}/v1`, FUFUCODE_WORKBENCH_TEST_KEY: 'local-fixture-only' }
  });
  const timer = setTimeout(() => child.kill(), 150000);
  const code = await new Promise((resolve, reject) => {
    child.once('error', reject); child.once('close', resolve);
  });
  clearTimeout(timer);
  if (code !== 0) throw new Error(`Workbench verification exited ${code}`);
  if (!JSON.stringify(server.requests).includes('workbench reference marker')) throw new Error('Reference did not reach model request');
  if (!JSON.stringify(server.requests).includes('Side chat acceptance marker')) throw new Error('Side chat did not reach model request');
  console.log(`PASS: real Pi, local scripted model, reference reached HTTP request; requests=${server.requests.length}`);
} finally { server.close(); }
