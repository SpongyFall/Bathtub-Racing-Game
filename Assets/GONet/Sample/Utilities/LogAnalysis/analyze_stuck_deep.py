#!/usr/bin/env python3
"""
Deep analysis of stuck objects in BlendDiag logs.
Analyzes both server and client side for stuck projectiles.
"""

import re
import sys
from collections import defaultdict
from pathlib import Path

def analyze_stuck_deep(log_path, max_lines=500000):
    # Track per-object data
    server_objects = defaultdict(lambda: {'data_in': [], 'blend': [], 'data_out': []})
    client_objects = defaultdict(lambda: {'data_in': [], 'blend': [], 'data_out': []})

    # Regex patterns - corrected for SVR/CLI
    data_in_pos = re.compile(r'DATA_IN\|(SVR|CLI)\|(\d+)\|([0-9.]+)\|POS\|(\d+)\|([0-9.-]+)\|([0-9.-]+)\|([0-9.-]+)\|')
    data_in_rot = re.compile(r'DATA_IN\|(SVR|CLI)\|(\d+)\|[0-9.]+\|ROT\|(\d+)\|')
    blend_pattern = re.compile(r'BLEND\|(SVR|CLI)\|(\d+)\|[0-9.]+\|(POS|ROT)\|\d+:\d+\|([0-9.]+)\|([0-9.-]+)\|([0-9.-]+)\|(\d+)\|\d+\|[0-9.-]+\|(\d)\|(\d)\|(\d+)\|')
    data_out_pos = re.compile(r'DATA_OUT\|(SVR|CLI)\|(\d+)\|[0-9.]+\|POS\|(\d+)\|([0-9.-]+)\|([0-9.-]+)\|([0-9.-]+)\|')

    line_count = 0

    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            line_count += 1
            if line_count > max_lines:
                break

            # DATA_IN Position
            m = data_in_pos.search(line)
            if m:
                side = m.group(1)
                frame = int(m.group(2))
                gonetId = int(m.group(4))
                x, y, z = float(m.group(5)), float(m.group(6)), float(m.group(7))
                obj_dict = server_objects if side == 'SVR' else client_objects
                obj_dict[gonetId]['data_in'].append({'frame': frame, 'pos': (x, y, z)})
                continue

            # BLEND
            m = blend_pattern.search(line)
            if m:
                side = m.group(1)
                frame = int(m.group(2))
                dtype = m.group(3)
                tValue = float(m.group(4))
                dtBracket = float(m.group(5))
                dtFromUpper = float(m.group(6))
                validCount = int(m.group(7))
                isExtrap = int(m.group(8))
                isPhysics = int(m.group(9))
                gonetId = int(m.group(10))

                obj_dict = server_objects if side == 'SVR' else client_objects
                obj_dict[gonetId]['blend'].append({
                    'frame': frame, 'dtype': dtype, 'tValue': tValue,
                    'dtBracket': dtBracket, 'validCount': validCount,
                    'isExtrap': isExtrap, 'isPhysics': isPhysics
                })
                continue

            # DATA_OUT Position
            m = data_out_pos.search(line)
            if m:
                side = m.group(1)
                frame = int(m.group(2))
                gonetId = int(m.group(3))
                x, y, z = float(m.group(4)), float(m.group(5)), float(m.group(6))
                obj_dict = server_objects if side == 'SVR' else client_objects
                obj_dict[gonetId]['data_out'].append({'frame': frame, 'pos': (x, y, z)})

    print(f"Processed {line_count} lines")
    print(f"\n{'='*80}")
    print("OVERVIEW")
    print(f"{'='*80}")
    print(f"Server objects: {len(server_objects)}")
    print(f"Client objects: {len(client_objects)}")

    # Identify object types by GONetId pattern
    def classify_object(gonetId):
        raw = gonetId >> 10
        authority = gonetId & 0x3FF
        if raw < 10:
            return 'player', authority
        else:
            return 'projectile', authority

    print(f"\n{'='*80}")
    print("SERVER-SIDE OBJECT ANALYSIS")
    print(f"{'='*80}")

    server_projectiles = {}
    server_players = {}
    for gid, data in server_objects.items():
        obj_type, auth = classify_object(gid)
        if obj_type == 'projectile':
            server_projectiles[gid] = data
        else:
            server_players[gid] = data

    print(f"Server players: {len(server_players)}")
    print(f"Server projectiles: {len(server_projectiles)}")

    # Analyze server projectiles for "stuck" behavior
    print(f"\n--- Server Projectile Analysis ---")
    server_stuck = []
    for gid, data in server_projectiles.items():
        if len(data['blend']) < 3:
            continue

        # Check blend tValues
        blend_entries = data['blend']
        avg_tValue = sum(e['tValue'] for e in blend_entries) / len(blend_entries)
        is_physics = blend_entries[0]['isPhysics'] if blend_entries else -1

        # Check position movement from DATA_OUT
        data_out = data['data_out']
        movement = 0
        first_pos = (0,0,0)
        if len(data_out) >= 2:
            first_pos = data_out[0]['pos']
            last = data_out[-1]['pos']
            movement = abs(last[0]-first_pos[0]) + abs(last[1]-first_pos[1]) + abs(last[2]-first_pos[2])
        elif len(data_out) == 1:
            first_pos = data_out[0]['pos']

        # Check for zero or near-zero start position (origin stuck)
        at_origin = abs(first_pos[0]) < 0.1 and abs(first_pos[1]) < 0.1 and abs(first_pos[2]) < 0.1

        if avg_tValue < 0.8 or movement < 1.0:
            server_stuck.append({
                'gonetId': gid,
                'blend_count': len(blend_entries),
                'avg_tValue': avg_tValue,
                'isPhysics': is_physics,
                'movement': movement,
                'first_pos': first_pos,
                'at_origin': at_origin,
                'data_out_count': len(data_out)
            })

    server_stuck.sort(key=lambda x: x['avg_tValue'])
    print(f"Server stuck/slow projectiles: {len(server_stuck)}")
    for s in server_stuck[:20]:
        print(f"  GONetId={s['gonetId']}: tValue={s['avg_tValue']:.3f}, movement={s['movement']:.1f}, " +
              f"isPhysics={s['isPhysics']}, at_origin={s['at_origin']}, pos={s['first_pos']}")

    print(f"\n{'='*80}")
    print("CLIENT-SIDE OBJECT ANALYSIS")
    print(f"{'='*80}")

    client_projectiles = {}
    client_players = {}
    for gid, data in client_objects.items():
        obj_type, auth = classify_object(gid)
        if obj_type == 'projectile':
            client_projectiles[gid] = data
        else:
            client_players[gid] = data

    print(f"Client players: {len(client_players)}")
    print(f"Client projectiles: {len(client_projectiles)}")

    # Analyze client projectiles
    print(f"\n--- Client Projectile Analysis ---")
    client_stuck = []
    for gid, data in client_projectiles.items():
        if len(data['blend']) < 3:
            continue

        blend_entries = data['blend']
        avg_tValue = sum(e['tValue'] for e in blend_entries) / len(blend_entries)
        is_physics = blend_entries[0]['isPhysics'] if blend_entries else -1

        data_out = data['data_out']
        movement = 0
        first_pos = (0,0,0)
        if len(data_out) >= 2:
            first_pos = data_out[0]['pos']
            last = data_out[-1]['pos']
            movement = abs(last[0]-first_pos[0]) + abs(last[1]-first_pos[1]) + abs(last[2]-first_pos[2])
        elif len(data_out) == 1:
            first_pos = data_out[0]['pos']

        at_origin = abs(first_pos[0]) < 0.1 and abs(first_pos[1]) < 0.1 and abs(first_pos[2]) < 0.1

        # Check data_in vs blend relationship
        data_in_count = len(data['data_in'])

        if avg_tValue < 0.8 or movement < 1.0 or at_origin:
            client_stuck.append({
                'gonetId': gid,
                'blend_count': len(blend_entries),
                'avg_tValue': avg_tValue,
                'isPhysics': is_physics,
                'movement': movement,
                'first_pos': first_pos,
                'at_origin': at_origin,
                'data_in_count': data_in_count,
                'data_out_count': len(data_out)
            })

    client_stuck.sort(key=lambda x: (x['at_origin'], -x['avg_tValue']), reverse=True)
    print(f"Client stuck/slow projectiles: {len(client_stuck)}")
    for s in client_stuck[:20]:
        print(f"  GONetId={s['gonetId']}: tValue={s['avg_tValue']:.3f}, movement={s['movement']:.1f}, " +
              f"isPhysics={s['isPhysics']}, at_origin={s['at_origin']}, data_in={s['data_in_count']}, pos={s['first_pos']}")

    # Cross-reference: objects on server but not on client
    print(f"\n{'='*80}")
    print("CROSS-REFERENCE: SERVER vs CLIENT")
    print(f"{'='*80}")
    server_proj_ids = set(server_projectiles.keys())
    client_proj_ids = set(client_projectiles.keys())
    server_only = server_proj_ids - client_proj_ids
    client_only = client_proj_ids - server_proj_ids
    both = server_proj_ids & client_proj_ids

    print(f"Projectiles on server only: {len(server_only)}")
    print(f"Projectiles on client only: {len(client_only)}")
    print(f"Projectiles on both: {len(both)}")

    if server_only:
        print(f"\nSample server-only projectiles (first 10):")
        for gid in list(server_only)[:10]:
            data = server_projectiles[gid]
            print(f"  GONetId={gid}: blend={len(data['blend'])}, data_out={len(data['data_out'])}")

    # Detailed analysis of stuck objects
    print(f"\n{'='*80}")
    print("DETAILED STUCK OBJECT TIMELINE")
    print(f"{'='*80}")

    # Pick a client stuck object and show its timeline
    if client_stuck:
        stuck = client_stuck[0]
        gid = stuck['gonetId']
        print(f"\nTimeline for GONetId={gid} (client stuck object):")
        data = client_objects[gid]

        print(f"  DATA_IN entries: {len(data['data_in'])}")
        for i, e in enumerate(data['data_in'][:5]):
            print(f"    [{i}] frame={e['frame']}, pos={e['pos']}")

        print(f"  BLEND entries: {len(data['blend'])}")
        for i, e in enumerate(data['blend'][:5]):
            print(f"    [{i}] frame={e['frame']}, tValue={e['tValue']:.3f}, validCount={e['validCount']}, isPhysics={e['isPhysics']}")

        print(f"  DATA_OUT entries: {len(data['data_out'])}")
        for i, e in enumerate(data['data_out'][:5]):
            print(f"    [{i}] frame={e['frame']}, pos={e['pos']}")

if __name__ == '__main__':
    log_path = Path("C:/Users/shash/AppData/LocalLow/Galore Interactive/GONetSandbox/logs/gonet-BlendDiag-2025-12-01.log")

    if len(sys.argv) > 1:
        log_path = Path(sys.argv[1])

    if not log_path.exists():
        print(f"Log file not found: {log_path}")
        sys.exit(1)

    analyze_stuck_deep(log_path)
