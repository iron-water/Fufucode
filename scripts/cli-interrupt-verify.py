"""Windows real CTRL_C_EVENT acceptance against an existing CLI/Pi binary."""
import ctypes
import json
from pathlib import Path
import subprocess
import threading
import time

root = Path(__file__).resolve().parent.parent
stage = root / 'tmp' / f'cli-interrupt-{int(time.time() * 1000)}'
project = stage / 'project'
project.mkdir(parents=True)
cli = root / 'src/Fufucode.Cli/bin/Debug/net10.0/Fufucode.Cli.exe'
base = [str(cli), '--project', str(project), '--app-data', str(stage / 'data'), '--json']
fixture = root / 'tests/fixtures/approval-ui-fixture.ts'
setup = subprocess.run(base + ['extensions', 'enable', str(fixture)], capture_output=True, text=True,
                       creationflags=subprocess.CREATE_NO_WINDOW, timeout=30)
assert setup.returncode == 0, setup.stderr
startup = subprocess.STARTUPINFO()
startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
startup.wShowWindow = 0
args = base + ['chat', '/fufucode-slow-approval-fixture']
child = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                         text=True, startupinfo=startup, creationflags=subprocess.CREATE_NEW_CONSOLE)
ready = threading.Event()
output = []
def read_output():
    for line in child.stdout:
        output.append(line)
        if 'approval_request' in line:
            ready.set()
thread = threading.Thread(target=read_output)
thread.start()
try:
    assert ready.wait(20), 'Real Pi approval was not reached'
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.FreeConsole()
    assert kernel.AttachConsole(child.pid), f'AttachConsole failed {ctypes.get_last_error()}'
    assert kernel.SetConsoleCtrlHandler(None, True), 'Sender control handler failed'
    assert kernel.GenerateConsoleCtrlEvent(0, 0), f'CTRL_C_EVENT failed {ctypes.get_last_error()}'
    code = child.wait(timeout=15)
    thread.join(timeout=3)
    stderr = child.stderr.read()
    record = {'command': args, 'signal': 'Windows CTRL_C_EVENT', 'exitCode': code,
              'stdout': ''.join(output).strip(), 'stderr': stderr.strip()}
    (stage / 'report.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
    print(json.dumps(record))
    assert code == 130, f'Expected cancellation exit 130, got {code}'
finally:
    if child.poll() is None:
        child.kill()
    kernel.FreeConsole() if 'kernel' in locals() else None
