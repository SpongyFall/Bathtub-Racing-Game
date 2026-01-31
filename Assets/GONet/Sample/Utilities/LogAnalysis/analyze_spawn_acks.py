#!/usr/bin/env python3
"""Analyze if lost spawns were ACKed by server"""

import re
import sys

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review2\47240-gonet-2025-12-02.log"

    lost_spawns = [57343, 74751, 82943, 115711, 145407, 168959, 189439, 201727,
                   285695, 295935, 306175, 316415, 326655, 344063, 355327, 375807]

    # Find when each lost spawn was relayed
    spawn_times = {}
    spawn_pattern = re.compile(r'\(frame:(\d+)/([\d.]+)s\).*\[SPAWN-RELAY\].*GONetId=(\d+)')
    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = spawn_pattern.search(line)
            if match:
                gid = int(match.group(3))
                if gid in lost_spawns:
                    spawn_times[gid] = (match.group(1), match.group(2))

    print("Lost spawn timings:")
    for gid in lost_spawns:
        if gid in spawn_times:
            print(f"  GONetId={gid}: frame={spawn_times[gid][0]}, time={spawn_times[gid][1]}s")
        else:
            print(f"  GONetId={gid}: NOT FOUND")

    # Check if any reliable message containing these spawns was sent
    # By looking for RELIABLE-SEQ entries near those timestamps
    print("\nLooking for RELIABLE-SEQ near lost spawn timestamps...")

    seq_pattern = re.compile(r'\(frame:(\d+)/([\d.]+)s\).*\[RELIABLE-SEQ\].*seq=(\d+).*bytes=(\d+)')
    ack_pattern = re.compile(r'\[RELIABLE-ACK\].*msgSeqs=\[([^\]]+)\]')

    # Find all ACKed message sequences
    acked_seqs = set()
    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = ack_pattern.search(line)
            if match:
                seqs = match.group(1).split(',')
                for seq in seqs:
                    acked_seqs.add(int(seq.strip()))

    print(f"\nTotal ACKed sequences: {len(acked_seqs)}")
    print(f"ACK range: {min(acked_seqs)} to {max(acked_seqs)}")

    # Now find RELIABLE-SEQ entries with 80 bytes (Physics Cube Projectile spawns)
    seq_80_bytes = []
    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = seq_pattern.search(line)
            if match:
                bytes_count = int(match.group(4))
                if bytes_count == 80:
                    seq = int(match.group(3))
                    seq_80_bytes.append((seq, match.group(1), match.group(2)))

    print(f"\nRELIABLE-SEQ entries with 80 bytes: {len(seq_80_bytes)}")
    for seq, frame, time in seq_80_bytes[:5]:
        acked = "ACKed" if seq in acked_seqs else "NOT ACKed"
        print(f"  seq={seq}, frame={frame}, time={time}s - {acked}")

    # Check how many 80-byte messages were ACKed
    acked_80 = sum(1 for seq, _, _ in seq_80_bytes if seq in acked_seqs)
    print(f"\n80-byte messages ACKed: {acked_80}/{len(seq_80_bytes)}")

if __name__ == "__main__":
    main()
