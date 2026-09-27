#!/usr/bin/env python3
"""Fast diagnostics checks, without launching systemd, Wine, or the game."""
import importlib.util
import json
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import Mock, patch

import test_diagnostics as diagnostics

spec = importlib.util.spec_from_file_location('safe_test', Path(__file__).with_name('safe-test.py'))
safe_test = importlib.util.module_from_spec(spec)
spec.loader.exec_module(safe_test)


class DiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_private_run_and_atomic_completion(self):
        directory = diagnostics.create_run(self.root / 'runs')
        self.assertEqual(stat.S_IMODE(directory.stat().st_mode), 0o700)
        self.assertEqual(stat.S_IMODE((directory / 'output.log').stat().st_mode), 0o600)
        self.assertEqual(json.loads((directory.parent / 'latest.json').read_text()),
                         {'run_directory': str(directory)})
        diagnostics.finish_run(directory, 7, 1.23456)
        report = json.loads((directory / 'report.json').read_text())
        self.assertEqual(report['status'], 'failed')
        self.assertEqual(report['exit_code'], 7)
        self.assertEqual(report['elapsed_seconds'], 1.235)
        self.assertEqual(stat.S_IMODE((directory / 'report.json').stat().st_mode), 0o600)
        self.assertEqual(list(directory.glob('.report.json-*')), [])

    def test_failed_publication_preserves_previous_document(self):
        path = self.root / 'report.json'
        diagnostics.atomic_json(path, {'valid': True})
        with patch.object(diagnostics.os, 'replace', side_effect=OSError('fixture')):
            with self.assertRaises(OSError):
                diagnostics.atomic_json(path, {'valid': False})
        self.assertEqual(json.loads(path.read_text()), {'valid': True})
        self.assertEqual(list(self.root.glob('.report.json-*')), [])

    def test_stage_records_running_and_failure_without_error_payload(self):
        recorder = diagnostics.Diagnostics(self.root)
        with self.assertRaisesRegex(ValueError, 'private-token'):
            with recorder.stage('Join room'):
                running = json.loads((self.root / 'stages.json').read_text())
                self.assertEqual(running['stages'][0]['status'], 'running')
                raise ValueError('private-token')
        text = (self.root / 'stages.json').read_text()
        self.assertNotIn('private-token', text)
        stage = json.loads(text)['stages'][0]
        self.assertEqual(stage['status'], 'failed')
        self.assertEqual(stage['error_type'], 'ValueError')
        self.assertGreaterEqual(stage['elapsed_seconds'], 0)

    def test_wait_returns_result_without_recording_it(self):
        recorder = diagnostics.Diagnostics(self.root)
        predicate = Mock(side_effect=[False, {'private': 'state'}])
        with patch.object(diagnostics.time, 'sleep'):
            self.assertEqual(recorder.wait('Board loaded', predicate, timeout=1), {'private': 'state'})
        text = (self.root / 'stages.json').read_text()
        self.assertNotIn('private', text)
        self.assertEqual(json.loads(text)['stages'][0]['status'], 'passed')

    def test_wait_timeout_names_stage_and_records_duration(self):
        recorder = diagnostics.Diagnostics(self.root)
        with patch.object(diagnostics.time, 'monotonic', side_effect=[0, 0, 2, 2, 2]):
            with self.assertRaisesRegex(TimeoutError, 'Board loaded.*2.0s'):
                recorder.wait('Board loaded', lambda: False, timeout=1)
        stage = json.loads((self.root / 'stages.json').read_text())['stages'][0]
        self.assertEqual(stage['status'], 'failed')
        self.assertEqual(stage['elapsed_seconds'], 2)

    def test_disconnected_progress_reader_does_not_abort(self):
        with patch('builtins.print', side_effect=BrokenPipeError):
            diagnostics.progress('fixture')

    def test_wrapper_redirects_output_and_preserves_resource_limits(self):
        process = Mock()
        process.wait.return_value = 9
        with patch.object(safe_test, 'PROJECT', self.root), \
             patch.object(safe_test.sys, 'argv', ['safe-test.py', 'test-program', '--token=private-token']), \
             patch.object(safe_test, 'available_memory', return_value=safe_test.LIMIT + safe_test.RESERVE), \
             patch.object(safe_test.subprocess, 'Popen', return_value=process) as launch:
            self.assertEqual(safe_test.main(), 9)
        args = launch.call_args.args[0]
        self.assertNotIn('--pipe', args)
        for limit in ['MemoryMax=8G', 'MemoryHigh=6G', 'MemorySwapMax=0', 'CPUQuota=150%',
                      'OOMPolicy=kill', 'KillMode=control-group', 'TimeoutStopSec=10']:
            self.assertIn(limit, args)
        run = Path(json.loads((self.root / '.lab/test-runs/latest.json').read_text())['run_directory'])
        self.assertIn(f'StandardOutput=append:{run}/output.log', args)
        self.assertIn(f'--setenv=ROOT_TEST_RUN_DIR={run}', args)
        self.assertEqual(launch.call_args.kwargs['stdout'].name, str(run / 'output.log'))
        report = (run / 'report.json').read_text()
        self.assertNotIn('private-token', report)
        self.assertEqual(json.loads(report)['exit_code'], 9)

    def test_insufficient_memory_still_prevents_launch(self):
        with patch.object(safe_test, 'PROJECT', self.root), \
             patch.object(safe_test.sys, 'argv', ['safe-test.py', 'test-program']), \
             patch.object(safe_test, 'available_memory', return_value=safe_test.LIMIT + safe_test.RESERVE - 1), \
             patch.object(safe_test.subprocess, 'Popen') as launch:
            with self.assertRaises(MemoryError):
                safe_test.main()
        launch.assert_not_called()

    def test_existing_job_still_prevents_launch(self):
        with patch.object(safe_test, 'PROJECT', self.root), \
             patch.object(safe_test.sys, 'argv', ['safe-test.py', 'test-program']), \
             patch.object(safe_test.fcntl, 'flock', side_effect=BlockingIOError), \
             patch.object(safe_test.subprocess, 'Popen') as launch:
            with self.assertRaisesRegex(RuntimeError, 'Another constrained development job'):
                safe_test.main()
        launch.assert_not_called()

    def test_interruption_stops_service_and_records_failure(self):
        process = Mock()
        process.wait.side_effect = [KeyboardInterrupt, 0]
        with patch.object(safe_test, 'PROJECT', self.root), \
             patch.object(safe_test.sys, 'argv', ['safe-test.py', 'test-program']), \
             patch.object(safe_test, 'available_memory', return_value=safe_test.LIMIT + safe_test.RESERVE), \
             patch.object(safe_test.subprocess, 'Popen', return_value=process), \
             patch.object(safe_test.subprocess, 'run') as stop:
            self.assertEqual(safe_test.main(), 130)
        self.assertEqual(stop.call_args.args[0], ['systemctl', '--user', 'stop', safe_test.UNIT])
        run = Path(json.loads((self.root / '.lab/test-runs/latest.json').read_text())['run_directory'])
        self.assertEqual(json.loads((run / 'report.json').read_text())['exit_code'], 130)

    def test_systemd_success_requires_child_completion(self):
        for child_finished in (False, True):
            with self.subTest(child_finished=child_finished):
                def systemd_finished():
                    run = Path(json.loads((self.root / '.lab/test-runs/latest.json').read_text())['run_directory'])
                    if child_finished:
                        diagnostics.atomic_json(run / 'command-exit.json', {'exit_code': 0})
                    return 0
                process = Mock()
                process.wait.side_effect = systemd_finished
                with patch.object(safe_test, 'PROJECT', self.root), \
                     patch.object(safe_test.sys, 'argv', ['safe-test.py', 'test-program']), \
                     patch.object(safe_test, 'available_memory', return_value=safe_test.LIMIT + safe_test.RESERVE), \
                     patch.object(safe_test.subprocess, 'Popen', return_value=process):
                    self.assertEqual(safe_test.main(), 0 if child_finished else 130)

    def test_launch_failure_still_has_report(self):
        with patch.object(safe_test, 'PROJECT', self.root), \
             patch.object(safe_test.sys, 'argv', ['safe-test.py', 'test-program']), \
             patch.object(safe_test, 'available_memory', return_value=safe_test.LIMIT + safe_test.RESERVE), \
             patch.object(safe_test.subprocess, 'Popen', side_effect=OSError('fixture')):
            with self.assertRaises(OSError):
                safe_test.main()
        run = Path(json.loads((self.root / '.lab/test-runs/latest.json').read_text())['run_directory'])
        self.assertEqual(json.loads((run / 'report.json').read_text())['status'], 'failed')


if __name__ == '__main__':
    unittest.main()
