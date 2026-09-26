#!/usr/bin/env python3
"""Exercise the real local Workers runtime through both launcher network adapters."""
from contextlib import ExitStack
from concurrent.futures import ThreadPoolExecutor
import argparse
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import socketserver
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

PROJECT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(PROJECT / 'launcher'), str(PROJECT / 'launcher/vendor')]
from network import ClientRelay, HostRelay, exchange, parse_invite
import websocket


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser()
    parser.add_argument('--native', action='store_true', help='Also prepare and launch isolated native host/client copies')
    args = parser.parse_args()
    key = secrets.token_hex(32)
    tokens = [secrets.token_hex(16) for _ in range(6)]
    with socket.socket() as reserved:
        reserved.bind(('127.0.0.1', 0))
        port = reserved.getsockname()[1]
    with tempfile.TemporaryDirectory(prefix='relay-test-', dir=PROJECT / '.lab') as directory, ExitStack() as stack:
        root = Path(directory)
        (root / '.dev.vars').write_text('HOST_KEY=' + key + '\n')
        config = json.loads((PROJECT / 'relay/wrangler.jsonc').read_text())
        config['main'] = str(PROJECT / 'relay/src/worker.ts')
        (root / 'wrangler.json').write_text(json.dumps(config))
        log = stack.enter_context((root / 'wrangler.log').open('w'))
        worker = subprocess.Popen(['node', str(PROJECT / 'relay/node_modules/wrangler/bin/wrangler.js'),
            'dev', '--config', str(root / 'wrangler.json'), '--ip', '127.0.0.1', '--port', str(port)],
            stdout=log, stderr=subprocess.STDOUT, start_new_session=True,
            env={**os.environ, 'WRANGLER_SEND_METRICS': 'false'})
        def stop_worker():
            os.killpg(worker.pid, signal.SIGTERM)
            try:
                worker.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(worker.pid, signal.SIGKILL)
                worker.wait()
        stack.callback(stop_worker)
        url = f'http://127.0.0.1:{port}'
        deadline = time.monotonic() + 60
        while True:
            try:
                with urllib.request.urlopen(url + '/health', timeout=1) as response:
                    assert json.load(response) == {'ok': True, 'protocol': 2}
                break
            except OSError:
                if time.monotonic() >= deadline or worker.poll() is not None:
                    raise RuntimeError('Local Worker failed to start: ' + (root / 'wrangler.log').read_text().replace(key, '[redacted]'))
                time.sleep(0.2)

        slow_started = threading.Event()
        slow_release = threading.Event()

        class Native(socketserver.StreamRequestHandler):
            def handle(self):
                request = json.loads(self.rfile.readline())
                if request['op'] == 'slow':
                    slow_started.set()
                    slow_release.wait(timeout=3)
                token = request['token']
                result = {'ok': True, 'seat': tokens.index(token), 'op': request['op']} if token in tokens else {'ok': False}
                self.wfile.write(json.dumps(result).encode() + b'\n')

        native = socketserver.ThreadingTCPServer(('127.0.0.1', 0), Native)
        thread = threading.Thread(target=native.serve_forever, daemon=True)
        thread.start()
        stack.callback(native.server_close)
        stack.callback(native.shutdown)
        host = HostRelay(url, key, native.server_address[1], tokens, local=True)
        stack.callback(host.close)
        clients = []
        for text in host.invitations:
            client = ClientRelay(parse_invite(text, local=True))
            clients.append(client)
            stack.callback(client.close)
        sockets = [client.socket for client in clients]

        def check_seat(seat):
            for index in range(12):
                op = f'poll-{seat}-{index}'
                # Relay must replace a spoofed request token with the authenticated seat.
                reply = json.loads(exchange(clients[seat].port, json.dumps({'op': op, 'token': tokens[(seat + 1) % 6]}).encode()))
                assert reply == {'ok': True, 'seat': seat, 'op': op}, reply

        with ThreadPoolExecutor(max_workers=6) as pool:
            list(pool.map(check_seat, range(6)))
        assert all(client.socket is original for client, original in zip(clients, sockets))

        # A second request on one seat cannot reorder or duplicate the pending action.
        clients[5].socket.send(json.dumps({'id': 'aaaa', 'body': json.dumps({'op': 'slow'})}))
        try:
            assert slow_started.wait(timeout=2)
            clients[5].socket.send(json.dumps({'id': 'bbbb', 'body': json.dumps({'op': 'join'})}))
            busy = json.loads(clients[5].socket.recv())
            assert busy['id'] == 'bbbb' and json.loads(busy['body'])['ok'] is False
        finally:
            slow_release.set()
        accepted = json.loads(clients[5].socket.recv())
        assert accepted['id'] == 'aaaa' and json.loads(accepted['body']) == {'ok': True, 'seat': 5, 'op': 'slow'}

        # Oversized guest messages close only that connection, before reaching the host.
        clients[4].socket.send(json.dumps({'id': 'cccc', 'body': json.dumps({'op': 'join', 'blob': 'x' * 65536})}))
        assert clients[4].socket.recv() == ''
        clients[4].close()
        check_seat(3)

        def rejected(invite, status):
            try:
                client = ClientRelay(invite)
            except websocket.WebSocketBadStatusException as error:
                assert error.status_code == status, error.status_code
            else:
                client.close()
                raise AssertionError('Invalid/duplicate connection accepted')

        rejected(parse_invite(host.invitations[0], local=True), 409)
        bad = parse_invite(host.invitations[0], local=True)
        bad['token'] = '0' * 32
        rejected(bad, 403)
        # An idle room must retain its authenticated seat routing.
        time.sleep(12)
        check_seat(1)
        # Closing a guest must not terminate the host or other seats.
        clients[0].close()
        time.sleep(0.2)
        check_seat(2)
        replacement = ClientRelay(parse_invite(host.invitations[0], local=True))
        stack.callback(replacement.close)
        assert json.loads(exchange(replacement.port, b'{"op":"join"}'))['seat'] == 0
        # The HTTP polling endpoint is gone, so the launcher cannot regress to per-poll requests.
        try:
            urllib.request.urlopen(urllib.request.Request(url + f'/room/{host.room}/request', data=b'{}'))
            raise AssertionError('Legacy HTTP endpoint remains enabled')
        except urllib.error.HTTPError as error:
            assert error.code == 404
            error.close()
        # No host key, no room creation.
        request = urllib.request.Request(url + f'/room/{secrets.token_hex(16)}/host')
        try:
            urllib.request.urlopen(request)
            raise AssertionError('Unauthenticated host accepted')
        except urllib.error.HTTPError as error:
            assert error.code == 403
            error.close()
        host.close()
        time.sleep(0.2)
        assert json.loads(exchange(replacement.port, b'{"op":"join"}'))['ok'] is False
        rejected(parse_invite(host.invitations[0], local=True), 503)
        result = {'status': 'passed', 'runtime': 'local Cloudflare Workers/Durable Objects',
                  'transport': 'persistent host and guest WebSockets',
                  'checks': ['six concurrent seats', 'repeated requests reuse connections', 'token spoofing rejected',
                             'invalid token rejected', 'duplicate seat rejected', 'idle connection survives',
                             'one pending request per seat', 'oversized guest message isolated',
                             'guest disconnect isolated', 'seat can rejoin', 'HTTP polling removed',
                             'host key required', 'host disconnect rejected'], 'internetTested': False}
        (PROJECT / 'results/relay-test.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result))
        if args.native:
            native_check(url, key)


def native_check(url, key):
    from main import lock_data
    from prepare import prepare
    from runtime import GameProcess, bootstrap
    from steam import discover
    data = PROJECT / '.lab/friends-launcher-test'
    with lock_data(data), ExitStack() as resources:
        installation = discover()
        prepare(installation, data, PROJECT / 'launcher/payload', lambda message: print(message, flush=True))
        bootstrap(installation, data, True, lambda message: print(message, flush=True))
        host = GameProcess(installation, data / 'host', 'server', headless=True)
        resources.callback(host.close)
        endpoint = host.endpoint()
        print('Fresh-prefix native host is ready.', flush=True)
        relay = HostRelay(url, key, endpoint['port'], endpoint['tokens'], local=True)
        resources.callback(relay.close)
        bridge = ClientRelay(parse_invite(relay.invitations[0], local=True))
        resources.callback(bridge.close)
        connection = data / 'client/connection.json'
        connection.write_text(json.dumps({'port': bridge.port, 'token': endpoint['tokens'][0]}))
        connection.chmod(0o600)
        resources.callback(lambda: connection.unlink(missing_ok=True))
        client = GameProcess(installation, data / 'client', 'client-probe', headless=True)
        resources.callback(client.close)
        code = client.process.wait(timeout=600)
        evidence = data / 'client/results/client-probe/test-results.json'
        if code != 0 or not evidence.is_file():
            raise RuntimeError(f'Native relay test did not finish. Inspect {data / "client/results/client-probe"}.')
        result = json.loads(evidence.read_text())
        assert result['status'] == 'passed' and result['players'] == 6 and result['accepted'] == 1, result
        result['transport'] = 'local Cloudflare Durable Object, persistent host and guest WebSockets'
        result['dedicatedProtonPrefixes'] = True
        result['internetTested'] = False
        (PROJECT / 'results/native-relay-test.json').write_text(json.dumps(result, indent=2) + '\n')
        print('PASS: fresh launcher game copies rendered six seats and accepted a native move through Cloudflare relay.', flush=True)


if __name__ == '__main__':
    main()
