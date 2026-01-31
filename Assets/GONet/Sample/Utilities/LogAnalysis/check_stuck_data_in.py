#!/usr/bin/env python3
"""
Check if stuck GONetIds are receiving DATA_IN events.
Parses pipe-delimited BlendDiag log format.
"""

import sys
import re
from collections import defaultdict

# Stuck GONetIds identified from BLEND entries with validCount=2, high sampleAge
STUCK_GIDS = [4095, 29695, 209919, 118783, 129023, 139263]

def main():
    if len(sys.argv) < 2:
        print("Usage: python check_stuck_data_in.py <blenddiag_log_path>")
        sys.exit(1)

    log_path = sys.argv[1]

    # Count events per GONetId
    data_in_counts = defaultdict(int)  # GONetId -> count
    soa_reg_counts = defaultdict(int)  # GONetId -> count
    blend_entries = defaultdict(list)  # GONetId -> [(line_num, validCount)]

    # Track by role (SVR/CLI)
    data_in_by_role = defaultdict(lambda: defaultdict(int))  # role -> GONetId -> count
    soa_reg_by_role = defaultdict(lambda: defaultdict(int))  # role -> GONetId -> count

    # Patterns for pipe-delimited format:
    # DATA_IN|SVR|frame|elapsed|POS|GONetId|...
    # SOA_REG|SVR|frame|elapsed|GONetId|type|...
    # BLEND|SVR|frame|elapsed|POS|streamIdx:objIdx|...|GONetId|...

    data_in_pattern = re.compile(r'DATA_IN\|(SVR|CLI)\|\d+\|[\d.]+\|(?:POS|ROT)\|(\d+)\|')
    soa_reg_pattern = re.compile(r'SOA_REG\|(SVR|CLI)\|\d+\|[\d.]+\|(\d+)\|')
    # BLEND pattern: validCount is at position 8 (0-indexed), GONetId at position 13
    # BLEND|SVR|46000|11.8374|POS|0:0|0.3232|0.107985|-0.0731|1|3|0.0269|0|0|3073|0
    blend_pattern = re.compile(r'BLEND\|(SVR|CLI)\|(\d+)\|[\d.]+\|(?:POS|ROT)\|[^|]+\|[^|]+\|[^|]+\|[^|]+\|[^|]+\|(\d+)\|[^|]+\|[^|]+\|[^|]+\|(\d+)\|')

    print(f"Analyzing log: {log_path}")
    print(f"Looking for stuck GONetIds: {STUCK_GIDS}")
    print("-" * 60)

    with open(log_path, 'r', errors='ignore') as f:
        for line_num, line in enumerate(f, 1):
            # Check DATA_IN
            match = data_in_pattern.search(line)
            if match:
                role = match.group(1)
                gid = int(match.group(2))
                data_in_counts[gid] += 1
                data_in_by_role[role][gid] += 1

            # Check SOA_REG
            match = soa_reg_pattern.search(line)
            if match:
                role = match.group(1)
                gid = int(match.group(2))
                soa_reg_counts[gid] += 1
                soa_reg_by_role[role][gid] += 1

            # Check BLEND entries
            match = blend_pattern.search(line)
            if match:
                role = match.group(1)
                frame = int(match.group(2))
                valid_count = int(match.group(3))
                gid = int(match.group(4))
                if gid in STUCK_GIDS or valid_count <= 2:  # Track stuck or potentially stuck
                    blend_entries[gid].append((line_num, valid_count, role))

    print("\n=== STUCK GONetIds ANALYSIS ===")
    for gid in STUCK_GIDS:
        raw = gid >> 10
        owner = gid & 1023
        print(f"\nGONetId {gid} (raw={raw}, owner={'SVR' if owner == 1023 else 'CLI'})")
        print(f"  DATA_IN total: {data_in_counts.get(gid, 0)}")
        print(f"    - from SVR: {data_in_by_role['SVR'].get(gid, 0)}")
        print(f"    - from CLI: {data_in_by_role['CLI'].get(gid, 0)}")
        print(f"  SOA_REG total: {soa_reg_counts.get(gid, 0)}")
        print(f"    - from SVR: {soa_reg_by_role['SVR'].get(gid, 0)}")
        print(f"    - from CLI: {soa_reg_by_role['CLI'].get(gid, 0)}")

        # Show BLEND entry history for this GONetId
        if gid in blend_entries:
            entries = blend_entries[gid]
            print(f"  BLEND entries: {len(entries)}")
            if len(entries) <= 8:
                for line_num, valid_count, role in entries:
                    print(f"    Line {line_num}: {role} validCount={valid_count}")
            else:
                for line_num, valid_count, role in entries[:4]:
                    print(f"    Line {line_num}: {role} validCount={valid_count}")
                print(f"    ... ({len(entries) - 8} more entries) ...")
                for line_num, valid_count, role in entries[-4:]:
                    print(f"    Line {line_num}: {role} validCount={valid_count}")
        else:
            print(f"  BLEND entries: 0")

    # Summary of all GONetIds with DATA_IN from CLIENT
    print("\n\n=== CLIENT DATA_IN (objects receiving network updates on client) ===")
    client_data = [(gid, count) for gid, count in data_in_by_role['CLI'].items()]
    client_data.sort(key=lambda x: -x[1])
    for gid, count in client_data[:30]:
        raw = gid >> 10
        owner = gid & 1023
        stuck_marker = " **STUCK**" if gid in STUCK_GIDS else ""
        print(f"  GONetId {gid} (raw={raw}, owner={'SVR' if owner == 1023 else 'CLI'}): {count} DATA_IN{stuck_marker}")

    if not client_data:
        print("  NONE! Client has no DATA_IN events logged.")

    # Summary of SOA_REG from CLIENT
    print("\n\n=== CLIENT SOA_REG (objects registered in SoA on client) ===")
    client_reg = [(gid, count) for gid, count in soa_reg_by_role['CLI'].items()]
    client_reg.sort(key=lambda x: -x[1])
    for gid, count in client_reg[:30]:
        raw = gid >> 10
        owner = gid & 1023
        stuck_marker = " **STUCK**" if gid in STUCK_GIDS else ""
        print(f"  GONetId {gid} (raw={raw}, owner={'SVR' if owner == 1023 else 'CLI'}): {count} SOA_REG{stuck_marker}")

    if not client_reg:
        print("  NONE! Client has no SOA_REG events logged.")

    # Look for objects with LOW validCount (potentially stuck)
    print("\n\n=== OBJECTS WITH CONSISTENTLY LOW validCount (potentially stuck) ===")
    stuck_candidates = []
    for gid, entries in blend_entries.items():
        if len(entries) >= 10:  # Only consider objects with enough history
            cli_entries = [e for e in entries if e[2] == 'CLI']
            if cli_entries:
                avg_valid = sum(e[1] for e in cli_entries) / len(cli_entries)
                if avg_valid <= 2.5:  # Low average = stuck
                    stuck_candidates.append((gid, avg_valid, len(cli_entries)))

    stuck_candidates.sort(key=lambda x: x[1])
    for gid, avg_valid, count in stuck_candidates[:20]:
        raw = gid >> 10
        owner = gid & 1023
        print(f"  GONetId {gid} (raw={raw}, owner={'SVR' if owner == 1023 else 'CLI'}): avgValidCount={avg_valid:.2f} over {count} BLEND entries")

if __name__ == "__main__":
    main()
