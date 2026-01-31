#!/usr/bin/env python3
"""
Analyze CLIENT-SIDE stuck objects from BlendDiag logs.

Focuses on:
1. Objects on CLIENT that receive data but don't blend properly
2. Objects with persistently low tValue
3. Correlation between isPhysics flag and stuck behavior
"""

import re
import sys
from collections import defaultdict
from pathlib import Path

def analyze_client_stuck(log_path, max_lines=500000):
    """Analyze client-side stuck objects."""

    # Track per-object blend statistics
    objects = defaultdict(lambda: {
        'blend_count': 0,
        'tValues': [],
        'dtTargets': [],
        'dtSamples': [],
        'validCounts': [],
        'isPhysics': None,
        'positions': [],
        'first_pos': None,
        'last_pos': None,
        'first_frame': None,
        'last_frame': None,
    })

    # BLEND format: BLEND|CLI|frame|time|POS/ROT|stream:idx|tValue|dtSamples|dtTarget|validCount|capacity|dtSamplesPlusDtTarget|isReady|isPhysics|gonetId|hasRigidBody
    blend_pattern = re.compile(
        r'BLEND\|CLI\|(\d+)\|[\d.]+\|(POS|ROT)\|(\d+):(\d+)\|([\d.]+)\|([\d.-]+)\|([\d.-]+)\|(\d+)\|\d+\|[\d.-]+\|(\d)\|(\d)\|(\d+)\|'
    )

    # DATA_IN format: DATA_IN|CLI|frame|time|POS|gonetId|x|y|z|ticks|isAnchor|isPhysics
    data_in_pattern = re.compile(
        r'DATA_IN\|CLI\|\d+\|[\d.]+\|POS\|(\d+)\|([\d.-]+)\|([\d.-]+)\|([\d.-]+)\|'
    )

    print(f"Analyzing: {log_path}")
    print(f"Max lines: {max_lines}")

    line_count = 0
    blend_count = 0
    data_in_count = 0

    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            line_count += 1
            if line_count > max_lines:
                break

            # Only process CLIENT entries
            if '[Client:1]' not in line:
                continue

            # Check for BLEND entries
            match = blend_pattern.search(line)
            if match:
                blend_count += 1
                frame = int(match.group(1))
                data_type = match.group(2)
                stream_idx = int(match.group(3))
                obj_idx = int(match.group(4))
                tValue = float(match.group(5))
                dtSamples = float(match.group(6))
                dtTarget = float(match.group(7))
                validCount = int(match.group(8))
                isReady = int(match.group(9))
                isPhysics = int(match.group(10))
                gonetId = int(match.group(11))

                obj = objects[gonetId]
                obj['blend_count'] += 1
                obj['tValues'].append(tValue)
                obj['dtTargets'].append(dtTarget)
                obj['dtSamples'].append(dtSamples)
                obj['validCounts'].append(validCount)
                obj['isPhysics'] = isPhysics

                if obj['first_frame'] is None:
                    obj['first_frame'] = frame
                obj['last_frame'] = frame
                continue

            # Check for DATA_IN entries
            match = data_in_pattern.search(line)
            if match:
                data_in_count += 1
                gonetId = int(match.group(1))
                x = float(match.group(2))
                y = float(match.group(3))
                z = float(match.group(4))
                pos = (x, y, z)

                obj = objects[gonetId]
                if obj['first_pos'] is None:
                    obj['first_pos'] = pos
                obj['last_pos'] = pos
                obj['positions'].append(pos)

    print(f"\nProcessed {line_count} lines")
    print(f"Client BLEND entries: {blend_count}")
    print(f"Client DATA_IN entries: {data_in_count}")
    print(f"Unique objects: {len(objects)}")

    # Find objects with consistently low tValue (potential stuck objects)
    print("\n" + "="*80)
    print("OBJECTS WITH CONSISTENTLY LOW tValue (potential stuck)")
    print("="*80)

    stuck_candidates = []
    for gonetId, obj in objects.items():
        if obj['blend_count'] < 5:
            continue

        avg_tValue = sum(obj['tValues']) / len(obj['tValues'])
        min_tValue = min(obj['tValues'])
        max_tValue = max(obj['tValues'])

        # Check for movement
        movement = 0
        if obj['first_pos'] and obj['last_pos']:
            dx = abs(obj['last_pos'][0] - obj['first_pos'][0])
            dy = abs(obj['last_pos'][1] - obj['first_pos'][1])
            dz = abs(obj['last_pos'][2] - obj['first_pos'][2])
            movement = dx + dy + dz

        # Objects with low avg tValue and little movement are stuck
        if avg_tValue < 0.7 or (movement < 0.5 and obj['blend_count'] > 20):
            stuck_candidates.append((gonetId, obj, avg_tValue, movement))

    stuck_candidates.sort(key=lambda x: x[2])  # Sort by avg_tValue

    for gonetId, obj, avg_tValue, movement in stuck_candidates[:30]:
        print(f"\nGONetId {gonetId}:")
        print(f"  Blend count: {obj['blend_count']}")
        print(f"  tValue: avg={avg_tValue:.4f}, min={min(obj['tValues']):.4f}, max={max(obj['tValues']):.4f}")
        print(f"  dtSamples: {obj['dtSamples'][:5]}")
        print(f"  validCount: {obj['validCounts'][:5]}")
        print(f"  isPhysics: {obj['isPhysics']}")
        print(f"  Movement: {movement:.4f}")
        print(f"  First pos: {obj['first_pos']}")
        print(f"  Last pos: {obj['last_pos']}")
        print(f"  Frames: {obj['first_frame']} - {obj['last_frame']}")

    # Summary by physics type
    print("\n" + "="*80)
    print("SUMMARY BY PHYSICS TYPE")
    print("="*80)

    physics_objects = [o for o in objects.values() if o['isPhysics'] == 1]
    non_physics_objects = [o for o in objects.values() if o['isPhysics'] == 0]

    print(f"Physics objects: {len(physics_objects)}")
    print(f"Non-physics objects: {len(non_physics_objects)}")

    if physics_objects:
        avg_tValue_physics = sum(sum(o['tValues'])/len(o['tValues']) for o in physics_objects) / len(physics_objects)
        print(f"  Physics avg tValue: {avg_tValue_physics:.4f}")

    if non_physics_objects:
        avg_tValue_non_physics = sum(sum(o['tValues'])/len(o['tValues']) for o in non_physics_objects) / len(non_physics_objects)
        print(f"  Non-physics avg tValue: {avg_tValue_non_physics:.4f}")

    # Look for objects that stopped receiving data early
    print("\n" + "="*80)
    print("OBJECTS THAT STOPPED RECEIVING BLEND DATA EARLY")
    print("="*80)

    if objects:
        max_frame = max(o['last_frame'] for o in objects.values() if o['last_frame'])
        early_stop = [(gid, o) for gid, o in objects.items()
                      if o['last_frame'] and o['last_frame'] < max_frame - 100
                      and o['blend_count'] > 5]
        early_stop.sort(key=lambda x: x[1]['last_frame'])

        print(f"Max frame seen: {max_frame}")
        print(f"Objects that stopped early: {len(early_stop)}")

        for gonetId, obj in early_stop[:20]:
            print(f"  GONetId {gonetId}: stopped at frame {obj['last_frame']}, blend_count={obj['blend_count']}, isPhysics={obj['isPhysics']}")


if __name__ == '__main__':
    log_path = Path("C:/Users/shash/AppData/LocalLow/Galore Interactive/GONetSandbox/logs/gonet-BlendDiag-2025-12-01.log")

    if len(sys.argv) > 1:
        log_path = Path(sys.argv[1])

    if not log_path.exists():
        print(f"Log file not found: {log_path}")
        sys.exit(1)

    analyze_client_stuck(log_path)
