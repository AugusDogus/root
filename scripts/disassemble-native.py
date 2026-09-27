#!/usr/bin/env python3
"""Inspect selected IL2CPP methods using NativeApi --addresses and Capstone."""
import argparse
from bisect import bisect_right
from collections import defaultdict
from pathlib import Path
import struct
import sys

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import require_test_budget


def main():
    require_test_budget()
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('filters', nargs='+')
    parser.add_argument('--map', type=Path, default=PROJECT / '.lab/native-ui-methods.tsv')
    parser.add_argument('--binary', type=Path, default=PROJECT / '.lab/game/GameAssembly.dll')
    args = parser.parse_args()
    methods = defaultdict(list)
    for line in args.map.read_text().splitlines():
        address, assembly, name = line.split('\t', 2)
        methods[int(address, 16)].append(assembly + ': ' + name)
    addresses = sorted(methods)
    content = args.binary.read_bytes()
    pe = struct.unpack_from('<I', content, 0x3c)[0]
    if content[pe:pe + 4] != b'PE\0\0' or struct.unpack_from('<H', content, pe + 24)[0] != 0x20b:
        raise ValueError('Expected a PE32+ image')
    count = struct.unpack_from('<H', content, pe + 6)[0]
    section_table = pe + 24 + struct.unpack_from('<H', content, pe + 20)[0]
    base = struct.unpack_from('<Q', content, pe + 48)[0]
    sections = [struct.unpack_from('<IIII', content, section_table + i * 40 + 8) for i in range(count)]
    disassembler = Cs(CS_ARCH_X86, CS_MODE_64)
    for address, names in methods.items():
        if not any(term.lower() in name.lower() for term in args.filters for name in names):
            continue
        section = next((s for s in sections if s[1] <= address < s[1] + s[2]), None)
        if section is None:
            raise ValueError(f'Method RVA {address:x} is outside file-backed PE sections')
        _, rva, size, offset = section
        next_index = bisect_right(addresses, address)
        end = min(addresses[next_index] if next_index < len(addresses) else rva + size, rva + size)
        data = content[offset + address - rva:offset + end - rva]
        selected = [name for name in names if any(term.lower() in name.lower() for term in args.filters)]
        print('\nMETHOD ' + ' | '.join(selected))
        for instruction in disassembler.disasm(data, base + address):
            annotation = ''
            if instruction.mnemonic in ('call', 'jmp') and instruction.op_str.startswith('0x'):
                target = int(instruction.op_str, 16) - base
                if target in methods:
                    aliases = methods[target]
                    annotation = ' ; ' + ' | '.join(aliases[:3])
                    if len(aliases) > 3:
                        annotation += f' | ({len(aliases)} shared-address aliases; not a unique call target)'
            print(f'{instruction.address - base:x}\t{instruction.mnemonic} {instruction.op_str}{annotation}')


if __name__ == '__main__':
    main()
