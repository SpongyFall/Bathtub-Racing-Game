#!/usr/bin/env python3
"""
Analyze spawn event propagation across GONet logs.

This script traces the lifecycle of client-spawned server-owned objects to identify
where spawn events might be getting lost.

Expected flow for client-spawned server-owned objects:
1. [SPAWN-PROPAGATE] - Client publishes spawn event
2. [SPAWN-RELAY]     - Client relays event to server (network send)
3. [SPAWN-DESER]     - Server deserializes event (network receive)
4. [SPAWN-RECV]      - Server processes spawn event

Usage: python analyze_spawn_events.py <log_directory>
"""

import os
import re
import sys
from collections import defaultdict

def parse_gonetid(gonetid_str):
    """Parse GONetId and extract raw ID and owner authority."""
    gonetid = int(gonetid_str)
    raw_id = gonetid >> 10
    owner_authority = gonetid & 0x3FF
    return gonetid, raw_id, owner_authority

def analyze_spawn_events(log_dir):
    """Analyze spawn event propagation across logs."""

    # Track spawn events at each stage
    # Key: GONetId, Value: dict of stages seen
    spawn_lifecycle = defaultdict(lambda: {
        'propagate': False,  # Client published
        'relay': False,      # Client relayed
        'deser': False,      # Server deserialized
        'recv': False,       # Server processed
        'source_authority': None,
        'name': None,
    })

    # Patterns for each stage
    patterns = {
        'propagate': re.compile(r'\[SPAWN-PROPAGATE\].*GONetId=(\d+).*name=\'([^\']+)\''),
        'relay': re.compile(r'\[SPAWN-RELAY\].*GONetId=(\d+)'),
        'deser': re.compile(r'\[SPAWN-DESER\].*GONetId=(\d+).*From=(\d+)'),
        'recv': re.compile(r'\[SPAWN-RECV\].*GONetId=(\d+).*SourceAuthorityId=(\d+)'),
    }

    log_files = []
    for f in os.listdir(log_dir):
        if f.endswith('.log'):
            log_files.append(os.path.join(log_dir, f))

    if not log_files:
        print(f"No .log files found in {log_dir}")
        return

    print(f"Analyzing {len(log_files)} log files in {log_dir}\n")

    for log_file in sorted(log_files):
        filename = os.path.basename(log_file)
        is_server = 'server' in filename.lower() or 'srv' in filename.lower()

        with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                # Check propagate (client-side)
                m = patterns['propagate'].search(line)
                if m:
                    gonetid = int(m.group(1))
                    name = m.group(2)
                    spawn_lifecycle[gonetid]['propagate'] = True
                    spawn_lifecycle[gonetid]['name'] = name
                    continue

                # Check relay (client-side)
                m = patterns['relay'].search(line)
                if m:
                    gonetid = int(m.group(1))
                    spawn_lifecycle[gonetid]['relay'] = True
                    continue

                # Check deser (server-side)
                m = patterns['deser'].search(line)
                if m:
                    gonetid = int(m.group(1))
                    source_auth = int(m.group(2))
                    spawn_lifecycle[gonetid]['deser'] = True
                    spawn_lifecycle[gonetid]['source_authority'] = source_auth
                    continue

                # Check recv (server-side)
                m = patterns['recv'].search(line)
                if m:
                    gonetid = int(m.group(1))
                    source_auth = int(m.group(2))
                    spawn_lifecycle[gonetid]['recv'] = True
                    spawn_lifecycle[gonetid]['source_authority'] = source_auth
                    continue

    # Analyze results
    total = len(spawn_lifecycle)
    if total == 0:
        print("No spawn events found with diagnostic logging.")
        print("Make sure the code has [SPAWN-PROPAGATE], [SPAWN-RELAY], [SPAWN-DESER], [SPAWN-RECV] logs enabled.")
        return

    complete = 0
    partial = []

    for gonetid, stages in sorted(spawn_lifecycle.items()):
        full_gonetid, raw_id, owner = parse_gonetid(gonetid)

        all_stages = stages['propagate'] and stages['relay'] and stages['deser'] and stages['recv']
        if all_stages:
            complete += 1
        else:
            # Missing some stages
            missing = []
            if not stages['propagate']: missing.append('PROPAGATE')
            if not stages['relay']: missing.append('RELAY')
            if not stages['deser']: missing.append('DESER')
            if not stages['recv']: missing.append('RECV')

            partial.append({
                'gonetid': gonetid,
                'raw_id': raw_id,
                'owner': owner,
                'name': stages['name'],
                'missing': missing,
                'stages': stages
            })

    print(f"=" * 60)
    print(f"SPAWN EVENT PROPAGATION ANALYSIS")
    print(f"=" * 60)
    print(f"Total spawn events tracked: {total}")
    print(f"Complete (all 4 stages):    {complete}")
    print(f"Incomplete:                 {len(partial)}")
    print()

    if partial:
        print(f"=" * 60)
        print(f"INCOMPLETE SPAWN EVENTS (missing stages)")
        print(f"=" * 60)

        # Group by missing stages
        by_missing = defaultdict(list)
        for p in partial:
            key = tuple(p['missing'])
            by_missing[key].append(p)

        for missing_stages, items in sorted(by_missing.items(), key=lambda x: len(x[1]), reverse=True):
            print(f"\nMissing {missing_stages}: ({len(items)} objects)")
            print("-" * 50)
            for item in items[:10]:  # Show first 10
                name = item['name'] or 'unknown'
                stages = item['stages']
                stage_str = f"P={'Y' if stages['propagate'] else 'N'} " \
                           f"L={'Y' if stages['relay'] else 'N'} " \
                           f"D={'Y' if stages['deser'] else 'N'} " \
                           f"R={'Y' if stages['recv'] else 'N'}"
                print(f"  GONetId={item['gonetid']:<10} raw={item['raw_id']:<5} owner={item['owner']:<5} [{stage_str}] {name}")
            if len(items) > 10:
                print(f"  ... and {len(items) - 10} more")

        print()
        print("Legend: P=Propagate, L=Relay, D=Deserialize, R=Receive")
        print()

        # Analysis summary
        print(f"=" * 60)
        print(f"ROOT CAUSE ANALYSIS")
        print(f"=" * 60)

        missing_deser_recv = [p for p in partial if 'DESER' in p['missing'] and 'RECV' in p['missing']]
        missing_relay = [p for p in partial if 'RELAY' in p['missing']]
        missing_propagate = [p for p in partial if 'PROPAGATE' in p['missing']]

        if missing_deser_recv and not missing_relay and not missing_propagate:
            print("\n[!] NETWORK LOSS DETECTED")
            print("    Events were published and relayed by client, but never reached server.")
            print("    Possible causes:")
            print("    - Network packet loss (check if RELIABLE channel is used)")
            print("    - Server not ready to receive (check connection state)")
            print("    - Deserialization error (check for errors in server log)")

        if missing_relay:
            print("\n[!] RELAY FAILURE DETECTED")
            print("    Events were published but NOT relayed to server.")
            print("    Possible causes:")
            print("    - Event filtered out in OnAnyEvent_RelayToRemoteConnections_IfAppropriate")
            print("    - IsSourceRemote check failing incorrectly")
            print("    - Serialization error")

        if missing_propagate:
            print("\n[!] PROPAGATION FAILURE DETECTED")
            print("    Spawn events never published by client.")
            print("    Possible causes:")
            print("    - AutoPropagateInitialInstantiation not called")
            print("    - GONetParticipant not properly initialized")

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} <log_directory>")
        print("Example: python analyze_spawn_events.py logs/issues4-pid-separation")
        sys.exit(1)

    log_dir = sys.argv[1]
    if not os.path.isdir(log_dir):
        print(f"Error: {log_dir} is not a directory")
        sys.exit(1)

    analyze_spawn_events(log_dir)
