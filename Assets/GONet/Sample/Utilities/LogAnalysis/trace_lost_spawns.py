#!/usr/bin/env python3
"""Deep trace of lost spawns through client reliable layer"""

import re

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review2\47240-gonet-2025-12-02.log"

    lost_spawns = [57343, 74751, 82943, 115711, 145407]  # First 5

    # Get timing for lost spawns from SPAWN-RELAY
    spawn_timings = {}
    relay_pattern = re.compile(r'\(frame:(\d+)/([\d.]+)s\).*\[SPAWN-RELAY\].*GONetId=(\d+).*bytes=(\d+)')
    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = relay_pattern.search(line)
            if match:
                gid = int(match.group(3))
                if gid in lost_spawns:
                    spawn_timings[gid] = {
                        'frame': int(match.group(1)),
                        'time': float(match.group(2)),
                        'bytes': int(match.group(4))
                    }

    print("Lost spawn timings from SPAWN-RELAY:")
    for gid in lost_spawns:
        info = spawn_timings.get(gid, {})
        print(f"  GONetId={gid}: frame={info.get('frame')}, time={info.get('time')}s, bytes={info.get('bytes')}")

    # Check SPAWN-TRANSPORT for same spawns
    transport_count = {}
    transport_pattern = re.compile(r'\(frame:(\d+)/([\d.]+)s\).*\[SPAWN-TRANSPORT\].*bytes=(\d+)')
    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = transport_pattern.search(line)
            if match:
                frame = int(match.group(1))
                bytes_count = int(match.group(3))
                for gid, info in spawn_timings.items():
                    if info['frame'] == frame and info['bytes'] == bytes_count:
                        transport_count[gid] = transport_count.get(gid, 0) + 1

    print("\nSPAWN-TRANSPORT matches (by frame+bytes):")
    for gid in lost_spawns:
        count = transport_count.get(gid, 0)
        print(f"  GONetId={gid}: {count} matches")

    # Now let's look for RELIABLE-SEQ entries near lost spawn timestamps
    print("\n--- Checking RELIABLE-SEQ entries near lost spawn times ---")
    seq_pattern = re.compile(r'\(frame:(\d+)/([\d.]+)s\).*\[RELIABLE-SEQ\].*seq=(\d+).*bytes=(\d+)')

    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        lines = f.readlines()

    for gid in lost_spawns:
        info = spawn_timings.get(gid, {})
        target_time = info.get('time', 0)
        target_bytes = info.get('bytes', 0)

        print(f"\n  GONetId={gid} (time={target_time}s, bytes={target_bytes}):")
        found = False
        for line in lines:
            match = seq_pattern.search(line)
            if match:
                seq_time = float(match.group(2))
                seq_bytes = int(match.group(4))
                seq_num = int(match.group(3))
                # Look for entries within 0.1s of spawn time with matching bytes
                if abs(seq_time - target_time) < 0.1 and seq_bytes == target_bytes:
                    print(f"    RELIABLE-SEQ seq={seq_num} at time={seq_time}s, bytes={seq_bytes}")
                    found = True
        if not found:
            print(f"    NO RELIABLE-SEQ found with bytes={target_bytes} near time={target_time}s")

if __name__ == "__main__":
    main()
