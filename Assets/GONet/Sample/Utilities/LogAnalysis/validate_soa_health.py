#!/usr/bin/env python3
"""
=============================================================================
QUICK VALIDATION SCRIPT FOR SOA HEALTH AND LIFECYCLE TRACKING
=============================================================================

PURPOSE: Quickly validate that SoA registration and lifecycle tracking are
working correctly. Run this after any test to get a PASS/FAIL verdict.

USAGE:
    python validate_soa_health.py <log_file_path>
    python validate_soa_health.py  # Uses most recent log in default location

=============================================================================
BASELINE EXPECTATIONS (from clean tests):
=============================================================================

1. REGISTRATION PARITY:
   - Every [SoA-REG-TRACE] should have a corresponding [LIFECYCLE] CLI|SOA_REG
   - Health monitor total should match SOA_REG count for each role
   - If mismatch: lifecycle tracker may be disabled or duplicate filtering issue

2. SERVER BEHAVIOR:
   - Server registers NON-authority objects (objects owned by clients)
   - Server SOA_REG count should match health monitor total
   - Server should NOT have stuck objects (it's the authority)

3. CLIENT BEHAVIOR:
   - Client registers NON-authority objects (objects owned by server OR other clients)
   - Client SOA_REG count should match health monitor total
   - Stuck objects indicate DATA_IN not arriving (network or registration issue)

4. HEALTHY TEST INDICATORS:
   - stuck=0 for both server and client at end of test
   - noDataIn=0 (all registered objects received network data)
   - SOA_REG count == health monitor total (no tracking discrepancy)

5. PROBLEM INDICATORS:
   - stuck > 0: Objects not receiving data or not being applied
   - noDataIn > 0: Objects registered but never received network updates
   - SOA_REG count < health monitor total: Registration path issue
   - validCount=2 in STUCK-OBJECT: Only seed data, no real sync received

6. WHAT IS NOT A PROBLEM:
   - Objects at rest that successfully synced are NOT stuck
   - Physics objects that stopped moving don't need continuous sync
   - "stale" data is fine if object was healthy and correctly positioned
   - Key metric: did applyCount > 0 and validCount > 2? If yes, it worked.

=============================================================================
TROUBLESHOOTING GUIDE:
=============================================================================

SYMPTOM: Client has more health-tracked objects than SOA_REG events
CAUSE: Scene hierarchy objects or objects without sync companions may be
       registered through OnGONetReady path but hit early return in
       RegisterObjectInSoA() (no sync companion = no blendable values)
CHECK:
  1. grep "SoA-REG-TRACE" to see registered objects
  2. Compare with [LIFECYCLE] CLI|READY events with isMine=False
  3. Objects with isMine=False but no trace = no sync companion (OK)

SYMPTOM: Stuck objects with NO_DATA_IN and validCount=2
CAUSE: Object registered in SoA but network sync data never arrived
CHECK:
  1. Is the object's GONetId in server's sync output?
  2. Did GONetId change after registration? (check OnGONetIdChanged events)
  3. Is the object in soaPositionLookup dictionary?
  4. Does object have [GONetAutoMagicalSync] attributes? (no = no sync)

SYMPTOM: Server has stuck objects
CAUSE: Server shouldn't have non-authority objects stuck
CHECK: Why is server registering authority objects in SoA?

SYMPTOM: STALE_NO_RECENT_DATA warnings for physics objects
CAUSE: REMOVED - this was a false positive. Objects at rest legitimately
       stop receiving sync updates when nothing changes. This is correct.
       Real stuck = NO_DATA_IN (never got data) or VALIDCOUNT_STUCK_AT_2

=============================================================================
"""

import sys
import re
import os
from collections import defaultdict
from pathlib import Path


def find_latest_log():
    """Find the most recent log file in the default location."""
    log_dir = Path(r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs")
    if not log_dir.exists():
        return None

    log_files = list(log_dir.glob("gonet-*.log"))
    if not log_files:
        return None

    return max(log_files, key=lambda f: f.stat().st_mtime)


def parse_log(log_path):
    """Parse relevant log entries."""
    results = {
        'health_snapshots': {'SVR': [], 'CLI': []},
        'stuck_objects': {'SVR': [], 'CLI': []},
        'lifecycle_soa_reg': {'SVR': set(), 'CLI': set()},
        'reg_traces': {'SVR': [], 'CLI': []},
        'errors': []
    }

    # Patterns
    health_pattern = re.compile(r'\[SoA-HEALTH\] (SVR|CLI)\|total=(\d+)\|healthy=(\d+)\|stuck=(\d+)\|noDataIn=(\d+)')
    stuck_pattern = re.compile(r'\[STUCK-OBJECT\] (SVR|CLI)\|gid=(\d+)\|raw=(\d+)\|owner=(\w+)\|reason=(\w+)')
    lifecycle_pattern = re.compile(r'\[LIFECYCLE\] (SVR|CLI)\|SOA_REG\|(\d+)\|')
    trace_pattern = re.compile(r'\[SoA-REG-TRACE\] (SVR|CLI)\|GONetId=(\d+)\|.*HealthMonitorEnabled=(\w+)\|LifecycleEnabled=(\w+)')

    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            # Health snapshots
            m = health_pattern.search(line)
            if m:
                role, total, healthy, stuck, no_data_in = m.groups()
                results['health_snapshots'][role].append({
                    'total': int(total),
                    'healthy': int(healthy),
                    'stuck': int(stuck),
                    'noDataIn': int(no_data_in)
                })
                continue

            # Stuck objects
            m = stuck_pattern.search(line)
            if m:
                role, gid, raw, owner, reason = m.groups()
                results['stuck_objects'][role].append({
                    'gonetId': int(gid),
                    'raw': int(raw),
                    'owner': owner,
                    'reason': reason
                })
                continue

            # Lifecycle SOA_REG
            m = lifecycle_pattern.search(line)
            if m:
                role, gid = m.groups()
                results['lifecycle_soa_reg'][role].add(int(gid))
                continue

            # Registration traces
            m = trace_pattern.search(line)
            if m:
                role, gid, health_enabled, lifecycle_enabled = m.groups()
                results['reg_traces'][role].append({
                    'gonetId': int(gid),
                    'healthEnabled': health_enabled == 'True',
                    'lifecycleEnabled': lifecycle_enabled == 'True'
                })

    return results


def validate(results):
    """Validate the parsed results and return pass/fail verdict."""
    issues = []
    warnings = []

    for role in ['SVR', 'CLI']:
        health = results['health_snapshots'][role]
        stuck = results['stuck_objects'][role]
        soa_reg = results['lifecycle_soa_reg'][role]
        traces = results['reg_traces'][role]

        if not health:
            warnings.append(f"{role}: No health snapshots found")
            continue

        # Get final health snapshot
        final_health = health[-1]
        max_total = max(h['total'] for h in health)

        # Check 1: Final stuck count
        if final_health['stuck'] > 0:
            issues.append(f"{role}: {final_health['stuck']} stuck objects at end of test")

        # Check 2: SOA_REG count vs max health total
        soa_reg_count = len(soa_reg)
        if soa_reg_count < max_total:
            issues.append(f"{role}: SOA_REG count ({soa_reg_count}) < max health total ({max_total}) - TRACKING DISCREPANCY")

        # Check 3: Registration trace analysis
        if traces:
            disabled_health = sum(1 for t in traces if not t['healthEnabled'])
            disabled_lifecycle = sum(1 for t in traces if not t['lifecycleEnabled'])
            if disabled_health > 0:
                issues.append(f"{role}: {disabled_health} registrations with health monitor DISABLED")
            if disabled_lifecycle > 0:
                issues.append(f"{role}: {disabled_lifecycle} registrations with lifecycle tracker DISABLED")

        # Check 4: Unique stuck object reasons
        if stuck:
            reasons = defaultdict(int)
            for s in stuck:
                reasons[s['reason']] += 1
            for reason, count in reasons.items():
                warnings.append(f"{role}: {count} stuck with reason={reason}")

    return issues, warnings


def main():
    # Find log file
    if len(sys.argv) > 1:
        log_path = sys.argv[1]
    else:
        log_path = find_latest_log()
        if not log_path:
            print("ERROR: No log file specified and couldn't find default")
            return 1

    print(f"Analyzing: {log_path}")
    print("=" * 70)

    # Parse and validate
    results = parse_log(log_path)
    issues, warnings = validate(results)

    # Print summary
    for role in ['SVR', 'CLI']:
        health = results['health_snapshots'][role]
        soa_reg = results['lifecycle_soa_reg'][role]
        traces = results['reg_traces'][role]

        if health:
            max_total = max(h['total'] for h in health)
            final = health[-1]
            print(f"\n{role}:")
            print(f"  Max objects tracked: {max_total}")
            print(f"  Final: total={final['total']}, healthy={final['healthy']}, stuck={final['stuck']}")
            print(f"  SOA_REG lifecycle events: {len(soa_reg)}")
            if traces:
                print(f"  Registration traces: {len(traces)}")

    # Print warnings
    if warnings:
        print("\n[WARNINGS]")
        for w in warnings:
            print(f"  - {w}")

    # Print issues and verdict
    print("\n" + "=" * 70)
    if issues:
        print("VERDICT: FAIL")
        print("\n[ISSUES]")
        for issue in issues:
            print(f"  - {issue}")
        return 1
    else:
        print("VERDICT: PASS")
        print("All checks passed - SoA health tracking looks correct")
        return 0


if __name__ == "__main__":
    sys.exit(main())
