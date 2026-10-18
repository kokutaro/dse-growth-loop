import contextlib
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts'))
import check_action_workflows as lint

WORKFLOW='''name: queue-test
on: push
jobs:
  delivery:
    runs-on: ubuntu-latest
    concurrency:
      group: deployment
      cancel-in-progress: false
      queue: max
    steps:
      - run: echo '${{ invalid_context.value }}'
'''

class WorkflowLintTests(unittest.TestCase):
    def test_only_validated_queue_field_is_removed_and_original_lines_are_preserved(self):
        text=lint.compatible_source(WORKFLOW)
        self.assertNotIn('queue: max',text)
        self.assertIn('on: push',text)
        self.assertIn('invalid_context.value',text)
        self.assertEqual(len(text.splitlines()),len(WORKFLOW.splitlines()))
        self.assertIn('queue: max',lint.compatible_source('name: ordinary\non: push\nqueue: max\n'))

    def test_invalid_conflicting_duplicate_or_inline_queue_is_not_hidden(self):
        for text in (WORKFLOW.replace('queue: max','queue: wrong'),WORKFLOW.replace('queue: max','queue: false'),
                WORKFLOW.replace('cancel-in-progress: false','cancel-in-progress: true'),
                WORKFLOW.replace('queue: max','queue: max\n      queue: single'),
                'on: push\nconcurrency: {group: example, queue: max}\n'):
            with self.subTest(text=text),self.assertRaises(ValueError):lint.compatible_source(text)
        self.assertNotIn('queue: single',lint.compatible_source(WORKFLOW.replace('queue: max','queue: single')))

    def test_workflow_and_job_queue_both_preserve_other_validation_input(self):
        text=WORKFLOW.replace('jobs:','concurrency:\n  group: global\n  queue: max\njobs:')
        self.assertNotIn('queue: max',lint.compatible_source(text))

    def test_real_linter_exit_status_and_source_name_are_preserved(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'workflow.yml';path.write_text(WORKFLOW)
            with patch.object(lint.subprocess,'run',return_value=subprocess.CompletedProcess([],1,'semantic error','')) as run,contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(lint.main(['--actionlint','tool','--no-shellcheck',str(path)]),1)
                self.assertIn(str(path),run.call_args.args[0])
                self.assertIn('invalid_context.value',run.call_args.kwargs['input'])
            with patch.object(lint.subprocess,'run',return_value=subprocess.CompletedProcess([],0,'','')),contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(lint.main(['--actionlint','tool',str(path)]),0)
            with patch.object(lint.subprocess,'run',side_effect=FileNotFoundError()),contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(lint.main(['--actionlint','missing',str(path)]),1)
            path.write_text('[')
            with patch.object(lint.subprocess,'run') as run,contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(lint.main([str(path)]),1)
                run.assert_not_called()

if __name__=='__main__':unittest.main()
