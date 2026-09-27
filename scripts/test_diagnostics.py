"""Small, credential-free progress records for isolated development tests."""
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import tempfile
import time
import uuid


def atomic_json(path, value):
    """Publish a complete private JSON document, never a partial write."""
    path = Path(path)
    descriptor, temporary = tempfile.mkstemp(prefix=f'.{path.name}-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'w') as output:
            json.dump(value, output, indent=2)
            output.write('\n')
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def progress(message):
    # Closing an agent's output reader must not abort a running test.
    try:
        print(message, flush=True)
    except BrokenPipeError:
        pass


def create_run(parent):
    parent = Path(parent)
    parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    name = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ-') + uuid.uuid4().hex[:8]
    directory = parent / name
    directory.mkdir(mode=0o700)
    (directory / 'output.log').touch(mode=0o600)
    atomic_json(directory / 'report.json', {
        'status': 'running',
        'started_at': datetime.now(timezone.utc).isoformat(),
        'elapsed_seconds': None,
        'exit_code': None,
    })
    atomic_json(parent / 'latest.json', {'run_directory': str(directory.resolve())})
    return directory


def finish_run(directory, exit_code, elapsed):
    path = directory / 'report.json'
    record = json.loads(path.read_text())
    record.update(status='passed' if exit_code == 0 else 'failed',
                  elapsed_seconds=round(elapsed, 3), exit_code=exit_code)
    atomic_json(path, record)


class Diagnostics:
    """Use static stage labels only. Do not pass credentials or game state."""

    def __init__(self, directory=None):
        self.directory = Path(directory) if directory is not None else None
        self.stages = []

    @classmethod
    def from_environment(cls):
        return cls(os.environ.get('ROOT_TEST_RUN_DIR'))

    def _save(self):
        if self.directory is not None:
            atomic_json(self.directory / 'stages.json', {'stages': self.stages})

    @contextmanager
    def stage(self, name):
        started = time.monotonic()
        entry = {'name': name, 'status': 'running', 'elapsed_seconds': None}
        self.stages.append(entry)
        self._save()
        progress(f'Stage: {name}')
        try:
            yield
        except BaseException as error:
            entry['status'] = 'failed'
            # Exception messages can contain tokens, URLs, or private game state.
            entry['error_type'] = type(error).__name__
            raise
        else:
            entry['status'] = 'passed'
        finally:
            entry['elapsed_seconds'] = round(time.monotonic() - started, 3)
            self._save()
            progress(f"Stage {entry['status']}: {name} ({entry['elapsed_seconds']:.1f}s)")

    def wait(self, name, predicate, *, timeout, interval=0.25):
        """Return the first truthy result; timeout belongs to this named stage."""
        if timeout <= 0 or interval <= 0:
            raise ValueError('Stage timeout and polling interval must be positive.')
        with self.stage(name):
            started = time.monotonic()
            deadline = started + timeout
            while True:
                result = predicate()
                if result:
                    return result
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(f'Stage "{name}" timed out after {time.monotonic() - started:.1f}s.')
                time.sleep(min(interval, remaining))
