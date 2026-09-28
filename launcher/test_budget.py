"""Resource requirements for isolated Linux development launches."""
from pathlib import Path

GIB = 1024 ** 3
LIMIT = 8 * GIB
RESERVE = 8 * GIB
UNIT = 'root-private-test.service'


def external_game_running(proc=Path('/proc')):
    for process in proc.iterdir():
        if not process.name.isdigit():
            continue
        try:
            if (process / 'comm').read_text().strip().lower() != 'root.exe':
                continue
            membership = (process / 'cgroup').read_text().strip().split('::', 1)[1]
            if Path(membership).name != UNIT:
                return True
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            # Processes can exit during inspection; other users' processes are
            # not part of this desktop session.
            continue
    return False


def available_memory():
    for line in Path('/proc/meminfo').read_text().splitlines():
        if line.startswith('MemAvailable:'):
            return int(line.split()[1]) * 1024
    raise RuntimeError('Cannot measure available RAM. No game test was started.')


def require_test_budget():
    membership = Path('/proc/self/cgroup').read_text().strip().split('::', 1)[1]
    group = Path('/sys/fs/cgroup') / membership.lstrip('/')
    if group.name != UNIT:
        raise RuntimeError('Run this development launch through python3 scripts/safe-test.py COMMAND. No game was started.')
    limit = (group / 'memory.max').read_text().strip()
    swap = (group / 'memory.swap.max').read_text().strip()
    if limit == 'max' or int(limit) > LIMIT or swap != '0':
        raise RuntimeError('The test needs a maximum 8 GiB RAM budget and no swap. No game was started.')
    if available_memory() < RESERVE:
        raise MemoryError('Less than 8 GiB RAM is available for the desktop. No game was started.')
