#!/usr/bin/env python3
"""Deep analysis of spawn loss in reliable-review4 logs - investigate buffer aliasing"""

import re
from collections import defaultdict

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review4\25684-gonet-2025-12-02.log"
    server_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-review4\100224-gonet-2025-12-02.log"

    # Extract client-sent spawns with their firstBytes
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

    print(f"Client sent {len(client_spawns)} unique spawn events via SPAWN-RELAY")

    # Extract server-received spawn events with their firstBytes from RECV-MSG
    recv_msg_pattern = re.compile(r'\(frame:(\d+)/([0-9.]+)s\).*\[RECV-MSG\].*bytes=(\d+).*firstBytes=([A-F0-9]+)')

    server_recv_spawns = []
    with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = recv_msg_pattern.search(line)
            if match:
                frame = int(match.group(1))
                time = float(match.group(2))
                bytes_count = int(match.group(3))
                first_bytes = match.group(4)
                if bytes_count in [89, 105, 110]:  # Common spawn-sized messages
                    server_recv_spawns.append({
                        'frame': frame,
                        'time': time,
                        'bytes': bytes_count,
                        'firstBytes': first_bytes
                    })

    print(f"Server received {len(server_recv_spawns)} spawn-sized RECV-MSG entries")

    # Analyze firstBytes patterns in server receives
    firstBytes_counts = defaultdict(int)
    for recv in server_recv_spawns:
        firstBytes_counts[recv['firstBytes']] += 1

    print(f"\n=== Server RECV-MSG firstBytes patterns ===")
    for fb, count in sorted(firstBytes_counts.items(), key=lambda x: -x[1])[:15]:
        # Decode the firstBytes
        # Format: channel(1) + size(4) + compression_header(4) + spawn_data(...)
        # FB = channel=06, size=XX XX XX XX, comp_hdr=XX XX XX XX, data=0D0D...
        channel = int(fb[0:2], 16)
        size = int(fb[6:8] + fb[4:6] + fb[2:4], 16)  # little-endian 4 bytes

        # Skip compression header (next 8 chars = 4 bytes)
        spawn_data_start = fb[18:]  # After channel(2) + size(8) + comp_header(8) = 18 chars

        print(f"  {fb}: {count}x (channel={channel}, size={size}, spawn_start={spawn_data_start})")

    # Now extract SPAWN-DESER to see what spawns actually got deserialized
    server_spawns_deser = {}
    deser_pattern = re.compile(r'\[SPAWN-DESER\].*GONetId=(\d+).*firstBytes=([A-F0-9]+)')

    with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            match = deser_pattern.search(line)
            if match:
                gid = int(match.group(1))
                first_bytes = match.group(2)
                server_spawns_deser[gid] = {'firstBytes': first_bytes}

    print(f"\nServer deserialized {len(server_spawns_deser)} unique spawn events")

    # Find missing spawns
    missing = set(client_spawns.keys()) - set(server_spawns_deser.keys())
    print(f"\nMissing spawns: {len(missing)}")

    if not missing:
        print("\n✅ All spawns received!")
        return

    # Group by time to see which spawns in the same batch got lost
    print("\n=== Missing spawn details ===")
    by_frame = defaultdict(list)
    for gid in missing:
        by_frame[client_spawns[gid]['frame']].append(gid)

    for frame in sorted(by_frame.keys())[:5]:
        gids = by_frame[frame]
        print(f"\nFrame {frame} - {len(gids)} missing:")

        # Show all spawns in this frame
        all_spawns_in_frame = [(gid, info) for gid, info in client_spawns.items() if info['frame'] == frame]
        all_spawns_in_frame.sort(key=lambda x: x[0])

        for gid, info in all_spawns_in_frame:
            status = "MISSING" if gid in missing else "OK"
            # Extract GONetId from firstBytes (offset 4 bytes in spawn data)
            # firstBytes format for 80-byte spawns: 0D0D 0000 FF XX XX XX (little endian GONetId at offset 4)
            fb = info['firstBytes']
            fb_gid_bytes = fb[8:16] if len(fb) >= 16 else "?"
            print(f"  GONetId={gid:8d}: {status}, bytes={info['bytes']}, firstBytes={fb[:20]}... (GONetId bytes: {fb_gid_bytes})")

    # Key analysis: Compare firstBytes for 80-byte spawns to see if they're unique
    print("\n=== Comparing 80-byte spawn firstBytes uniqueness ===")
    spawns_80 = {gid: info for gid, info in client_spawns.items() if info['bytes'] == 80}
    fb_to_gids = defaultdict(list)
    for gid, info in spawns_80.items():
        fb_to_gids[info['firstBytes']].append(gid)

    duplicate_fbs = {fb: gids for fb, gids in fb_to_gids.items() if len(gids) > 1}
    if duplicate_fbs:
        print(f"Found {len(duplicate_fbs)} firstBytes patterns with multiple GONetIds!")
        for fb, gids in list(duplicate_fbs.items())[:5]:
            print(f"  {fb[:24]}...: GONetIds {gids}")
    else:
        print("All 80-byte spawn firstBytes are unique (no duplicates in SPAWN-RELAY)")

if __name__ == "__main__":
    main()
