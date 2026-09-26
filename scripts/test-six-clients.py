#!/usr/bin/env python3
"""Render six native clients together inside a bounded systemd user service."""
from contextlib import ExitStack
import json
import fcntl
import os
from pathlib import Path
import subprocess
import tempfile
import time
from gameplay_checks import check_private_snapshots
from test_client_support import exchange, stop

PROJECT = Path(__file__).resolve().parents[1]
OUTPUT = PROJECT / '.lab/results/six-clients'


def read_json(path):
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def await_condition(processes, predicate, description, seconds=180):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        assert all(process.poll() is None for process in processes), f'{description}: a lab process exited'
        value = predicate()
        if value:
            return value
        time.sleep(0.5)
    raise TimeoutError(description)


def main():
    group = Path('/sys/fs/cgroup') / Path('/proc/self/cgroup').read_text().strip().split('::', 1)[1].lstrip('/')
    limit = (group / 'memory.max').read_text().strip()
    assert limit != 'max', 'Run in a systemd user service with MemoryMax and MemorySwapMax=0; see README'
    assert (group / 'memory.swap.max').read_text().strip() == '0', 'Disable swap for this test service'
    available = next(int(line.split()[1]) * 1024 for line in Path('/proc/meminfo').read_text().splitlines() if line.startswith('MemAvailable:'))
    assert available >= int(limit) + 2 * 1024**3, 'Not enough available RAM for this budget plus a 2 GiB desktop reserve; free memory before rerunning'
    OUTPUT.mkdir(parents=True, exist_ok=True)
    labs = [PROJECT / f'.lab/clients/seat-{seat}' for seat in range(1, 7)]
    for seat in range(1, 7):
        subprocess.run(['bash', 'scripts/setup-client-lab.sh', str(seat)], cwd=PROJECT, check=True)
    processes = []
    clients = []
    endpoint = None
    invitations = []
    result = {'status': 'running'}
    (OUTPUT / 'test-results.json').write_text(json.dumps(result))
    with ExitStack() as stack:
        def launch(lab, mode, extra):
            log = stack.enter_context((lab / 'fleet-launch.log').open('w'))
            process = subprocess.Popen(['bash', 'scripts/run-lab.sh', mode], cwd=PROJECT,
                env={**os.environ, 'ROOT_LAB_DIR': str(lab), 'ROOT_LAB_LIFETIME_SECONDS': '1800', **extra},
                stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            processes.append(process)
            return process

        try:
            started = time.time()
            host = launch(PROJECT / '.lab', 'server', {'ROOT_LAB_TEST_SEED': '12345'})
            endpoint_file = PROJECT / '.lab/results/server/endpoint.json'
            def fresh_endpoint():
                return read_json(endpoint_file) if endpoint_file.exists() and endpoint_file.stat().st_mtime >= started else None
            endpoint = await_condition(processes, fresh_endpoint, 'Host readiness')
            initial = [exchange(endpoint, {'op': 'join', 'token': token}) for token in endpoint['tokens']]
            accounts = [player['account'] for player in initial[0]['roster']]
            states = []
            prior_memory = int((group / 'memory.current').read_text())
            client_memory = 0
            for seat, lab in enumerate(labs):
                current_memory = int((group / 'memory.current').read_text())
                if seat and current_memory + client_memory + 512 * 1024**2 > int(limit):
                    raise MemoryError(f'Another native client would exceed the service memory budget; {seat} clients started, largest increase {client_memory} bytes. Free memory before rerunning with a larger bounded budget.')
                output = lab / 'results/client-probe'
                output.mkdir(parents=True, exist_ok=True)
                # Only remove test markers while this lab is stopped and locked.
                with (lab / 'run.lock').open('w') as lock:
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    for name in ['finish', 'submit', 'capture', 'fleet-state.json', 'fleet.png']:
                        (output / name).unlink(missing_ok=True)
                with tempfile.NamedTemporaryFile(mode='w', prefix='fleet-invite-', dir=lab, delete=False) as file:
                    json.dump({'port': endpoint['port'], 'token': endpoint['tokens'][seat]}, file)
                    invitation = Path(file.name)
                invitations.append(invitation)
                client = launch(lab, 'client-probe', {'ROOT_LAB_FLEET_TEST': '1', 'ROOT_CLIENT_CONNECTION': str(invitation)})
                clients.append(client)
                def ready():
                    state = read_json(output / 'fleet-state.json')
                    if state:
                        assert state['error'] is None, state['error']
                        if state['players'] == 6 and state['scene'] == 'Playmat' and state['account'] == accounts[seat] and state['queued'] == 0:
                            return state
                    return None
                states.append(await_condition(processes, ready, f'Seat {seat + 1} native board readiness'))
                current_memory = int((group / 'memory.current').read_text())
                client_memory = max(client_memory, current_memory - prior_memory)
                prior_memory = current_memory
                print(f'Seat {seat + 1} ready; {seat + 1} graphical clients running', flush=True)
            displays = [read_json(PROJECT / '.lab/results/server/display.json')]
            displays += [read_json(lab / 'results/client-probe/display.json') for lab in labs]
            assert len({display['display'] for display in displays}) == 7
            assert all(display['display'] != os.environ.get('DISPLAY') for display in displays)
            (labs[0] / 'results/client-probe/submit').touch()
            def moved():
                state = read_json(labs[0] / 'results/client-probe/fleet-state.json')
                assert state['error'] is None, state['error']
                return state if state['accepted'] == 1 else None
            await_condition(processes, moved, 'Seat 1 native placement')
            updated = [exchange(endpoint, {'op': 'join', 'token': token}) for token in endpoint['tokens']]
            final_states = []
            for seat, lab in enumerate(labs):
                def caught_up():
                    state = read_json(lab / 'results/client-probe/fleet-state.json')
                    assert state['error'] is None, state['error']
                    return state if state['cursor'] == updated[seat]['next'] and state['received'] > states[seat]['received'] and state['queued'] == 0 else None
                final_states.append(await_condition(processes, caught_up, f'Seat {seat + 1} receives placement'))
                (lab / 'results/client-probe/capture').touch()
            await_condition(processes, lambda: all((lab / 'results/client-probe/fleet.png').exists() for lab in labs), 'Six screenshots')
            privacy = check_private_snapshots(endpoint)
            result = {'status': 'passed', 'clients': final_states, 'displays': [d['display'] for d in displays],
                      'privacy': privacy, 'memoryPeakBytes': int((group / 'memory.peak').read_text())}
            print('PASS: six simultaneous native clients receive a placement through native controls', flush=True)
        except (Exception, KeyboardInterrupt) as error:
            result = {'status': 'failed', 'error': str(error) or type(error).__name__}
            raise
        finally:
            for lab in labs[:len(clients)]:
                (lab / 'results/client-probe/finish').touch()
            for process in reversed(clients):
                stop(process)
            if endpoint and processes[0].poll() is None:
                try:
                    exchange(endpoint, {'op': 'shutdown', 'token': endpoint['controlToken']})
                except (OSError, ValueError):
                    pass
            if processes:
                stop(processes[0])
            for invitation in invitations:
                invitation.unlink(missing_ok=True)
            (OUTPUT / 'test-results.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    main()
