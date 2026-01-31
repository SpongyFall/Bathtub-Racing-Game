#!/usr/bin/env python3
"""
Trace GONetId lifecycle - registration, assignment, and data flow.
Looking for mismatch between client registration and server sync.
"""

import sys
import re
from collections import defaultdict

def main():
    if len(sys.argv) < 2:
        print("Usage: python trace_gonetid_lifecycle.py <main_gonet_log_path>")
        sys.exit(1)

    log_path = sys.argv[1]

    # Track events per GONetId
    registration_events = defaultdict(list)  # GONetId -> [(line, event_type, details)]

    # Patterns to search
    patterns = {
        'SoA-SEED': re.compile(r'\[SoA-SEED\].*GONetId=(\d+)'),
        'SoA-REG': re.compile(r'\[SoA\].*GONetId (\d+)'),
        'GONetId-assign': re.compile(r'GONetId.*=.*(\d{4,})'),  # 4+ digit numbers likely GONetIds
        'spawn': re.compile(r'[Ss]pawn.*GONetId.*(\d+)'),
        'instantiate': re.compile(r'[Ii]nstantiate.*GONetId.*(\d+)'),
    }

    # Stuck GONetIds we're investigating
    stuck_gids = {4095, 29695, 209919, 118783, 129023, 139263}

    # Related raw IDs (same raw as stuck, different owner)
    related_gids = set()
    for gid in stuck_gids:
        raw = gid >> 10
        # Client-owned version
        related_gids.add((raw << 10) | 1)
        # Server-owned version
        related_gids.add((raw << 10) | 1023)

    print(f"Analyzing log: {log_path}")
    print(f"Stuck GONetIds: {stuck_gids}")
    print(f"Related GONetIds (same raw): {related_gids}")
    print("-" * 60)

    line_count = 0
    matches_found = 0

    with open(log_path, 'r', errors='ignore') as f:
        for line_num, line in enumerate(f, 1):
            line_count += 1

            # Check for any stuck GONetIds mentioned
            for gid in stuck_gids:
                if str(gid) in line:
                    print(f"[STUCK] Line {line_num}: {line.strip()[:200]}")
                    matches_found += 1

            # Check for related GONetIds
            for gid in related_gids:
                if str(gid) in line and gid not in stuck_gids:
                    # Only show first few to avoid spam
                    if matches_found < 50:
                        print(f"[RELATED] Line {line_num}: GONetId {gid} - {line.strip()[:200]}")
                        matches_found += 1

    print(f"\nScanned {line_count} lines, found {matches_found} matches")

if __name__ == "__main__":
    main()
