"""Pinned deployment profile; no cloud writes or paid model calls."""
import copy
import sys
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from cost_config import parameters
from delivery_core import preserved

PROFILE = {'AiUsage__MaxInputTokens': '10240', 'AiUsage__MaxOutputTokens': '4096',
           'Foundry__OutputLimit': '4096', 'Jev__OutputLimit': '4096'}

class ChoiceLimitProfileTests(unittest.TestCase):
    def test_deployment_defaults_match_runtime_without_enabling_ai_or_accounting_changes(self):
        env = {r['name']: r['value'] for r in parameters({})['runtimeSettings']}
        for k, v in PROFILE.items(): self.assertEqual(v, env[k])
        self.assertEqual(('Mock', 'true', 'false', 'Ledger'), tuple(env[k] for k in
            ['Ai__Mode', 'AiUsage__Stopped', 'AiUsage__LiveOptIn', 'AiUsage__AccountingMode']))

    def test_explicit_smaller_operator_limits_are_preserved(self):
        explicit = {k: '512' for k in PROFILE}
        env = {r['name']: r['value'] for r in parameters(explicit)['runtimeSettings']}
        for k, v in explicit.items(): self.assertEqual(v, env[k])

    def test_example_and_iac_initial_profile_agree(self):
        example = dict(line.split('=', 1) for line in (ROOT/'.env.example').read_text().splitlines()
                       if '=' in line and not line.startswith('#'))
        iac = (ROOT/'infra/main.bicep').read_text()
        for k, v in PROFILE.items():
            self.assertEqual(v, example[k])
            self.assertIn("{ name: '" + k + "', value: '" + v + "' }", iac)

    def test_image_only_delivery_preserves_profile(self):
        from test_delivery import production_app
        for name in ['api', 'worker']:
            before = production_app(name)
            before['properties']['template']['containers'][0]['env'].extend(
                {'name': k, 'value': v} for k, v in PROFILE.items())
            after = copy.deepcopy(before)
            after['properties']['template']['containers'][0]['image'] = 'new-image'
            after['properties']['template']['revisionSuffix'] = 'new-revision'
            self.assertEqual(preserved(before), preserved(after))
