#!/usr/bin/env python3
"""Check resource guards without launching a game or allocating large buffers."""
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
import test_budget


class BudgetTests(unittest.TestCase):
    def test_game_guard_distinguishes_owned_tests_from_other_games(self):
        with tempfile.TemporaryDirectory() as temporary:
            proc = Path(temporary)
            process = proc / '123'
            process.mkdir()
            (process / 'comm').write_text('Root.exe\n')
            (process / 'cgroup').write_text('0::/user.slice/root-private-test.service\n')
            self.assertFalse(test_budget.external_game_running(proc))
            (process / 'cgroup').write_text('0::/user.slice/root-six-player-070.service\n')
            self.assertTrue(test_budget.external_game_running(proc))
            (process / 'comm').write_text('steam\n')
            self.assertFalse(test_budget.external_game_running(proc))
            (process / 'comm').unlink()
            self.assertFalse(test_budget.external_game_running(proc))

    def test_game_guard_rejects_launch_before_starting_child(self):
        supervise = runpy.run_path(str(PROJECT / 'scripts/safe-test.py'))['supervise']
        with patch.dict(supervise.__globals__, require_test_budget=Mock(), external_game_running=lambda: True), \
                patch('subprocess.Popen') as launch:
            with self.assertRaisesRegex(RuntimeError, 'Root is running'):
                supervise(['test-only'])
        launch.assert_not_called()

    def test_running_job_stops_when_user_opens_root(self):
        supervise = runpy.run_path(str(PROJECT / 'scripts/safe-test.py'))['supervise']
        child = Mock(pid=12345)
        child.poll.return_value = None
        with patch.dict(supervise.__globals__, require_test_budget=Mock(),
                        available_memory=lambda: test_budget.LIMIT + test_budget.RESERVE,
                        external_game_running=Mock(side_effect=[False, True])), \
                patch('subprocess.Popen', return_value=child), patch('os.killpg') as stop:
            self.assertEqual(supervise(['test-only']), 1)
            stop.assert_called_once()
            self.assertEqual(stop.call_args.args[0], child.pid)

    def check_budget(self, group, limit='8589934592', swap='0', available=16 * test_budget.GIB):
        with patch.object(Path, 'read_text', side_effect=[group, limit, swap]), patch('test_budget.available_memory', return_value=available):
            test_budget.require_test_budget()

    def test_uncapped_launch_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'safe-test.py'):
            self.check_budget('0::/unbounded')
        for limit, swap in [('max', '0'), ('8589934593', '0'), ('4294967296', 'max')]:
            with self.subTest(limit=limit, swap=swap), self.assertRaisesRegex(RuntimeError, 'maximum 8 GiB'):
                self.check_budget('0::/root-private-test.service', limit, swap)

    def test_desktop_reserve(self):
        self.check_budget('0::/root-private-test.service')
        with self.assertRaises(MemoryError):
            self.check_budget('0::/root-private-test.service', available=7 * test_budget.GIB)

    def test_running_job_stops_on_memory_pressure(self):
        supervise = runpy.run_path(str(PROJECT / 'scripts/safe-test.py'))['supervise']
        child = Mock(pid=12345)
        child.poll.return_value = None
        with patch.dict(supervise.__globals__, require_test_budget=Mock(), available_memory=lambda: 0), \
                patch('subprocess.Popen', return_value=child), patch('os.killpg') as stop:
            self.assertEqual(supervise(['test-only']), 1)
            stop.assert_called_once()
            self.assertEqual(stop.call_args.args[0], child.pid)

    def test_actual_service_limits_and_exclusive_job(self):
        test_budget.require_test_budget()
        nested = subprocess.run([sys.executable, str(PROJECT / 'scripts/safe-test.py'), 'true'], capture_output=True)
        self.assertEqual(nested.returncode, 1)
        self.assertIn(b'Another constrained development job', nested.stderr)


if __name__ == '__main__':
    unittest.main()
