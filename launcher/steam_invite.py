"""Compact private Steam invitation, shared with engine-probe/SteamInvitation.cs."""
import re

from steam import BUILD


def validate_invite(text: str) -> str:
    match = re.fullmatch(r'root6:6:' + BUILD + r':([0-9]+):([0-9]+):([2-6]):([0-9a-f]{32})', text)
    if len(text) >= 256 or match is None:
        raise ValueError('This invitation is invalid or uses a different mod version. Both players should install the same release, then ask the host for a new invitation.')
    host, port = int(match[1]), int(match[2])
    if host >> 32 != 0x01100001 or host & 0xffffffff == 0 or not 100 <= port <= 999:
        raise ValueError('Invalid Steam host or virtual port. Ask the host for a new invitation.')
    return text
