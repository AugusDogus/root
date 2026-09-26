"""Cloudflare relay adapter. The game itself continues to use loopback TCP."""
import json
import re
import secrets
import socket
import socketserver
import threading
import urllib.parse

from steam import BUILD, VERSION

MAX_RESPONSE = 4 * 1024 * 1024


def relay_url(value: str, local: bool = False) -> str:
    url = urllib.parse.urlsplit(value)
    development = local and url.scheme == 'http' and url.hostname == '127.0.0.1'
    if not (url.scheme == 'https' or development) or not url.hostname or url.username or url.password or url.query or url.fragment or url.path not in ('', '/'):
        raise ValueError('Enter the relay HTTPS address, without a path, password, or query string.')
    return value.rstrip('/')


def parse_invite(text: str, local: bool = False) -> dict:
    try:
        if len(text) > 4096:
            raise ValueError()
        value = json.loads(text)
        if not isinstance(value, dict) or set(value) != {'version', 'build', 'relay', 'room', 'token', 'seat'}:
            raise ValueError()
        if value['version'] != VERSION or value['build'] != BUILD:
            raise ValueError('Invitation needs a different launcher or game build. Ask the host for the matching package.')
        if not isinstance(value['relay'], str):
            raise ValueError()
        relay_url(value['relay'], local)
        for field in ('room', 'token'):
            if not isinstance(value[field], str) or re.fullmatch('[0-9a-f]{32}', value[field]) is None:
                raise ValueError()
        if type(value['seat']) is not int or not 1 <= value['seat'] <= 6:
            raise ValueError()
        return value
    except (KeyError, TypeError, ValueError) as error:
        if str(error).startswith('Invitation needs'):
            raise
        raise ValueError('Invalid invitation. Paste the complete seat invitation supplied by the host.') from error


def exchange(port: int, body: bytes) -> bytes:
    with socket.create_connection(('127.0.0.1', port), timeout=5) as client:
        client.sendall(body + b'\n')
        with client.makefile('rb') as stream:
            response = stream.readline(MAX_RESPONSE + 1)
        if len(response) > MAX_RESPONSE or not response.endswith(b'\n'):
            raise ValueError('Native host returned an incomplete or oversized reply.')
        return response.rstrip(b'\n')


class HostRelay:
    def __init__(self, url: str, key: str, port: int, tokens: list[str], local: bool = False):
        import websocket
        url = relay_url(url, local)
        if not key or '\r' in key or '\n' in key:
            raise ValueError('Enter the relay host key supplied by its operator.')
        self.room = secrets.token_hex(16)
        self.error = ''
        self.stopping = threading.Event()
        self.socket = websocket.create_connection(
            url.replace('https://', 'wss://').replace('http://', 'ws://') + f'/room/{self.room}/host',
            header={'Authorization': f'Bearer {key}', 'X-Seat-Tokens': json.dumps(tokens)}, timeout=15,
            suppress_origin=True)
        self.socket.settimeout(1)
        self.invitations = [json.dumps({'version': VERSION, 'build': BUILD, 'relay': url,
                                       'room': self.room, 'token': token, 'seat': seat})
                            for seat, token in enumerate(tokens, 1)]

        def run():
            while not self.stopping.is_set():
                try:
                    message = self.socket.recv()
                    if not message:
                        raise OSError('Relay closed its connection.')
                    if len(message) > 128 * 1024 + 1024:
                        raise ValueError('Relay request exceeded its limit.')
                    request = json.loads(message)
                    try:
                        body = exchange(port, request['body'].encode()).decode()
                    except (OSError, ValueError):
                        body = json.dumps({'ok': False, 'error': 'Native host unavailable. Rejoin before retrying a move.'})
                    self.socket.send(json.dumps({'id': request['id'], 'body': body}))
                except websocket.WebSocketTimeoutException:
                    continue
                except (OSError, ValueError, KeyError, websocket.WebSocketException):
                    if not self.stopping.is_set():
                        self.error = 'Relay disconnected. Stop and resume the host, then share its new invitations. Saved moves are preserved.'
                    break

        self.thread = threading.Thread(target=run, daemon=True)
        self.thread.start()

    def close(self):
        self.stopping.set()
        self.socket.close()
        self.thread.join(timeout=10)


class ClientRelay:
    def __init__(self, invite: dict):
        import websocket
        self.socket = websocket.create_connection(
            invite['relay'].replace('https://', 'wss://').replace('http://', 'ws://') + f'/room/{invite["room"]}/guest',
            header={'Authorization': 'Bearer ' + invite['token']}, timeout=10, suppress_origin=True)
        self.socket.settimeout(9)
        self.exchange_lock = threading.Lock()
        self.stopping = threading.Event()
        self.slots = threading.BoundedSemaphore(8)
        owner = self

        class Handler(socketserver.StreamRequestHandler):
            def handle(self):
                if not owner.slots.acquire(blocking=False):
                    return
                try:
                    self.connection.settimeout(12)
                    body = self.rfile.readline(65537)
                    if len(body) > 65536 or not body.endswith(b'\n'):
                        return
                    reply = owner.request(body)
                    self.wfile.write(reply + b'\n')
                except OSError:
                    pass
                finally:
                    owner.slots.release()

        class Server(socketserver.ThreadingTCPServer):
            daemon_threads = True

        try:
            self.server = Server(('127.0.0.1', 0), Handler)
        except OSError:
            self.socket.close()
            raise
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def request(self, body: bytes) -> bytes:
        import websocket
        with self.exchange_lock:
            try:
                if self.stopping.is_set():
                    raise OSError('Relay connection closed')
                request_id = secrets.token_hex(16)
                self.socket.send(json.dumps({'id': request_id, 'body': body.decode()}))
                message = self.socket.recv()
                if not isinstance(message, str) or len(message) > MAX_RESPONSE * 2 + 1024:
                    raise ValueError('Invalid relay envelope')
                response = json.loads(message)
                if not isinstance(response, dict) or response.get('id') != request_id or not isinstance(response.get('body'), str):
                    raise ValueError('Unexpected relay response')
                reply = response['body'].encode()
                if len(reply) > MAX_RESPONSE:
                    raise ValueError('Oversized relay response')
                return reply
            except (OSError, ValueError, websocket.WebSocketException):
                # Never reconnect/replay automatically: the host may have accepted a move.
                self.stopping.set()
                self.socket.close()
                return b'{"ok":false,"error":"Relay disconnected. Rejoin before retrying a move."}'

    def close(self):
        self.stopping.set()
        self.socket.close()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
