#!/usr/bin/env python3
"""Analyze spawn loss in reliable-review3 logs - post compression fix"""

import re
from collections import defaultdict

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review3\99792-gonet-2025-12-02.log"
    server_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review3\68056-gonet-2025-12-02.log"

    # Extract client-sent spawns with their firstBytes and frame numbers
    client_spawns = {}
    relay_pattern = re.compile(r'\(frame:(\d+)/([0-9.]+)s\).*\[SPAWN-RELAY\].*GONetId=(\d+).*bytes=(\d+).*firstBytes=([A-F0-9]+)')

    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = relay_pattern.search(line)
            if match:
                frame = int(match.group(1))
                time = float(match.group(2))
                gid = int(match.group(3))
                bytes_count = int(match.group(4))
                first_bytes = match.group(5)
                client_spawns[gid] = {
                    'frame': frame,
                    'time': time,
                    'bytes': bytes_count,
                    'firstBytes': first_bytes
                }

    print(f"Client sent {len(client_spawns)} unique spawn events")

    # Extract server-deserialized spawns
    server_spawns = {}
    deser_pattern = re.compile(r'\[SPAWN-DESER\].*GONetId=(\d+).*firstBytes=([A-F0-9]+)')

    with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = deser_pattern.search(line)
            if match:
                gid = int(match.group(1))
                first_bytes = match.group(2)
                server_spawns[gid] = {'firstBytes': first_bytes}

    print(f"Server deserialized {len(server_spawns)} unique spawn events")

    # Find missing spawns
    missing = set(client_spawns.keys()) - set(server_spawns.keys())
    print(f"\nMissing spawns: {len(missing)}")

    if not missing:
        print("\n✅ All spawns received!")
        return

    print("\nMissing GONetIds and their firstBytes:")
    for gid in sorted(missing):
        info = client_spawns[gid]
        mod1024 = gid % 1024
        raw_id = gid >> 10
        print(f"  GONetId={gid:8d} (raw={raw_id:5d}, mod1024={mod1024}) frame={info['frame']}, bytes={info['bytes']}")
        print(f"    firstBytes: {info['firstBytes']}")

    # Check if missing spawn firstBytes appear anywhere in server log
    print("\n--- Checking if missing spawn bytes appear in server RECV-MSG ---")

    with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
        server_lines = f.readlines()

    for gid in sorted(missing)[:5]:  # Check first 5
        info = client_spawns[gid]
        first_bytes = info['firstBytes']

        # Search for the spawn event header (0D0D0000 or 0D0D0400) followed by GONetId bytes
        # GONetId is at offset 4-7 in the spawn event
        gid_bytes_le = f"{gid:08X}"
        # Convert to little-endian format
        gid_le = gid_bytes_le[6:8] + gid_bytes_le[4:6] + gid_bytes_le[2:4] + gid_bytes_le[0:2]

        found_in_recv = False
        found_in_deser = False

        for line in server_lines:
            if first_bytes[:8] in line:  # Check first 8 chars of firstBytes
                if 'RECV-MSG' in line:
                    found_in_recv = True
                if 'SPAWN-DESER' in line:
                    found_in_deser = True

        print(f"\n  GONetId={gid}: firstBytes pattern {first_bytes[:16]}...")
        print(f"    In RECV-MSG: {found_in_recv}")
        print(f"    In SPAWN-DESER: {found_in_deser}")

    # Analyze frames with missing spawns - check if other spawns in same frame made it
    print("\n--- Analyzing frames with missing spawns ---")

    missing_by_frame = defaultdict(list)
    for gid in missing:
        frame = client_spawns[gid]['frame']
        missing_by_frame[frame].append(gid)

    for frame, gids in sorted(missing_by_frame.items()):
        # Count how many spawns in this frame were sent vs received
        frame_spawns = [gid for gid, info in client_spawns.items() if info['frame'] == frame]
        frame_received = [gid for gid in frame_spawns if gid in server_spawns]

        print(f"\n  Frame {frame}: {len(frame_received)}/{len(frame_spawns)} received")
        print(f"    Missing: {gids}")

        # Show all spawns in this frame for comparison
        print(f"    All spawns in frame:")
        for gid in sorted(frame_spawns):
            status = "RECEIVED" if gid in server_spawns else "MISSING"
            info = client_spawns[gid]
            print(f"      {gid}: {status}, bytes={info['bytes']}, firstBytes={info['firstBytes'][:16]}...")

if __name__ == "__main__":
    main()
