"""Bounded loopback requests to the local native rules host."""
import socket

MAX_RESPONSE = 4 * 1024 * 1024


def exchange(port: int, body: bytes) -> bytes:
    with socket.create_connection(('127.0.0.1', port), timeout=5) as client:
        client.sendall(body + b'\n')
        with client.makefile('rb') as stream:
            response = stream.readline(MAX_RESPONSE + 1)
        if len(response) > MAX_RESPONSE or not response.endswith(b'\n'):
            raise ValueError('Native host returned an incomplete or oversized reply.')
        return response.rstrip(b'\n')
