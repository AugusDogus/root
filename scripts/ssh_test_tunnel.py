"""Temporary loopback-only SSH server and forwarding, using disposable keys."""
from contextlib import contextmanager
import getpass
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time


def unused_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


@contextmanager
def local_ssh_forward(target_port, log_path):
    processes = []
    # OpenSSH rejects an authorized-key path underneath world-writable /tmp.
    with tempfile.TemporaryDirectory(prefix='root-ssh-test-', dir=log_path.resolve().parent) as temporary, log_path.open('w') as log:
        root = Path(temporary)
        host_key, client_key = root / 'host', root / 'client'
        for key in [host_key, client_key]:
            subprocess.run(['ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-f', str(key)], check=True)
        ssh_port, forwarded_port = unused_port(), unused_port()
        config = root / 'sshd_config'
        config.write_text('\n'.join([
            f'Port {ssh_port}', 'ListenAddress 127.0.0.1', f'HostKey {host_key}',
            f'PidFile {root / "sshd.pid"}', f'AuthorizedKeysFile {client_key}.pub',
            f'AllowUsers {getpass.getuser()}', 'PasswordAuthentication no', 'KbdInteractiveAuthentication no',
            'UsePAM no', 'AuthenticationMethods publickey', 'AllowTcpForwarding local',
            f'PermitOpen 127.0.0.1:{target_port}', 'PermitTTY no', 'X11Forwarding no',
            'AllowAgentForwarding no', 'ForceCommand /bin/false', 'LogLevel VERBOSE',
        ]) + '\n')
        public = host_key.with_suffix('.pub').read_text().split()
        known_hosts = root / 'known_hosts'
        known_hosts.write_text(f'[127.0.0.1]:{ssh_port} {public[0]} {public[1]}\n')

        def launch(arguments):
            process = subprocess.Popen(arguments, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            processes.append(process)
            return process

        def ready(process, port):
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f'Test SSH process exited; inspect {log_path}')
                try:
                    with socket.create_connection(('127.0.0.1', port), timeout=0.2):
                        return
                except OSError:
                    time.sleep(0.1)
            raise TimeoutError(f'Test SSH port did not become ready; inspect {log_path}')

        try:
            server = launch(['/usr/bin/sshd', '-D', '-e', '-f', str(config)])
            ready(server, ssh_port)
            client = launch(['ssh', '-N', '-T', '-F', '/dev/null', '-p', str(ssh_port), '-i', str(client_key),
                '-o', 'IdentitiesOnly=yes', '-o', 'IdentityAgent=none', '-o', 'BatchMode=yes',
                '-o', 'StrictHostKeyChecking=yes', '-o', f'UserKnownHostsFile={known_hosts}',
                '-o', 'GlobalKnownHostsFile=/dev/null', '-o', 'ExitOnForwardFailure=yes',
                '-L', f'127.0.0.1:{forwarded_port}:127.0.0.1:{target_port}', f'{getpass.getuser()}@127.0.0.1'])
            ready(client, forwarded_port)
            yield forwarded_port
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait()
