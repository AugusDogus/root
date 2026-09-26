#!/usr/bin/env python3
"""Check resource guards without launching a game or allocating large buffers."""
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from unittest.mock import Mock, patch

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
import test_budget


class BudgetTests(unittest.TestCase):
    def check_budget(self, group, limit='4294967296', swap='0', available=16 * test_budget.GIB):
        with patch.object(Path, 'read_text', side_effect=[group, limit, swap]), patch('test_budget.available_memory', return_value=available):
            test_budget.require_test_budget()

    def test_uncapped_launch_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'safe-test.py'):
            self.check_budget('0::/unbounded')
        for limit, swap in [('max', '0'), ('8589934592', '0'), ('4294967296', 'max')]:
            with self.subTest(limit=limit, swap=swap), self.assertRaisesRegex(RuntimeError, 'maximum 4 GiB'):
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
