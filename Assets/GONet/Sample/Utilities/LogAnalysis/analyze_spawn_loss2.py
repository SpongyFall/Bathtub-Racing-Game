#!/usr/bin/env python3
"""Analyze spawn loss in reliable-review2 logs"""

import re
import sys

def extract_gonetids(log_path, pattern):
    """Extract GONetIds matching pattern from log file"""
    ids = set()
    regex = re.compile(pattern + r'.*GONetId=(\d+)')
    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = regex.search(line)
            if match:
                ids.add(int(match.group(1)))
    return ids

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review2\47240-gonet-2025-12-02.log"
    server_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review2\68256-gonet-2025-12-02.log"

    # Extract client-sent spawns
    client_sent = extract_gonetids(client_log, r'\[SPAWN-RELAY\]')
    print(f"Client sent {len(client_sent)} unique spawn events")

    # Extract server-deserialized spawns
    server_deser = extract_gonetids(server_log, r'\[SPAWN-DESER\]')
    print(f"Server deserialized {len(server_deser)} unique spawn events")

    # Find missing spawns (client sent but server didn't deser)
    missing = client_sent - server_deser
    print(f"\nMissing spawns (client sent, server didn't receive): {len(missing)}")

    if missing:
        print("\nMissing GONetIds:")
        for gid in sorted(missing):
            mod1024 = gid % 1024
            raw_id = gid >> 10
            print(f"  {gid:8d}  (raw={raw_id:5d}, mod1024={mod1024})")

    # Check if all missing have same mod1024 pattern
    if missing:
        mods = set(gid % 1024 for gid in missing)
        print(f"\nmod1024 values of missing spawns: {mods}")

    # Extract firstBytes for missing spawns
    print("\n--- firstBytes for missing spawns ---")
    firstbytes_pattern = re.compile(r'\[SPAWN-RELAY\].*GONetId=(\d+).*firstBytes=([A-F0-9]+)')
    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = firstbytes_pattern.search(line)
            if match:
                gid = int(match.group(1))
                if gid in missing:
                    fb = match.group(2)
                    print(f"  GONetId={gid}, firstBytes={fb}")

if __name__ == "__main__":
    main()
