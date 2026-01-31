#!/usr/bin/env python3
"""
Comprehensive analysis of stuck objects in GONet.
Traces the lifecycle of objects from spawn to blend to understand why they get stuck.
"""

import sys
import re
from collections import defaultdict

def decode_gonetid(gonetid):
    """Decode GONetId into raw and owner components."""
    raw = gonetid >> 10
    owner = gonetid & 1023
    owner_str = "SVR" if owner == 1023 else f"CLI{owner}"
    return raw, owner, owner_str

def main():
    if len(sys.argv) < 3:
        print("Usage: python analyze_stuck_objects.py <main_log_path> <blenddiag_log_path>")
        sys.exit(1)

    main_log = sys.argv[1]
    blend_log = sys.argv[2]

    # Track events per GONetId
    spawns = {}  # GONetId -> {time, raw, owner, role}
    soa_seeds = {}  # GONetId -> {time, role}
    soa_regs = defaultdict(list)  # GONetId -> [(time, role)]
    data_in_counts = defaultdict(int)  # GONetId -> count
    blend_info = defaultdict(list)  # GONetId -> [(time, validCount, role)]

    # Track objects by raw ID to find mismatches
    raw_id_map = defaultdict(list)  # raw -> [(GONetId, source)]

    print(f"Analyzing main log: {main_log}")
    print("=" * 70)

    # Parse main log
    with open(main_log, 'r', errors='ignore') as f:
        for line_num, line in enumerate(f, 1):
            # Parse role from log prefix like [Server] or [Client:1]
            role_match = re.search(r'\[(Server|Client:\d+)\]', line)
            role = role_match.group(1) if role_match else "Unknown"

            # Extract elapsed time
            time_match = re.search(r'\(frame:\d+/(\d+\.?\d*)s\)', line)
            elapsed = float(time_match.group(1)) if time_match else 0

            # SoA-SEED events
            seed_match = re.search(r'\[SoA-SEED\].*GONetId=(\d+)', line)
            if seed_match:
                gid = int(seed_match.group(1))
                raw, owner, owner_str = decode_gonetid(gid)
                if gid not in soa_seeds:
                    soa_seeds[gid] = {'time': elapsed, 'role': role}
                raw_id_map[raw].append((gid, f"SEED-{role}"))

            # SYNC-GATE events (indicates object ready for sync)
            sync_match = re.search(r'\[SYNC-GATE\].*GONetId=(\d+)', line)
            if sync_match:
                gid = int(sync_match.group(1))
                raw, owner, owner_str = decode_gonetid(gid)
                if gid not in spawns:
                    spawns[gid] = {'time': elapsed, 'role': role}
                raw_id_map[raw].append((gid, f"SYNC-{role}"))

    # Parse blend diagnostics log
    print(f"\nAnalyzing blend log: {blend_log}")

    # Pattern: DATA_IN|SVR|frame|elapsed|POS|GONetId|...
    data_in_pattern = re.compile(r'DATA_IN\|(SVR|CLI)\|\d+\|[\d.]+\|(?:POS|ROT)\|(\d+)\|')
    # Pattern: BLEND|CLI|56425|24.1560|POS|0:0|...|validCount|...|GONetId|...
    blend_pattern = re.compile(r'BLEND\|(SVR|CLI)\|(\d+)\|([\d.]+)\|(?:POS|ROT)\|[^|]+\|[^|]+\|[^|]+\|[^|]+\|[^|]+\|(\d+)\|[^|]+\|[^|]+\|[^|]+\|(\d+)\|')

    with open(blend_log, 'r', errors='ignore') as f:
        for line_num, line in enumerate(f, 1):
            # Check DATA_IN
            match = data_in_pattern.search(line)
            if match:
                role = match.group(1)
                gid = int(match.group(2))
                data_in_counts[gid] += 1

            # Check BLEND entries
            match = blend_pattern.search(line)
            if match:
                role = match.group(1)
                frame = int(match.group(2))
                elapsed = float(match.group(3))
                valid_count = int(match.group(4))
                gid = int(match.group(5))
                blend_info[gid].append((elapsed, valid_count, role))

    # Identify stuck objects (validCount consistently low on CLIENT)
    print("\n" + "=" * 70)
    print("STUCK OBJECTS ANALYSIS (CLIENT validCount consistently <= 2)")
    print("=" * 70)

    stuck_objects = []
    for gid, entries in blend_info.items():
        cli_entries = [e for e in entries if e[2] == 'CLI']
        if len(cli_entries) >= 10:  # Need enough samples
            avg_valid = sum(e[1] for e in cli_entries) / len(cli_entries)
            if avg_valid <= 2.5:  # Stuck = consistently 2 or very close
                raw, owner, owner_str = decode_gonetid(gid)
                stuck_objects.append({
                    'gid': gid,
                    'raw': raw,
                    'owner_str': owner_str,
                    'avg_valid': avg_valid,
                    'cli_entries': len(cli_entries),
                    'data_in': data_in_counts.get(gid, 0),
                    'seeded': gid in soa_seeds,
                    'synced': gid in spawns
                })

    stuck_objects.sort(key=lambda x: x['avg_valid'])

    print(f"\nFound {len(stuck_objects)} stuck objects")
    print("-" * 70)
    print(f"{'GONetId':>10} {'Raw':>6} {'Owner':>5} {'DataIn':>8} {'AvgValid':>8} {'Seeded':>7} {'Synced':>7}")
    print("-" * 70)

    for obj in stuck_objects[:30]:
        print(f"{obj['gid']:>10} {obj['raw']:>6} {obj['owner_str']:>5} {obj['data_in']:>8} {obj['avg_valid']:>8.2f} {str(obj['seeded']):>7} {str(obj['synced']):>7}")

    # Summary
    print("\n" + "=" * 70)
    print("SUMMARY")
    print("=" * 70)

    stuck_no_data_in = sum(1 for obj in stuck_objects if obj['data_in'] == 0)
    stuck_not_seeded = sum(1 for obj in stuck_objects if not obj['seeded'])

    print(f"Total stuck objects: {len(stuck_objects)}")
    print(f"  - No DATA_IN: {stuck_no_data_in}")
    print(f"  - Not seeded on CLIENT: {stuck_not_seeded}")
    print(f"  - Have DATA_IN but still stuck: {len(stuck_objects) - stuck_no_data_in}")

    # Check which role seeded the stuck objects
    svr_seeded = sum(1 for obj in stuck_objects if obj['seeded'] and soa_seeds[obj['gid']]['role'] == 'Server')
    cli_seeded = sum(1 for obj in stuck_objects if obj['seeded'] and 'Client' in soa_seeds.get(obj['gid'], {}).get('role', ''))
    
    print(f"\nSeeded on SERVER (not CLIENT): {svr_seeded}")
    print(f"Seeded on CLIENT: {cli_seeded}")

if __name__ == "__main__":
    main()
