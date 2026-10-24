import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('specific_quality', Path(__file__).resolve().parents[2] / 'scripts/specific_skill_quality.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)


class SpecificQualityTests(unittest.TestCase):
    def pair(self):
        r = dict(id='local-fixture', split='final', sourceSha256='a' * 64, modelVersion='fixture', promptVersion='specific-skills-v1', configSha256='b' * 64)
        expected = dict(version='specific-skills-v1', state='unknown', items=None)
        return dict(version=m.VERSION, records=[dict(r, expected=expected)]), dict(version=m.VERSION, origin='mock', records=[dict(r, status='success', specificSkills=copy.deepcopy(expected))])

    def test_null_empty_and_not_evaluated_are_not_interchangeable(self):
        g, p = self.pair(); self.assertEqual(1, m.evaluate(g, p)['summaries'][1]['correct'])
        p['records'][0]['specificSkills'].update(state='extracted', items=[])
        self.assertEqual(0, m.evaluate(g, p)['summaries'][1]['correct'])
        p['records'][0]['specificSkills'].update(state='not_evaluated', items=None)
        self.assertEqual(0, m.evaluate(g, p)['summaries'][1]['correct'])

    def test_old_gold_mock_and_changed_bindings_cannot_be_new_quality_evidence(self):
        g, p = self.pair(); self.assertFalse(m.evaluate(g, p)['qualityClaim'])
        self.assertFalse(m.evaluate(g, p)['independentUnseenTest'])
        for key in m.BINDINGS:
            changed = copy.deepcopy(p); changed['records'][0][key] = 'changed'
            self.assertEqual(0, m.evaluate(g, changed)['summaries'][1]['correct'])
        g['version'] = 'offline-quality-v1'
        with self.assertRaises(ValueError): m.evaluate(g, p)

    def test_involvement_and_exact_evidence_remain_in_set_comparison(self):
        g, p = self.pair()
        item = dict(raw='C#', normalizationCandidate=None, evidenceId='e1', quote='C#で実装した。', participation='performed')
        for target in [g['records'][0]['expected'], p['records'][0]['specificSkills']]: target.update(state='extracted', items=[copy.deepcopy(item)])
        self.assertEqual(1, m.evaluate(g, p)['summaries'][1]['correct'])
        p['records'][0]['specificSkills']['items'][0]['participation'] = 'other_person'
        self.assertEqual(0, m.evaluate(g, p)['summaries'][1]['correct'])
        self.assertIsNone(m.evaluate(g, p)['gatePassed'])
