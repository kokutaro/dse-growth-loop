"""Signal regressions use local OS process handles, never Azure or an AI transport."""
import contextlib
import io
import json
import os
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import issue30_safety_supervisor as safety


class SignalDeadlineTests(unittest.TestCase):
    def test_pidfd_wait_distinguishes_signal_wakeup_from_parent_exit(self):
        for ready, exited in (([123], False), ([456], True), ([123, 456], True)):
            with self.subTest(ready=ready):
                watcher = safety.ParentWatch.__new__(safety.ParentWatch)
                watcher.handle, watcher.wake_fd = 456, 123
                watcher.kind, watcher.exited = 'pidfd', False
                with patch.object(safety.select, 'select', return_value=(ready, [], [])) as select_call, \
                        patch.object(safety.os, 'read', return_value=b'x') as drain:
                    self.assertEqual(watcher.wait(4), exited)
                select_call.assert_called_once_with([456, 123], [], [], 4)
                self.assertEqual(drain.call_count, int(123 in ready))

    def test_wakeup_setup_failure_still_executes_emergency_stop(self):
        args = ['supervisor', '--execute-after-approval', '--parent-pid', '99999', '--run-seconds', '4']
        previous = {s: signal.getsignal(s) for s in (signal.SIGTERM, signal.SIGINT)}
        with patch.object(sys, 'argv', args), patch.object(safety.os, 'pipe', side_effect=OSError('private-detail')), \
                patch.object(safety, 'stop_apps', return_value={'stopConfirmed': False}) as stop, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(safety.main(), 2)
        stop.assert_called_once()
        self.assertEqual(json.loads(output.getvalue())['trigger'], 'SIGNAL_WAKEUP_UNAVAILABLE')
        self.assertNotIn('private-detail', output.getvalue())
        self.assertEqual(previous, {s: signal.getsignal(s) for s in previous})

    def test_first_signal_timestamp_anchors_deadline_despite_delayed_wait_return(self):
        now = [10.0]
        def delayed_wait(_):
            signal.getsignal(signal.SIGTERM)(signal.SIGTERM, None)
            now[0] = 11.0
            signal.getsignal(signal.SIGINT)(signal.SIGINT, None)
            now[0] = 12.0
            return False
        args = ['supervisor', '--execute-after-approval', '--parent-pid', '99999',
                '--run-seconds', '4', '--poll-seconds', '4', '--stop-seconds', '.3']
        with patch.object(sys, 'argv', args), patch.object(safety, 'ParentWatch') as watch, \
                patch.object(safety.time, 'monotonic', side_effect=lambda: now[0]), \
                patch.object(safety, 'stop_apps', return_value={'stopConfirmed': False}) as stop, \
                contextlib.redirect_stdout(io.StringIO()):
            watch.return_value.wait.side_effect = delayed_wait
            self.assertEqual(safety.main(), 2)
        self.assertAlmostEqual(stop.call_args.args[2].end, 10.3)

    def test_long_parent_wait_is_woken_by_sigterm_and_sigint(self):
        child_code = r'''
import json,sys,time
sys.path.insert(0,sys.argv[1]+'/scripts')
import issue30_safety_supervisor as safety
original=safety.ParentWatch.wait
def wait(self,timeout):
 print(json.dumps({'event':'WAIT_ENTERED'}),flush=True)
 return original(self,timeout)
def stop(cli,apps,deadline,poll):
 print(json.dumps({'event':'STOP_ENTERED','at':time.monotonic(),'deadline':deadline.end}),flush=True)
 return {'stopConfirmed':False}
safety.ParentWatch.wait=wait
safety.stop_apps=stop
sys.argv=['supervisor','--execute-after-approval','--parent-pid',sys.argv[2],
 '--run-seconds','4','--poll-seconds','4','--stop-seconds','.3']
raise SystemExit(safety.main())
'''
        for received in (signal.SIGTERM, signal.SIGINT):
            with self.subTest(signal=received):
                parent = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(20)'])
                child = subprocess.Popen([sys.executable, '-u', '-c', child_code, str(ROOT), str(parent.pid)],
                                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                try:
                    for event in ('SUPERVISOR_ARMED', 'WAIT_ENTERED'):
                        line = bytearray(); until = time.monotonic() + 3
                        with selectors.DefaultSelector() as selector:
                            selector.register(child.stdout, selectors.EVENT_READ)
                            while not line.endswith(b'\n'):
                                remaining = until - time.monotonic()
                                self.assertGreater(remaining, 0, 'Local signal probe did not become ready')
                                self.assertTrue(selector.select(remaining), 'Local signal probe did not become ready')
                                line.extend(os.read(child.stdout.fileno(), 1))
                        self.assertEqual(json.loads(line)['event'], event)
                    sent = time.monotonic()
                    os.kill(child.pid, received)
                    try: stdout, stderr = child.communicate(timeout=1)
                    except subprocess.TimeoutExpired:
                        self.fail('Signal did not wake the parent wait within the stop allowance')
                    self.assertEqual(child.returncode, 2)
                    self.assertEqual(stderr, '')
                    rows = [json.loads(line) for line in stdout.splitlines()]
                    entered = next(r for r in rows if r['event'] == 'STOP_ENTERED')
                    self.assertLess(entered['at'] - sent, .3)
                    self.assertLessEqual(entered['deadline'], sent + .4)
                    self.assertEqual(rows[-1]['trigger'], 'SIGNAL')
                    self.assertIsNone(parent.poll())
                finally:
                    for process in (child, parent):
                        if process.poll() is None: process.kill()
                        process.wait(timeout=2)
                        for pipe in (process.stdout, process.stderr):
                            if pipe: pipe.close()


if __name__ == '__main__':
    unittest.main()
