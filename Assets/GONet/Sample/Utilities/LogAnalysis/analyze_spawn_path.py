#!/usr/bin/env python3
"""Trace lost spawns through the complete path"""

import re

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review2\47240-gonet-2025-12-02.log"
    server_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review2\68256-gonet-2025-12-02.log"

    lost_spawns = [57343, 74751, 82943, 115711, 145407, 168959, 189439, 201727,
                   285695, 295935, 306175, 316415, 326655, 344063, 355327, 375807]

    # Get firstBytes for each lost spawn
    lost_firstbytes = {}
    fb_pattern = re.compile(r'\[SPAWN-RELAY\].*GONetId=(\d+).*firstBytes=([A-F0-9]+)')
    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = fb_pattern.search(line)
            if match:
                gid = int(match.group(1))
                if gid in lost_spawns:
                    lost_firstbytes[gid] = match.group(2)

    print("Lost spawn firstBytes (from client SPAWN-RELAY):")
    for gid in lost_spawns:
        fb = lost_firstbytes.get(gid, "NOT FOUND")
        print(f"  GONetId={gid}: {fb}")

    # Extract just the first 8 bytes for searching (more reliable matching)
    short_patterns = {gid: fb[:16] for gid, fb in lost_firstbytes.items()}

    print("\n--- Searching for lost spawns in server log ---")

    # Read server log once and search for patterns
    with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
        server_lines = f.readlines()

    for gid in lost_spawns[:5]:  # Check first 5
        pattern = short_patterns.get(gid, "")
        if not pattern:
            print(f"  GONetId={gid}: No firstBytes pattern")
            continue

        found_in = []
        for i, line in enumerate(server_lines):
            if pattern in line:
                # Extract tag
                if '[RELIABLE-RECV-MSG]' in line:
                    found_in.append('RELIABLE-RECV-MSG')
                elif '[RELIABLE-DELIVER]' in line:
                    found_in.append('RELIABLE-DELIVER')
                elif '[RECV-MSG]' in line:
                    found_in.append('RECV-MSG')
                elif '[DESER-ENTRY]' in line:
                    found_in.append('DESER-ENTRY')
                elif '[SPAWN-DESER]' in line:
                    found_in.append('SPAWN-DESER')
                else:
                    found_in.append(f'line {i}')

        if found_in:
            print(f"  GONetId={gid} ({pattern}): FOUND in {', '.join(found_in)}")
        else:
            print(f"  GONetId={gid} ({pattern}): NOT FOUND in server log")

    # Now let's look at what byte patterns ARE in SPAWN-DESER vs SPAWN-RELAY
    print("\n--- Cross-reference: Check if lost spawns appear anywhere on server ---")

    # Get all GONetIds that appear in server SPAWN-DESER
    server_spawn_ids = set()
    spawn_deser_pattern = re.compile(r'\[SPAWN-DESER\].*GONetId=(\d+)')
    for line in server_lines:
        match = spawn_deser_pattern.search(line)
        if match:
            server_spawn_ids.add(int(match.group(1)))

    for gid in lost_spawns:
        status = "MISSING" if gid not in server_spawn_ids else "FOUND"
        print(f"  GONetId={gid}: {status} in SPAWN-DESER")

if __name__ == "__main__":
    main()
