import contextlib
import io
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'scripts'))
import issue30_restore_plan as restore
import issue30_safety_supervisor as safety


def snapshot():
    return {'schemaVersion':1,'target':restore.TARGET,
            'apps':[{'name':name,'template':{'name':name,'properties':{'environmentId':safety.ENV,
                     'configuration':{'activeRevisionsMode':'Single'},
                     'template':{'scale':{'minReplicas':0 if name=='dse-api-vnet' else 1,'maxReplicas':1},
                                 'containers':[{'image':'dummy@sha256:'+'a'*64,'env':[{'name':'secret','value':'never-print'}]}]}}}}
                    for name in safety.ALLOWED]}


def stop_reports():
    return {name:{'mode':'replicas','target':dict(restore.TARGET,app=name),
                  'checks':[{'id':'app-environment','status':'passed','data':{'name':name,'environment':safety.ENV}},
                            {'id':'all-revisions','status':'passed','data':[{'name':name+'-rev','active':False,'replicas':0,'status':'Stopped'}]},
                            {'id':'replicas-'+name+'-rev','status':'passed','data':{'count':0}},
                            {'id':'all-revisions-final','status':'passed','data':[{'name':name+'-rev','active':False,'replicas':0,'status':'Stopped'}]}]}
            for name in safety.ALLOWED}


class RestorePlanTests(unittest.TestCase):
    def test_conflicting_template_environment_alias_refuses_freeze(self):
        value=copy.deepcopy(snapshot())
        value['apps'][0]['template']['properties']['managedEnvironmentId']='other-env'
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'source';dest=Path(directory)/'frozen';source.write_text(json.dumps(value))
            with self.assertRaises(ValueError):restore.freeze_snapshot(source,dest)
            self.assertFalse(dest.exists())

    def test_snapshot_write_is_exclusive_hash_bound_and_private(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'original.json'; destination=Path(directory)/'frozen.json'
            raw=json.dumps(snapshot()).encode();source.write_bytes(raw)
            result=restore.freeze_snapshot(source,destination)
            self.assertEqual(destination.read_bytes(),raw)
            self.assertEqual(result['sha256'],hashlib.sha256(raw).hexdigest())
            self.assertEqual(destination.stat().st_mode & 0o777,0o600)
            self.assertNotIn('never-print',json.dumps(result))
            with self.assertRaises(FileExistsError): restore.freeze_snapshot(source,destination)
            self.assertEqual(destination.read_bytes(),raw)

    def test_wrong_environment_app_or_scale_is_refused_before_snapshot_creation(self):
        for kind in ('environment','app','scale','resource-id','image'):
            value=copy.deepcopy(snapshot())
            if kind=='environment':value['apps'][0]['template']['properties']['environmentId']='old-env'
            elif kind=='app':value['apps'][0]['name']='dse-api'
            elif kind=='scale':value['apps'][0]['template']['properties']['template']['scale']['maxReplicas']=2
            elif kind=='resource-id':value['apps'][0]['template']['id']='/subscriptions/wrong/resourceGroups/other/providers/Microsoft.App/containerApps/dse-api-vnet'
            else:value['apps'][0]['template']['properties']['template']['containers']=[]
            with self.subTest(kind=kind),tempfile.TemporaryDirectory() as directory:
                source=Path(directory)/'source';dest=Path(directory)/'dest';source.write_text(json.dumps(value))
                with self.assertRaises(ValueError):restore.freeze_snapshot(source,dest)
                self.assertFalse(dest.exists())

    def test_plan_matches_both_stop_targets_and_never_claims_actual_restoration(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'snapshot';source.write_text(json.dumps(snapshot()))
            digest=hashlib.sha256(source.read_bytes()).hexdigest()
            with patch('subprocess.Popen',side_effect=AssertionError('No CLI permitted')):
                result=restore.review_plan(source,digest,stop_reports())
            self.assertTrue(result['planValid'])
            self.assertTrue(result['keepStoppedRequired'])
            self.assertFalse(result['executionEnabled'])
            self.assertFalse(result['restorationConfirmed'])
            self.assertEqual(result['liveAcceptance'],'pending')

    def test_unknown_changed_hash_or_wrong_stop_target_keeps_stop_requirement(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'snapshot';source.write_text(json.dumps(snapshot()));digest=hashlib.sha256(source.read_bytes()).hexdigest()
            cases=[('bad-hash','0'*64,stop_reports()),('missing-worker',digest,{'dse-api-vnet':stop_reports()['dse-api-vnet']}),
                   ('unknown-stop',digest,stop_reports()),('wrong-target',digest,stop_reports())]
            cases[2][2]['dse-api-vnet']['checks'][2]['data']['count']=None
            cases[3][2]['dse-api-vnet']['target']['environment']='dse-environment'
            for name,sha,reports in cases:
                with self.subTest(name=name):
                    result=restore.review_plan(source,sha,reports)
                    self.assertFalse(result['planValid']);self.assertTrue(result['keepStoppedRequired'])
                    self.assertFalse(result['executionEnabled']);self.assertFalse(result['restorationConfirmed'])

    def test_cli_freeze_refuses_overwrite_and_review_never_executes(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'source';dest=Path(directory)/'frozen';reports=Path(directory)/'reports'
            source.write_text(json.dumps(snapshot()));reports.write_text(json.dumps(stop_reports()))
            args=['restore','freeze','--source',str(source),'--destination',str(dest)]
            with patch.object(sys,'argv',args),contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(restore.main(),0)
            original=dest.read_bytes();digest=json.loads(output.getvalue())['sha256']
            self.assertNotIn('never-print',output.getvalue())
            with patch.object(sys,'argv',args),contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit) as error:
                restore.main()
            self.assertEqual(error.exception.code,2);self.assertEqual(dest.read_bytes(),original)
            args=['restore','review','--snapshot',str(dest),'--sha256',digest,'--stop-reports',str(reports)]
            with patch.object(sys,'argv',args),patch('subprocess.Popen',side_effect=AssertionError('No Azure')) as cli,contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(restore.main(),0)
            cli.assert_not_called();self.assertFalse(json.loads(output.getvalue())['restorationConfirmed'])
