"""One launcher session, with private host saves and per-seat invitations."""
from datetime import datetime
import json
from pathlib import Path
import secrets
import sys
import subprocess
import threading

from network import ClientRelay, HostRelay, exchange, parse_invite, relay_url
from prepare import prepare
from runtime import GameProcess, bootstrap
from steam import discover, proton_paths
from steam_invite import validate_invite


class Session:
    def __init__(self, data: Path, payload: Path, headless: bool = False):
        self.data = data
        self.payload = payload
        self.headless = headless
        self.busy = False
        self.message = 'Prepare your installed copy of Root to begin.'
        self.error = ''
        self.invitations = []
        self.host = None
        self.client = None
        self.host_relay = None
        self.client_relay = None
        self.endpoint = None
        self.save = ''
        self.worker = None
        self.lock = threading.Lock()
        self.native_menu = False

    def status(self) -> dict:
        error = self.error
        if self.client is not None:
            try:
                state = json.loads((self.data / 'client/steam-status.json').read_text())
                if isinstance(state.get('error'), str):
                    error = state['error']
                invites = state.get('invitations')
                if isinstance(invites, list) and len(invites) == 6 and all(isinstance(item, str) for item in invites):
                    self.invitations = invites
            except (FileNotFoundError, json.JSONDecodeError):
                pass
        if self.host_relay is not None and self.host_relay.error:
            error = self.host_relay.error
        for role, process in [('Host', self.host), ('Client', self.client)]:
            if process is not None and process.process.poll() is not None:
                error = f'{role} has exited. Stop this session, then resume or rejoin. Saved moves are preserved.'
        return {'busy': self.busy, 'message': self.message, 'error': error, 'data': str(self.data),
                'prepared': self.prepared(),
                'active': self.host is not None or self.client is not None,
                'invitations': self.invitations, 'save': self.save,
                'saves': [file.name for file in sorted((self.data / 'saves').glob('*.json'))]}

    def prepared(self) -> bool:
        try:
            return (json.loads((self.data / 'prepared.json').read_text()) ==
                    json.loads((self.payload / 'manifest.json').read_text()) and
                    all((self.data / role / 'bindings-ready').exists() for role in ('host', 'client')))
        except (FileNotFoundError, json.JSONDecodeError):
            return False

    def start(self, action: str, values: dict):
        with self.lock:
            if self.busy:
                raise ValueError('Another operation is still running. Wait for it to finish.')
            native_host = action in ('host-native', 'resume-native') and self.native_menu and self.client is not None and self.host is None
            if action not in ('stop', 'return-menu') and not native_host and (self.host is not None or self.client is not None):
                raise ValueError('Stop the current session before starting another operation.')
            self.busy = True
            self.error = ''
        def work():
            try:
                self.perform(action, values)
            except Exception as error:
                self.error = str(error)
                if action in ('host', 'resume', 'join', 'wait', 'play'):
                    try:
                        self.stop()
                    except Exception as cleanup:
                        self.error += f' Cleanup also failed: {cleanup}'
            finally:
                self.busy = False
        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def progress(self, message: str):
        self.message = message

    def perform(self, action: str, values: dict):
        if action == 'return-menu':
            self.stop()
            self.perform('play', {})
            return
        if action == 'stop':
            self.stop()
            return
        installation = discover(values.get('game', ''))
        if sys.platform == 'linux':
            proton_paths(installation)
        if action == 'prepare':
            prepare(installation, self.data, self.payload, self.progress)
            bootstrap(installation, self.data, self.headless, self.progress)
            return
        if not (self.data / 'prepared.json').is_file():
            raise ValueError('Prepare Root before hosting or joining.')
        if not all((self.data / role / 'bindings-ready').exists() for role in ('host', 'client')):
            raise ValueError('First-run preparation has not completed. Click Prepare my game before starting a match.')
        if not self.prepared():
            raise ValueError('These game copies use a different mod package. Start the launcher with --data pointing to a new folder and prepare it. Existing saves remain in this folder.')
        native_host = action in ('host-native', 'resume-native')
        if native_host and (not self.native_menu or self.client is None or self.host is not None):
            raise ValueError('Open the six-player game menu before starting a match.')
        if action in ('host', 'resume', 'host-native', 'resume-native'):
            transport = 'steam' if native_host else values.get('transport', 'steam')
            if transport not in ('steam', 'cloudflare'):
                raise ValueError('Choose Steam or Cloudflare networking.')
            if transport == 'cloudflare':
                url = relay_url(values.get('relay', ''))
                key = values.get('key', '')
                if not key:
                    raise ValueError('Enter the relay host key. Guests only need their seat invitation.')
            saves = self.data / 'saves'
            saves.mkdir(exist_ok=True)
            if action in ('resume', 'resume-native'):
                filename = values.get('save', '')
                if not filename or Path(filename).name != filename or not (saves / filename).is_file():
                    raise ValueError('Select an existing saved match.')
            else:
                filename = datetime.now().strftime('%Y-%m-%d_%H-%M-%S_') + secrets.token_hex(3) + '.json'
            self.save = filename
            setup_file = self.data / 'host/match-setup.json'
            if action in ('host', 'host-native') and values.get('setup'):
                setup = json.loads(values['setup'])
                if not isinstance(setup, dict):
                    raise ValueError('Match settings must be an object.')
                setup_file.write_text(json.dumps(setup))
            else:
                setup_file.unlink(missing_ok=True)
            self.progress('Starting the host. First launch may take several minutes…')
            self.host = GameProcess(installation, self.data / 'host', 'server', headless=self.headless,
                                    save=saves / filename, resume=action in ('resume', 'resume-native'))
            self.endpoint = self.host.endpoint()
            connection = {'port': self.endpoint['port'], 'token': self.endpoint['tokens'][0]}
            if transport == 'steam':
                # Exclude the authority's shutdown credential from the graphical Steam process.
                config = {'port': self.endpoint['port'], 'tokens': self.endpoint['tokens']}
                if 'setup' in self.endpoint:
                    config['setup'] = self.endpoint['setup']
                (self.data / 'client/steam-config.json').write_text(json.dumps(config))
                mode = 'steam-host'
                self.progress('Opening your chosen faction and Steam networking. Invite your friends from the game.')
            else:
                self.host_relay = HostRelay(url, key, self.endpoint['port'], self.endpoint['tokens'])
                self.invitations = self.host_relay.invitations
                mode = 'client'
                self.progress('Host ready. Opening your chosen faction. Share one invitation per friend. Sessions last up to 12 hours.')
        elif action == 'join':
            text = values.get('invite', '')
            if text.startswith('root6:'):
                (self.data / 'client/steam-config.json').write_text(json.dumps({'invite': validate_invite(text)}))
                self.progress('Opening your seat through Steam networking. Keep Steam signed in.')
                self.client = GameProcess(installation, self.data / 'client', 'steam-client', headless=self.headless)
                return
            invite = parse_invite(text)
            self.client_relay = ClientRelay(invite)
            connection = {'port': self.client_relay.port, 'token': invite['token']}
            # Reject stale invitations before starting another heavy game process.
            reply = json.loads(exchange(connection['port'], json.dumps({'op': 'join', 'token': connection['token']}).encode()))
            if reply.get('ok') is not True:
                raise ValueError(f'Cannot join: {reply.get("error", "host rejected the invitation")}')
            mode = 'client'
            self.progress(f'Opening seat {invite["seat"]}. First launch may take several minutes…')
        elif action in ('wait', 'play'):
            self.progress('Opening Root to receive a Steam invitation. Wait for Ready in the game, then accept your host’s invitation through Steam.')
            self.native_menu = action == 'play'
            self.client = GameProcess(installation, self.data / 'client', 'steam-menu' if self.native_menu else 'steam-wait', headless=self.headless)
            return
        else:
            raise ValueError('Unknown launcher action.')
        (self.data / 'client/connection.json').write_text(json.dumps(connection))
        if native_host:
            return
        self.client = GameProcess(installation, self.data / 'client', mode, headless=self.headless)

    def stop(self):
        errors = []
        # Stop request sources before gracefully shutting down the native host.
        for attribute in ('client', 'client_relay', 'host_relay'):
            resource = getattr(self, attribute)
            if resource is not None:
                try:
                    resource.close()
                    setattr(self, attribute, None)
                except Exception as error:
                    errors.append(str(error))
        if self.host is not None:
            if self.endpoint is not None and self.host.process.poll() is None:
                try:
                    exchange(self.endpoint['port'], json.dumps({'op': 'shutdown', 'token': self.endpoint['controlToken']}).encode())
                    self.host.process.wait(timeout=10)
                except (OSError, ValueError, TimeoutError, subprocess.TimeoutExpired):
                    pass
            try:
                self.host.close()
                self.host = None
            except Exception as error:
                errors.append(str(error))
        self.invitations = []
        self.endpoint = None
        self.native_menu = False
        for name in ('steam-config.json', 'steam-status.json'):
            (self.data / 'client' / name).unlink(missing_ok=True)
        (self.data / 'host/match-setup.json').unlink(missing_ok=True)
        self.progress('Session stopped. Saved matches are kept. Resume creates new invitations.')
        if errors:
            raise RuntimeError('Some session processes could not be stopped: ' + '; '.join(errors))
