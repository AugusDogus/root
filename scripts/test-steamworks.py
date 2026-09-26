#!/usr/bin/env python3
"""Probe Root's Steam API in a copied game on its own muted Xvfb display."""
import json
import os
from pathlib import Path
import shutil
import secrets
import socketserver
import sys
import threading

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from runtime import GameProcess
from steam import discover


def main():
    os.umask(0o077)
    lab = PROJECT / '.lab/steam-probe'
    with lock_data(lab):
        if not (lab / 'game').exists():
            shutil.copytree(PROJECT / '.lab/friends-launcher-test/client/game', lab / 'game')
        shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll', lab / 'game/BepInEx/plugins/EngineProbe.dll')
        evidence = lab / 'results/steam-probe/test-results.json'
        evidence.unlink(missing_ok=True)
        tokens = [secrets.token_hex(16) for _ in range(6)]
        blob_length = 4 * 1024 * 1024 - 100
        requests = []

        class Authority(socketserver.StreamRequestHandler):
            def handle(self):
                self.connection.settimeout(5)
                request = json.loads(self.rfile.readline(65537))
                seat = tokens.index(request['token'])
                requests.append((seat, request['op']))
                self.wfile.write(json.dumps({'ok': True, 'seat': seat, 'blob': 'x' * blob_length}).encode() + b'\n')

        authority = socketserver.ThreadingTCPServer(('127.0.0.1', 0), Authority)
        thread = threading.Thread(target=authority.serve_forever, daemon=True)
        thread.start()
        (lab / 'steam-config.json').write_text(json.dumps({'port': authority.server_address[1],
                                                        'tokens': tokens, 'blobLength': blob_length}))
        game = None
        try:
            game = GameProcess(discover(), lab, 'steam-probe', headless=True)
            game.process.wait(timeout=300)
            if not evidence.is_file():
                raise RuntimeError(f'Steam probe did not report a result. Inspect {lab / "results/steam-probe"}.')
            result = json.loads(evidence.read_text())
            if result['status'] == 'passed' and sorted(requests) != sorted((seat, op) for seat in range(1, 6) for op in ('join', 'poll')):
                raise AssertionError('Unexpected requests reached the authority.')
            (PROJECT / 'results/steamworks-probe.json').write_text(json.dumps(result, indent=2) + '\n')
            print(json.dumps(result))
            return 0 if result['status'] == 'passed' else 1
        finally:
            if game is not None:
                game.close()
            (lab / 'steam-config.json').unlink(missing_ok=True)
            authority.shutdown()
            authority.server_close()
            thread.join()


if __name__ == '__main__':
    raise SystemExit(main())
