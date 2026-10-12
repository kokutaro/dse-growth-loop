import contextlib
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import issue30_safety_supervisor as safety


class Clock:
    now = 10.0
    def monotonic(self): return self.now
    def sleep(self, seconds): self.now += seconds


class SupervisorUnitTests(unittest.TestCase):
    def test_remaining_deadline_reaches_every_cli_call_and_revision(self):
        clock = Clock()
        deadline = safety.Deadline(1.0, clock.monotonic)
        cli = safety.AzureCli(['fake'], call_timeout=8.0)
        observed = []
        class Process:
            returncode = 0
            pid = 999
            stdout = stderr = None
            def poll(self): return 0
            def wait(self, timeout): return 0
            def communicate(self, timeout):
                observed.append(timeout); clock.now += .4
                return ('[]', '')
        with patch.object(safety.subprocess, 'Popen', return_value=Process()), patch.object(safety.os,'killpg',side_effect=ProcessLookupError):
            cli.call(['containerapp', 'revision', 'list'], deadline)
            cli.call(['containerapp', 'revision', 'list'], deadline)
            with self.assertRaises(safety.SafetyError): cli.call(['containerapp', 'revision', 'list'], deadline)
            with self.assertRaises(safety.SafetyError): cli.call(['containerapp', 'revision', 'list'], deadline)
        self.assertAlmostEqual(observed[0], 1.0)
        self.assertAlmostEqual(observed[1], .6)
        self.assertAlmostEqual(observed[2], .2)
        calls = []
        def call(parts, budget):
            budget.remaining(); calls.append(parts)
            clock.now += .6
            if 'show' in parts: return {'name': 'dse-api-vnet', 'environment': safety.ENV}
            return [{'name':'one','active':True,'replicas':1,'status':'Running'}, {'name':'two','active':True,'replicas':1,'status':'Running'}]
        clock.now = 10
        with self.assertRaises(safety.SafetyError):
            safety.stop_app(type('Cli', (), {'call': staticmethod(call)})(), 'dse-api-vnet', safety.Deadline(1.0, clock.monotonic))
        self.assertFalse(any('deactivate' in x for x in calls))

    def test_both_apps_receive_stop_requests_before_api_confirmation_spends_budget(self):
        clock=Clock();calls=[]
        state={'dse-api-vnet':[{'name':f'api-{i}','active':True,'replicas':1,'status':'Running'} for i in range(4)],
               'dse-worker-vnet':[{'name':'worker-0','active':True,'replicas':1,'status':'Running'}]}
        def call(parts,budget):
            budget.remaining();calls.append(parts);clock.now+=1
            app=parts[parts.index('--name')+1]
            if 'show' in parts:return {'name':app,'environment':safety.ENV}
            if 'deactivate' in parts:
                name=parts[parts.index('--revision')+1]
                for r in state[app]:
                    if r['name']==name:r.update(active=False,replicas=0,status='Stopped')
                return None
            if 'revision' in parts:return [dict(r) for r in state[app]]
            return []
        cli=type('Cli',(),{'call':staticmethod(call)})()
        with patch.object(safety.time,'sleep',side_effect=clock.sleep):
            result=safety.stop_apps(cli,list(safety.ALLOWED),safety.Deadline(8,clock.monotonic),.1)
        requests=[p for p in calls if 'deactivate' in p]
        self.assertTrue(any(p[p.index('--name')+1]=='dse-worker-vnet' for p in requests))
        self.assertFalse(state['dse-worker-vnet'][0]['active'])
        self.assertFalse(any('replica' in p for p in calls))
        self.assertFalse(result['stopConfirmed'])
        self.assertIn('dse-worker-vnet',result['unconfirmedApps'])

    def test_missing_cli_is_sanitized_and_fail_closed(self):
        with patch.object(safety.subprocess, 'Popen', side_effect=FileNotFoundError('secret-path')):
            with self.assertRaises(safety.SafetyError) as error:
                safety.AzureCli(['fake']).call(['containerapp', 'show'], safety.Deadline(1))
        self.assertEqual(error.exception.code, 'CLI_UNAVAILABLE')
        self.assertNotIn('secret', str(error.exception))

    def test_hung_api_preparation_preserves_worker_request_budget(self):
        clock=Clock();calls=[]
        state={app:[{'name':app+'-rev','active':True,'replicas':1,'status':'Running'}] for app in safety.ALLOWED}
        def call(parts,budget):
            remaining=budget.remaining();calls.append(parts)
            app=parts[parts.index('--name')+1]
            elapsed=30 if app=='dse-api-vnet' and 'show' in parts else .25
            if elapsed>=remaining:
                clock.now+=remaining
                raise safety.SafetyError('CLI_TIMEOUT')
            clock.now+=elapsed
            if 'show' in parts:return {'name':app,'environment':safety.ENV}
            if 'deactivate' in parts:
                state[app][0].update(active=False,replicas=0,status='Stopped');return None
            if 'revision' in parts:return [dict(r) for r in state[app]]
            return []
        cli=type('Cli',(),{'call':staticmethod(call)})()
        with patch.object(safety.time,'sleep',side_effect=clock.sleep):
            result=safety.stop_apps(cli,list(safety.ALLOWED),safety.Deadline(8,clock.monotonic),.1)
        self.assertFalse(state['dse-worker-vnet'][0]['active'])
        self.assertIn('dse-worker-vnet',result['confirmedApps'])
        self.assertFalse(result['stopConfirmed'])
        self.assertTrue(any(f.get('app')=='dse-api-vnet' and f['code']=='CLI_TIMEOUT' for f in result['failures']))

    def test_emergency_resource_stop_never_claims_worker_quiescence(self):
        with patch.object(safety,'prepare_stop',return_value=[]),patch.object(safety,'confirm_stop',return_value=True):
            result=safety.stop_apps(None,list(safety.ALLOWED),safety.Deadline(1),.1)
        self.assertTrue(result['resourceStopConfirmed'])
        self.assertTrue(result['stopConfirmed'])
        self.assertEqual(result['shutdownMode'],'emergency-resource-stop')
        self.assertFalse(result['workerQuiescenceConfirmed'])
        self.assertFalse(result['gracefulStopConfirmed'])
        self.assertTrue(result['inFlightRecoveryRequired'])

    def test_timeout_kills_cli_group_even_if_cli_parent_has_already_exited(self):
        from unittest.mock import Mock
        process=Mock(pid=123,returncode=0)
        process.communicate.side_effect=subprocess.TimeoutExpired(['fake'],.1)
        process.poll.return_value=0
        with patch.object(safety.subprocess,'Popen',return_value=process), patch.object(safety.os,'killpg') as kill, self.assertRaises(safety.SafetyError):
            safety.AzureCli(['fake']).call(['containerapp','show'],safety.Deadline(.1))
        kill.assert_called_once_with(123,signal.SIGKILL)

    def test_plan_only_does_not_start_cli_or_monitor(self):
        with patch.object(sys, 'argv', ['supervisor']), patch.object(safety.subprocess, 'Popen') as launch, \
                patch.object(safety, 'ParentWatch') as monitor, contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(safety.main(), 0)
        launch.assert_not_called(); monitor.assert_not_called()
        self.assertIn('PLAN_ONLY', output.getvalue())

    def test_invalid_or_old_target_arguments_do_not_launch(self):
        for args in (['--apps','dse-api'], ['--run-seconds','nan'], ['--stop-seconds','0'], ['--run-seconds','1e308','--stop-seconds','1e308']):
            with self.subTest(args=args), patch.object(sys,'argv',['supervisor',*args]), patch.object(safety.subprocess,'Popen') as launch, \
                    contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit): safety.main()
            launch.assert_not_called()


class SupervisorOsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='dse-supervisor-os-')
        self.folder = Path(self.temp.name)
        self.state = self.folder / 'state.json'; self.events = self.folder / 'events.jsonl'
        self.state.write_text(json.dumps({name:[{'name':name+'-rev','active':True,'replicas':1,'status':'Running'}]
                                         for name in safety.ALLOWED}))
        self.env = dict(os.environ, DSE_FAKE_AZ_STATE=str(self.state), DSE_FAKE_AZ_EVENTS=str(self.events))
        self.parent = subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'],start_new_session=True)
        self.supervisors = []
        self.orphans = []

    def tearDown(self):
        for process in self.supervisors + [self.parent]:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=2)
            for pipe in (process.stdout,process.stderr):
                if pipe:pipe.close()
        for pid in self.orphans:
            try: os.killpg(pid,signal.SIGKILL)
            except ProcessLookupError: pass
        self.temp.cleanup()

    def launch(self, scenario='success', run_seconds=10, stop_seconds=3, call_timeout_seconds=.3):
        env = dict(self.env, DSE_FAKE_AZ_SCENARIO=scenario)
        cli_command=[str(self.folder/'missing-cli')] if scenario=='missing-cli' else [sys.executable,str(ROOT/'tests/scripts/fixtures/issue30_fake_az.py')]
        command = [sys.executable, str(ROOT/'scripts/issue30_safety_supervisor.py'), '--execute-after-approval',
                   '--parent-pid',str(self.parent.pid),'--run-seconds',str(run_seconds),'--stop-seconds',str(stop_seconds),
                   '--poll-seconds','.03','--call-timeout-seconds',str(call_timeout_seconds), '--cli-command',*cli_command]
        process = subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,env=env,start_new_session=True)
        self.supervisors.append(process)
        # Wait for actual readiness, not an arbitrary startup sleep.
        import selectors
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout,selectors.EVENT_READ)
            self.assertTrue(selector.select(timeout=3), 'Supervisor did not arm')
            line=process.stdout.readline();self.assertIn('SUPERVISOR_ARMED',line)
        return process, line

    def finish(self, process, first):
        stdout,stderr=process.communicate(timeout=5)
        output=first+stdout
        self.assertNotIn('secret',output+stderr)
        lines=[json.loads(line) for line in output.splitlines()]
        self.record_evidence(lines[-1])
        return lines[-1], output, stderr

    def record_evidence(self, result):
        path=os.environ.get('DSE_ISSUE30_TEST_EVIDENCE')
        if path:
            with Path(path).open('a') as file:
                file.write(json.dumps({'test':self.id(),'evidenceKind':'real OS processes with local fake CLI; no Azure', 'result':result})+'\n')

    def test_real_parent_exit_triggers_stop_of_both_new_apps(self):
        process, first=self.launch()
        self.parent.terminate();self.parent.wait(timeout=2)
        result,_,_=self.finish(process,first)
        self.assertEqual(process.returncode,0)
        self.assertEqual(result['trigger'],'PARENT_EXIT')
        self.assertTrue(result['stopConfirmed'])
        self.assertTrue(all(not r['active'] and r['replicas']==0 for rows in json.loads(self.state.read_text()).values() for r in rows))

    def test_real_sigterm_triggers_stop_without_waiting_for_parent_exit(self):
        process,first=self.launch()
        os.kill(process.pid,signal.SIGTERM)
        result,_,_=self.finish(process,first)
        self.assertEqual(result['trigger'],'SIGNAL')
        self.assertTrue(result['stopConfirmed'])
        self.assertIsNone(self.parent.poll())

    def test_run_deadline_triggers_stop_within_total_budget(self):
        started=time.monotonic();process,first=self.launch(run_seconds=.1,stop_seconds=2)
        result,_,_=self.finish(process,first)
        self.assertEqual(result['trigger'],'RUN_DEADLINE')
        self.assertTrue(result['stopConfirmed'])
        self.assertLess(time.monotonic()-started,2.8)

    def test_real_cli_denial_invalid_json_and_wrong_environment_never_confirm_stop(self):
        for scenario in ('missing-cli','denied','expired-auth','deactivate-denied','invalid-json','wrong-env'):
            with self.subTest(scenario=scenario):
                process,first=self.launch(scenario,run_seconds=.05,stop_seconds=1)
                result,_,_=self.finish(process,first)
                self.assertEqual(process.returncode,2)
                self.assertFalse(result['stopConfirmed'])
                self.assertTrue(result['keepStoppedRequired'])
                self.assertFalse(result['restorationConfirmed'])
        events=[json.loads(x)['args'] for x in self.events.read_text().splitlines()]
        self.assertFalse(any(x in command for command in events for x in ('activate','update','create','delete','start')))

    def test_hung_cli_and_revisions_that_never_stop_respect_total_deadline(self):
        for scenario in ('hang','never-stops'):
            with self.subTest(scenario=scenario):
                started=time.monotonic();process,first=self.launch(scenario,run_seconds=.05,stop_seconds=.45)
                result,_,_=self.finish(process,first)
                self.assertEqual(process.returncode,2)
                self.assertFalse(result['stopConfirmed'])
                self.assertLess(time.monotonic()-started,1.25)

    def test_actual_parent_child_relationship_survives_parent_termination(self):
        log=self.folder/'detached-supervisor.jsonl';pid_file=self.folder/'child.pid'
        command=[sys.executable,str(ROOT/'scripts/issue30_safety_supervisor.py'),'--execute-after-approval',
                 '--run-seconds','10','--stop-seconds','2','--poll-seconds','.03','--call-timeout-seconds','.3',
                 '--cli-command',sys.executable,str(ROOT/'tests/scripts/fixtures/issue30_fake_az.py')]
        launcher = """import json,os,subprocess,sys,time
from pathlib import Path
command=json.loads(sys.argv[1]);command[2:2]=['--parent-pid',str(os.getpid())]
log=Path(sys.argv[2]);pid_file=Path(sys.argv[3])
with log.open('w') as output:
 child=subprocess.Popen(command,stdout=output,stderr=output,start_new_session=True)
 pid_file.write_text(str(child.pid))
 while True: time.sleep(.1)
"""
        parent=subprocess.Popen([sys.executable,'-c',launcher,json.dumps(command),str(log),str(pid_file)],env=self.env,start_new_session=True)
        self.supervisors.append(parent)
        end=time.monotonic()+3
        while time.monotonic()<end and (not log.exists() or 'SUPERVISOR_ARMED' not in log.read_text()):time.sleep(.02)
        self.assertIn('SUPERVISOR_ARMED',log.read_text())
        child=int(pid_file.read_text());self.orphans.append(child)
        parent.terminate();parent.wait(timeout=2)
        end=time.monotonic()+3
        while time.monotonic()<end and 'SUPERVISOR_FINISHED' not in log.read_text():time.sleep(.02)
        result=json.loads(log.read_text().splitlines()[-1])
        self.assertEqual(result['trigger'],'PARENT_EXIT')
        self.assertTrue(result['stopConfirmed'])
        self.record_evidence(result)

    def test_repeated_sigterm_during_stop_does_not_extend_deadline(self):
        process,first=self.launch('never-stops',run_seconds=.05,stop_seconds=.45)
        end=time.monotonic()+1
        while time.monotonic()<end and (not self.events.exists() or 'deactivate' not in self.events.read_text()):time.sleep(.01)
        os.kill(process.pid,signal.SIGTERM)
        time.sleep(.03)
        if process.poll() is None:os.kill(process.pid,signal.SIGTERM)
        result,_,_=self.finish(process,first)
        self.assertFalse(result['stopConfirmed'])
        self.assertLess(result['elapsedSeconds'],.8)

    def test_successful_cli_json_still_reclaims_child_with_closed_stdio(self):
        env=dict(self.env,DSE_FAKE_AZ_SCENARIO='spawn-child')
        with patch.dict(os.environ,env):
            value=safety.AzureCli([sys.executable,str(ROOT/'tests/scripts/fixtures/issue30_fake_az.py')]).call(
                ['containerapp','show','--name','dse-api-vnet','--resource-group',safety.RG],safety.Deadline(2))
        self.assertEqual(value['name'],'dse-api-vnet')
        children=[json.loads(x) for x in self.events.read_text().splitlines() if 'childPid' in x]
        self.assertEqual(len(children),1)
        for child in children:
            self.orphans.append(child['groupPid'])
            end=time.monotonic()+2
            while time.monotonic()<end:
                try:os.kill(child['childPid'],0)
                except ProcessLookupError:break
                time.sleep(.02)
            else:self.fail('Successful CLI left child process alive')
        self.record_evidence({'event':'CLI_CHILD_RECOVERED','cliSucceeded':True,'childCount':len(children),'childrenAlive':0})

    def test_real_api_preparation_hang_does_not_starve_worker_stop(self):
        process,first=self.launch('api-preparation-hang',run_seconds=.05,stop_seconds=1.2,call_timeout_seconds=2)
        result,_,_=self.finish(process,first)
        self.assertEqual(process.returncode,2)
        self.assertFalse(result['stopConfirmed'])
        self.assertIn('dse-worker-vnet',result['confirmedApps'])
        self.assertFalse(json.loads(self.state.read_text())['dse-worker-vnet'][0]['active'])
        self.assertTrue(result['keepStoppedRequired'])

    def test_real_empty_successful_deactivation_response_can_be_confirmed(self):
        process,first=self.launch('empty-deactivate',run_seconds=.05,stop_seconds=2)
        result,_,_=self.finish(process,first)
        self.assertEqual(process.returncode,0)
        self.assertTrue(result['stopConfirmed'])
        self.assertEqual(set(result['confirmedApps']),set(safety.ALLOWED))
