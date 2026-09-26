"""Local test transport and process cleanup, without starting a game on import."""
import json
import os
import signal
import socket
import subprocess


def exchange(endpoint, payload):
    with socket.create_connection(('127.0.0.1', endpoint['port']), timeout=10) as client:
        client.sendall(json.dumps(payload).encode() + b'\n')
        with client.makefile('rb') as stream:
            return json.loads(stream.readline(4 * 1024 * 1024))


def stop(process):
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=15)
