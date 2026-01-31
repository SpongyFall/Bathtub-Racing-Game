#!/usr/bin/env python3
"""Trace the exact point of corruption for missing spawns"""

import re
from collections import defaultdict

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review4\25684-gonet-2025-12-02.log"
    server_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review4\100224-gonet-2025-12-02.log"

    # Known missing GONetIds from previous analysis
    missing_gids = [27647, 110591, 121855, 132095, 157695]  # First few

    print("=== Tracing corruption path for missing GONetIds ===\n")

    # For each missing GONetId, trace:
    # 1. Client SPAWN-RELAY (shows unique firstBytes)
    # 2. Client RELIABLE-SEQ (shows possibleGONetId - may be compression header)
    # 3. Server RELIABLE-DELIVER (shows msgSeq)
    # 4. Server SPAWN-DESER (should show GONetId - but missing!)

    for gid in missing_gids[:3]:
        print(f"\n{'='*60}")
        print(f"GONetId {gid}:")
        print(f"{'='*60}")

        # Convert GONetId to expected hex pattern (little-endian)
        gid_bytes_le = f"{gid & 0xFFFF:04X}"  # Lower 16 bits
        gid_hex_le = gid_bytes_le[2:4] + gid_bytes_le[0:2]  # Swap bytes for little-endian
        gid_full = f"FF{gid_hex_le}00"
        print(f"Expected firstBytes pattern: 0D0D0000{gid_full} or 0D0D0400{gid_full}")

        # Search client log for this GONetId
        with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
            for line in f:
                if f"GONetId={gid}," in line or f"GONetId={gid} " in line:
                    if "SPAWN-RELAY" in line:
                        print(f"\n[CLIENT SPAWN-RELAY]")
                        # Extract relevant parts
                        match = re.search(r'\(frame:(\d+)/([0-9.]+)s\).*firstBytes=([A-F0-9]+)', line)
                        if match:
                            print(f"  Frame: {match.group(1)}, Time: {match.group(2)}s")
                            print(f"  firstBytes: {match.group(3)}")
                    elif "RELIABLE-SEQ" in line:
                        print(f"\n[CLIENT RELIABLE-SEQ]")
                        match = re.search(r'seq=(\d+).*bytes=(\d+)', line)
                        if match:
                            print(f"  seq={match.group(1)}, bytes={match.group(2)}")

        # Search server log for this GONetId
        with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
            for line in f:
                if f"GONetId={gid}," in line or f"GONetId={gid} " in line:
                    if "SPAWN-DESER" in line:
                        print(f"\n[SERVER SPAWN-DESER] FOUND!")
                        print(f"  Line: {line.strip()[:200]}")
                    elif "SPAWN-RECV" in line:
                        print(f"\n[SERVER SPAWN-RECV] FOUND!")

        # Also search for the expected firstBytes pattern on server
        expected_patterns = [
            f"0D0D0000{gid_full[:8]}",
            f"0D0D0400{gid_full[:8]}"
        ]
        with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
            content = f.read()
            for pattern in expected_patterns:
                if pattern in content:
                    print(f"\n[SERVER CONTENT] Pattern '{pattern}' FOUND in server log")
                    # Find the exact line
                    for line in content.split('\n'):
                        if pattern in line and len(line) < 500:
                            print(f"  In: {line[:200]}")
                            break
                else:
                    print(f"\n[SERVER CONTENT] Pattern '{pattern}' NOT FOUND in server log")

    # Analyze the gap between what client sent vs what server received
    print(f"\n\n{'='*60}")
    print("=== Comparing client SPAWN-RELAY vs server SPAWN-DESER firstBytes ===")
    print(f"{'='*60}\n")

    client_spawns = {}
    relay_pattern = re.compile(r'\[SPAWN-RELAY\].*GONetId=(\d+).*firstBytes=([A-F0-9]+)')

    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = relay_pattern.search(line)
            if match:
                gid = int(match.group(1))
                fb = match.group(2)
                client_spawns[gid] = fb

    server_spawns = {}
    deser_pattern = re.compile(r'\[SPAWN-DESER\].*GONetId=(\d+).*firstBytes=([A-F0-9]+)')

    with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = deser_pattern.search(line)
            if match:
                gid = int(match.group(1))
                fb = match.group(2)
                server_spawns[gid] = fb

    # Compare firstBytes
    mismatches = []
    for gid in client_spawns:
        if gid in server_spawns:
            if client_spawns[gid] != server_spawns[gid]:
                mismatches.append((gid, client_spawns[gid], server_spawns[gid]))

    if mismatches:
        print(f"Found {len(mismatches)} firstBytes mismatches:")
        for gid, client_fb, server_fb in mismatches[:10]:
            print(f"  GONetId={gid}:")
            print(f"    Client: {client_fb}")
            print(f"    Server: {server_fb}")
    else:
        print("All matching GONetIds have identical firstBytes between client and server!")

    missing = set(client_spawns.keys()) - set(server_spawns.keys())
    print(f"\nMissing GONetIds (sent by client, not deserialized by server): {len(missing)}")
    for gid in sorted(missing)[:10]:
        print(f"  {gid}: client firstBytes = {client_spawns[gid][:24]}...")

if __name__ == "__main__":
    main()
