#!/usr/bin/env python3
"""Launch Root with the six-player menu. --browser opens the development controls."""
import argparse
import atexit
from contextlib import closing
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import secrets
import sys
import threading
import time
import webbrowser
import urllib.parse

HERE = Path(__file__).resolve().parent
from session import Session


def data_home() -> Path:
    if sys.platform == 'win32':
        return Path(os.environ['LOCALAPPDATA']) / 'RootSixPlayer'
    return Path(os.environ.get('XDG_DATA_HOME', str(Path.home() / '.local/share'))) / 'root-six-player'


def lock_data(data: Path):
    data.mkdir(parents=True, exist_ok=True, mode=0o700)
    file = (data / 'launcher.lock').open('a+b')
    try:
        if os.name == 'nt':
            import msvcrt
            file.seek(0)
            file.write(b'0')
            file.flush()
            file.seek(0)
            msvcrt.locking(file.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        file.close()
        raise ValueError('Another launcher is using this data folder. Close it before starting another.')
    return file


def make_server(session: Session, token: str) -> ThreadingHTTPServer:
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format, *args):
            pass

        def respond(self, status: int, body: bytes, mime: str = 'application/json'):
            self.send_response(status)
            self.send_header('Content-Type', mime)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Referrer-Policy', 'no-referrer')
            self.send_header('Content-Security-Policy', "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
            self.end_headers()
            self.wfile.write(body)

        def authorized(self) -> bool:
            host = f'127.0.0.1:{self.server.server_port}'
            return (self.headers.get('Host') == host and
                    self.headers.get('Origin', f'http://{host}') == f'http://{host}' and
                    secrets.compare_digest(self.headers.get('Authorization', ''), f'Bearer {token}'))

        def do_GET(self):
            if self.path == '/api/status':
                if not self.authorized():
                    self.respond(403, b'{"error":"Launcher authentication required"}')
                    return
                self.respond(200, json.dumps(session.status()).encode())
                return
            files = {'/': ('index.html', 'text/html; charset=utf-8'), '/app.js': ('app.js', 'text/javascript'),
                     '/style.css': ('style.css', 'text/css')}
            selected = files.get(self.path)
            if selected is None:
                self.respond(404, b'{}')
                return
            self.respond(200, (HERE / 'web' / selected[0]).read_bytes(), selected[1])

        def do_POST(self):
            if not self.authorized():
                self.respond(403, b'{"error":"Launcher authentication required"}')
                return
            try:
                length = int(self.headers.get('Content-Length', '0'))
                if not 0 < length <= 16384 or self.headers.get('Content-Type') != 'application/json':
                    raise ValueError('Expected a small JSON request.')
                self.connection.settimeout(5)
                values = json.loads(self.rfile.read(length))
                if not isinstance(values, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in values.items()):
                    raise ValueError('Expected text fields.')
                action = self.path.removeprefix('/api/')
                if self.path != f'/api/{action}' or action not in ('prepare', 'host', 'join', 'wait', 'resume', 'stop', 'quit', 'host-native', 'resume-native', 'return-menu'):
                    raise ValueError('Unknown launcher action.')
                if action == 'quit':
                    if session.busy or session.status()['active']:
                        raise ValueError('Stop the session and wait for pending work before closing the launcher.')
                    threading.Thread(target=self.server.shutdown, daemon=True).start()
                else:
                    session.start(action, values)
                self.respond(202, b'{"ok":true}')
            except (ValueError, OSError) as error:
                self.respond(400, json.dumps({'error': str(error)}).encode())

    return ThreadingHTTPServer(('127.0.0.1', 0), Handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, default=data_home())
    parser.add_argument('--headless', action='store_true')
    parser.add_argument('--no-browser', action='store_true')
    parser.add_argument('--browser', action='store_true', help='Open the development controls instead of the game')
    args = parser.parse_args()
    os.umask(0o077)
    data = args.data.expanduser().resolve()
    try:
        lock = lock_data(data)
    except ValueError:
        if args.browser and reopen_launcher(data):
            return
        raise
    with lock:
        session = Session(data, HERE / 'payload', args.headless)
        token = secrets.token_urlsafe(32)
        server = make_server(session, token)
        url = f'http://127.0.0.1:{server.server_port}/#{token}'
        link = data / 'launcher-url.txt'
        link.write_text(url)
        atexit.register(session.stop)
        try:
            if args.browser or args.no_browser:
                if args.browser and not webbrowser.open(url):
                    raise RuntimeError('Your browser could not open the development controls.')
                server.serve_forever()
            else:
                thread = threading.Thread(target=server.serve_forever, daemon=True)
                thread.start()
                try:
                    if not session.prepared():
                        session.perform('prepare', {})
                    control = data / 'client/launcher-control.json'
                    control.write_text(json.dumps({'port': server.server_port, 'token': token}))
                    session.perform('play', {})
                    while session.busy or session.client is not None and session.client.process.poll() is None:
                        time.sleep(0.5)
                    if session.error:
                        raise RuntimeError(session.error)
                finally:
                    server.shutdown()
                    thread.join()
        except KeyboardInterrupt:
            pass
        finally:
            server.server_close()
            if session.worker is not None and session.worker.is_alive():
                print('Waiting for the current operation before cleaning up…', flush=True)
                session.worker.join()
            session.stop()
            atexit.unregister(session.stop)
            link.unlink(missing_ok=True)
            (data / 'client/launcher-control.json').unlink(missing_ok=True)


def reopen_launcher(data: Path) -> bool:
    """A second double-click reopens the existing launcher, without another session."""
    try:
        url = (data / 'launcher-url.txt').read_text()
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme != 'http' or parsed.hostname != '127.0.0.1' or parsed.port is None or
                parsed.username is not None or parsed.password is not None or parsed.path != '/' or
                parsed.query or len(parsed.fragment) != 43):
            return False
        with closing(http.client.HTTPConnection('127.0.0.1', parsed.port, timeout=2)) as connection:
            connection.request('GET', '/api/status', headers={'Authorization': 'Bearer ' + parsed.fragment})
            response = connection.getresponse()
            if response.status != 200:
                return False
            state = json.loads(response.read(65536))
        if not isinstance(state, dict) or state.get('data') != str(data):
            return False
        return webbrowser.open(url)
    except (OSError, ValueError, http.client.HTTPException):
        return False


if __name__ == '__main__':
    main()
