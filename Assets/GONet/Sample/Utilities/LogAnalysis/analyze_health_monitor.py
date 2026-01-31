#!/usr/bin/env python3
"""
Analyze SoA_ObjectHealthMonitor and SoA_LifecycleTracker output from GONet logs.

Parses [SoA-HEALTH], [STUCK-OBJECT], [LIFECYCLE], and [LIFECYCLE-SUMMARY] log lines to produce:
1. Complete lifecycle analysis (spawn → GONetId → ready → SoA_reg → data_in → apply)
2. Health summary timeline (how system health changed over time)
3. Stuck object root cause breakdown with lifecycle context
4. Objects stuck at specific lifecycle stages

Usage:
    python analyze_health_monitor.py <log_file_path>

Example:
    python analyze_health_monitor.py "C:/Users/.../logs/gonet-2025-12-01.log"
"""

import sys
import re
from collections import defaultdict
from dataclasses import dataclass, field
from typing import List, Dict, Optional, Set


@dataclass
class HealthSnapshot:
    """A single [SoA-HEALTH] log line parsed."""
    timestamp: float
    role: str
    total: int
    healthy: int
    stuck: int
    no_data_in: int
    stale_only: int
    recent: int
    is_mine: int


@dataclass
class StuckObject:
    """A single [STUCK-OBJECT] log line parsed."""
    timestamp: float
    role: str
    gonet_id: int
    raw_id: int
    owner: str
    reason: str
    age: float
    data_ins: int
    applies: int
    skips: int
    valid_count: int
    dist_spawn: float
    is_physics: bool
    last_skip: str
    since_last: float
    name: str


@dataclass
class LifecycleEvent:
    """A single [LIFECYCLE] log line parsed."""
    timestamp: float
    role: str
    stage: str
    gonet_id: int
    raw_id: int
    owner: str
    name: str
    extra: str


@dataclass
class ObjectLifecycle:
    """Complete lifecycle for a single object."""
    gonet_id: int
    name: str = ""
    owner: str = ""
    raw_id: int = 0

    # Stage timestamps (0 = not reached)
    spawn_time: float = 0
    gonetid_time: float = 0
    ready_time: float = 0
    soa_reg_time: float = 0
    despawn_time: float = 0

    # Extra info from stages
    is_mine: bool = False
    is_physics: bool = False
    spawn_source: str = ""


def decode_gonetid(gonetid: int) -> tuple:
    """Decode GONetId into raw and owner components."""
    raw = gonetid >> 10
    owner = gonetid & 1023
    owner_str = "SVR" if owner == 1023 else f"CLI{owner}"
    return raw, owner, owner_str


def parse_health_line(line: str, timestamp: float) -> Optional[HealthSnapshot]:
    """Parse a [SoA-HEALTH] log line."""
    match = re.search(
        r'\[SoA-HEALTH\]\s+(\w+)\|total=(\d+)\|healthy=(\d+)\|stuck=(\d+)\|'
        r'noDataIn=(\d+)\|staleOnly=(\d+)\|recent=(\d+)\|isMine=(\d+)',
        line
    )
    if match:
        return HealthSnapshot(
            timestamp=timestamp,
            role=match.group(1),
            total=int(match.group(2)),
            healthy=int(match.group(3)),
            stuck=int(match.group(4)),
            no_data_in=int(match.group(5)),
            stale_only=int(match.group(6)),
            recent=int(match.group(7)),
            is_mine=int(match.group(8))
        )
    return None


def parse_stuck_object_line(line: str, timestamp: float) -> Optional[StuckObject]:
    """Parse a [STUCK-OBJECT] log line."""
    match = re.search(
        r'\[STUCK-OBJECT\]\s+(\w+)\|gid=(\d+)\|raw=(\d+)\|owner=(\w+)\|reason=([^|]+)\|'
        r'age=([\d.]+)s\|dataIns=(\d+)\|applies=(\d+)\|skips=(\d+)\|validCnt=(\d+)\|'
        r'distSpawn=([\d.]+)\|physics=(\w+)\|lastSkip=([^|]+)\|sinceLast=([\d.-]+)s\|name=(.+)',
        line
    )
    if match:
        return StuckObject(
            timestamp=timestamp,
            role=match.group(1),
            gonet_id=int(match.group(2)),
            raw_id=int(match.group(3)),
            owner=match.group(4),
            reason=match.group(5),
            age=float(match.group(6)),
            data_ins=int(match.group(7)),
            applies=int(match.group(8)),
            skips=int(match.group(9)),
            valid_count=int(match.group(10)),
            dist_spawn=float(match.group(11)),
            is_physics=match.group(12).lower() == 'true',
            last_skip=match.group(13),
            since_last=float(match.group(14)),
            name=match.group(15)
        )
    return None


def parse_lifecycle_line(line: str, timestamp: float) -> Optional[LifecycleEvent]:
    """Parse a [LIFECYCLE] log line."""
    # Format: [LIFECYCLE] role|stage|GONetId|raw=X|owner=Y|t=Z|name|extra
    match = re.search(
        r'\[LIFECYCLE\]\s+(\w+)\|(\w+)\|(\d+)\|raw=(\d+)\|owner=(\w+)\|t=([\d.]+)\|([^|]*)\|?(.*)',
        line
    )
    if match:
        return LifecycleEvent(
            timestamp=timestamp,
            role=match.group(1),
            stage=match.group(2),
            gonet_id=int(match.group(3)),
            raw_id=int(match.group(4)),
            owner=match.group(5),
            name=match.group(7),
            extra=match.group(8) if match.group(8) else ""
        )
    return None


def extract_timestamp(line: str) -> float:
    """Extract elapsed time from log line."""
    match = re.search(r'\(frame:\d+/([\d.]+)s\)', line)
    if match:
        return float(match.group(1))
    return 0.0


def analyze_log(filepath: str):
    """Analyze a GONet log file for health monitor and lifecycle data."""
    health_snapshots: List[HealthSnapshot] = []
    stuck_objects: List[StuckObject] = []
    lifecycle_events: List[LifecycleEvent] = []
    object_lifecycles: Dict[int, ObjectLifecycle] = {}  # gonet_id -> lifecycle

    print(f"Analyzing: {filepath}")
    print("=" * 80)

    with open(filepath, 'r', errors='ignore') as f:
        for line in f:
            timestamp = extract_timestamp(line)

            if '[SoA-HEALTH]' in line:
                snapshot = parse_health_line(line, timestamp)
                if snapshot:
                    health_snapshots.append(snapshot)

            if '[STUCK-OBJECT]' in line:
                stuck = parse_stuck_object_line(line, timestamp)
                if stuck:
                    stuck_objects.append(stuck)

            if '[LIFECYCLE]' in line and '[LIFECYCLE-SUMMARY]' not in line:
                event = parse_lifecycle_line(line, timestamp)
                if event:
                    lifecycle_events.append(event)

                    # Update object lifecycle tracking
                    gid = event.gonet_id
                    if gid not in object_lifecycles:
                        object_lifecycles[gid] = ObjectLifecycle(gonet_id=gid)

                    lc = object_lifecycles[gid]
                    lc.name = event.name or lc.name
                    lc.owner = event.owner
                    lc.raw_id = event.raw_id

                    if event.stage == "SPAWN":
                        lc.spawn_time = timestamp
                        if "local=True" in event.extra:
                            lc.spawn_source = "local"
                        elif "local=False" in event.extra:
                            lc.spawn_source = "remote"
                    elif event.stage == "GONETID":
                        lc.gonetid_time = timestamp
                    elif event.stage == "READY":
                        lc.ready_time = timestamp
                        if "isMine=True" in event.extra:
                            lc.is_mine = True
                    elif event.stage == "SOA_REG":
                        lc.soa_reg_time = timestamp
                        if "physics=True" in event.extra:
                            lc.is_physics = True
                    elif event.stage == "DESPAWN":
                        lc.despawn_time = timestamp

    if not health_snapshots and not stuck_objects and not lifecycle_events:
        print("No health monitor or lifecycle data found in log file.")
        print("\nTo enable monitoring, the log should contain lines like:")
        print("  [LIFECYCLE] CLI|GONETID|12345|raw=12|owner=SVR|...")
        print("  [SoA-HEALTH] CLI|total=10|healthy=8|stuck=2|...")
        print("  [STUCK-OBJECT] CLI|gid=12345|...")
        return

    # =========================================================================
    # LIFECYCLE ANALYSIS
    # =========================================================================
    if lifecycle_events:
        print("\nLIFECYCLE SUMMARY")
        print("-" * 80)

        # Count stages reached
        stage_counts = defaultdict(int)
        for event in lifecycle_events:
            stage_counts[event.stage] += 1

        print("Events by stage:")
        for stage in ["SPAWN", "GONETID", "READY", "SOA_REG", "DESPAWN"]:
            print(f"  {stage}: {stage_counts.get(stage, 0)}")

        # Find objects stuck at each stage
        print("\nOBJECTS BY LIFECYCLE COMPLETION:")
        print("-" * 80)

        completed_soa = [lc for lc in object_lifecycles.values() if lc.soa_reg_time > 0 and not lc.is_mine]
        stuck_at_ready = [lc for lc in object_lifecycles.values() if lc.ready_time > 0 and lc.soa_reg_time == 0 and not lc.is_mine]
        stuck_at_gonetid = [lc for lc in object_lifecycles.values() if lc.gonetid_time > 0 and lc.ready_time == 0]
        never_got_id = [lc for lc in object_lifecycles.values() if lc.gonetid_time == 0 and lc.spawn_time > 0]

        print(f"  Completed SoA registration (non-IsMine): {len(completed_soa)}")
        print(f"  Stuck at READY (didn't reach SOA_REG): {len(stuck_at_ready)}")
        print(f"  Stuck at GONETID (didn't reach READY): {len(stuck_at_gonetid)}")
        print(f"  Never got GONetId: {len(never_got_id)}")

        # Show stuck at READY (most likely cause of stuck objects)
        if stuck_at_ready:
            print(f"\n[WARNING] OBJECTS STUCK AT READY (never registered in SoA):")
            print("-" * 80)
            print(f"{'GONetId':>10} {'Owner':>5} {'Name':<40} {'ReadyTime':>10}")
            print("-" * 80)
            for lc in stuck_at_ready[:15]:
                print(f"{lc.gonet_id:>10} {lc.owner:>5} {lc.name[:40]:<40} {lc.ready_time:>10.2f}")

    # =========================================================================
    # HEALTH TIMELINE
    # =========================================================================
    if health_snapshots:
        print("\n" + "=" * 80)
        print("HEALTH TIMELINE")
        print("-" * 80)
        print(f"{'Time':>10} {'Role':>5} {'Total':>6} {'Healthy':>8} {'Stuck':>6} {'NoData':>7} {'Stale':>6}")
        print("-" * 80)

        for snap in health_snapshots[-20:]:  # Last 20 snapshots
            print(f"{snap.timestamp:>10.1f} {snap.role:>5} {snap.total:>6} {snap.healthy:>8} "
                  f"{snap.stuck:>6} {snap.no_data_in:>7} {snap.stale_only:>6}")

        if len(health_snapshots) > 1:
            last = health_snapshots[-1]
            first = health_snapshots[0]
            print(f"\nHealth trend: stuck went from {first.stuck} to {last.stuck} "
                  f"over {last.timestamp - first.timestamp:.1f}s")

    # =========================================================================
    # STUCK OBJECTS ANALYSIS
    # =========================================================================
    if stuck_objects:
        print("\n" + "=" * 80)
        print("STUCK OBJECTS ANALYSIS")
        print("=" * 80)

        # Group by reason
        by_reason: Dict[str, List[StuckObject]] = defaultdict(list)
        for obj in stuck_objects:
            by_reason[obj.reason].append(obj)

        print("\nBREAKDOWN BY ROOT CAUSE:")
        print("-" * 80)
        for reason, objs in sorted(by_reason.items(), key=lambda x: -len(x[1])):
            unique_ids = set(o.gonet_id for o in objs)
            print(f"  {reason}: {len(unique_ids)} unique objects, {len(objs)} log entries")

        # Most stuck objects with lifecycle context
        unique_stuck = {}
        for obj in stuck_objects:
            if obj.gonet_id not in unique_stuck or obj.timestamp > unique_stuck[obj.gonet_id].timestamp:
                unique_stuck[obj.gonet_id] = obj

        print("\nTOP STUCK OBJECTS (with lifecycle status):")
        print("-" * 80)
        print(f"{'GONetId':>10} {'Owner':>5} {'Age':>8} {'DataIn':>7} {'Reason':<25} {'Lifecycle':<15}")
        print("-" * 80)

        sorted_stuck = sorted(unique_stuck.values(), key=lambda x: -x.age)
        for obj in sorted_stuck[:15]:
            # Get lifecycle status
            lc = object_lifecycles.get(obj.gonet_id)
            if lc:
                if lc.soa_reg_time > 0:
                    lc_status = "SOA_REG"
                elif lc.ready_time > 0:
                    lc_status = "READY"
                elif lc.gonetid_time > 0:
                    lc_status = "GONETID"
                else:
                    lc_status = "SPAWN?"
            else:
                lc_status = "NO_LIFECYCLE"

            print(f"{obj.gonet_id:>10} {obj.owner:>5} {obj.age:>7.1f}s {obj.data_ins:>7} "
                  f"{obj.reason[:25]:<25} {lc_status:<15}")

        # =========================================================================
        # RECOMMENDATIONS
        # =========================================================================
        print("\n" + "=" * 80)
        print("RECOMMENDATIONS")
        print("=" * 80)

        no_data_count = sum(1 for o in unique_stuck.values() if 'NO_DATA_IN' in o.reason)
        validcount_stuck = sum(1 for o in unique_stuck.values() if 'VALIDCOUNT' in o.reason)
        always_skipped = sum(1 for o in unique_stuck.values() if 'ALWAYS_SKIPPED' in o.reason)

        if no_data_count > 0:
            print(f"\n1. NO_DATA_IN ({no_data_count} objects):")
            print("   - Objects registered in SoA but never received network sync data")
            print("   - Check: Is the server sending position/rotation updates for these?")
            print("   - Check: Is SoA_WritePositionUpdate being called on clients?")
            print("   - Check: Are the GONetIds matching between server and client?")

        if validcount_stuck > 0:
            print(f"\n2. VALIDCOUNT_STUCK_AT_2 ({validcount_stuck} objects):")
            print("   - Objects have only seed samples (from registration), no real data")
            print("   - The ring buffer's historyCount never exceeded the initial seed value")
            print("   - Check: SoA_LockFreeRingBuffer.WritePositionUpdate increment logic")

        if always_skipped > 0:
            print(f"\n3. ALWAYS_SKIPPED ({always_skipped} objects):")
            print("   - Objects have data but Apply always skips them")
            print("   - Check lastSkip reason in object details above")

        # Objects that got stuck between READY and SOA_REG
        if lifecycle_events:
            stuck_at_ready = [lc for lc in object_lifecycles.values()
                           if lc.ready_time > 0 and lc.soa_reg_time == 0 and not lc.is_mine]
            if stuck_at_ready:
                print(f"\n4. STUCK AT READY ({len(stuck_at_ready)} objects):")
                print("   - Objects fired OnGONetReady but never registered in SoA")
                print("   - Check: RegisterObjectInSoA conditions (IsMine check, v2_isRegisteredInSoA)")
                print("   - This is likely the root cause of stuck objects!")


def analyze_cross_session(filepath: str):
    """
    Analyze server vs client consistency.
    Compares spawns, despawns, GONetIds, and usage between roles.
    """
    print("\n" + "=" * 80)
    print("CROSS-SESSION RECONCILIATION (Server vs Client)")
    print("=" * 80)

    # Track per-role data
    role_data = defaultdict(lambda: {
        'spawns': set(),        # GONetIds spawned
        'despawns': set(),      # GONetIds despawned
        'ready': set(),         # GONetIds that reached READY
        'soa_reg': set(),       # GONetIds registered in SoA
        'gonetid_assigned': set(),  # GONetIds assigned
        'id_usage_count': defaultdict(int),  # How many times each ID was seen
    })

    with open(filepath, 'r', errors='ignore') as f:
        for line in f:
            if '[LIFECYCLE]' not in line or '[LIFECYCLE-SUMMARY]' in line:
                continue

            # Parse role and stage
            match = re.search(
                r'\[LIFECYCLE\]\s+(\w+)\|(\w+)\|(\d+)\|',
                line
            )
            if not match:
                continue

            role = match.group(1)
            stage = match.group(2)
            gonet_id = int(match.group(3))

            data = role_data[role]
            data['id_usage_count'][gonet_id] += 1

            if stage == "SPAWN":
                data['spawns'].add(gonet_id)
            elif stage == "GONETID":
                data['gonetid_assigned'].add(gonet_id)
            elif stage == "READY":
                data['ready'].add(gonet_id)
            elif stage == "SOA_REG":
                data['soa_reg'].add(gonet_id)
            elif stage == "DESPAWN":
                data['despawns'].add(gonet_id)

    if not role_data:
        print("No lifecycle data found for cross-session analysis.")
        return

    # Summary table
    print("\nSUMMARY BY ROLE:")
    print("-" * 80)
    print(f"{'Role':>5} {'Spawns':>8} {'GONetId':>8} {'Ready':>8} {'SoA_Reg':>8} {'Despawn':>8} {'UniqueIDs':>10}")
    print("-" * 80)

    for role, data in sorted(role_data.items()):
        unique_ids = len(data['id_usage_count'])
        print(f"{role:>5} {len(data['spawns']):>8} {len(data['gonetid_assigned']):>8} "
              f"{len(data['ready']):>8} {len(data['soa_reg']):>8} {len(data['despawns']):>8} {unique_ids:>10}")

    # Cross-role comparison (if we have both SVR and CLI)
    if 'SVR' in role_data and 'CLI' in role_data:
        svr = role_data['SVR']
        cli = role_data['CLI']

        print("\nSERVER vs CLIENT DIFFERENCES:")
        print("-" * 80)

        # GONetIds on server but not client
        svr_only_ids = svr['gonetid_assigned'] - cli['gonetid_assigned']
        cli_only_ids = cli['gonetid_assigned'] - svr['gonetid_assigned']
        both_ids = svr['gonetid_assigned'] & cli['gonetid_assigned']

        print(f"GONetIds assigned on BOTH: {len(both_ids)}")
        print(f"GONetIds assigned ONLY on SERVER: {len(svr_only_ids)}")
        print(f"GONetIds assigned ONLY on CLIENT: {len(cli_only_ids)}")

        if svr_only_ids:
            print(f"\n  Server-only IDs (first 10): {sorted(list(svr_only_ids))[:10]}")
        if cli_only_ids:
            print(f"\n  Client-only IDs (first 10): {sorted(list(cli_only_ids))[:10]}")

        # SoA registration comparison
        print("\nSoA REGISTRATION COMPARISON:")
        svr_soa = svr['soa_reg']
        cli_soa = cli['soa_reg']
        print(f"  Server registered in SoA: {len(svr_soa)}")
        print(f"  Client registered in SoA: {len(cli_soa)}")

        # Client should have SoA registrations for server-owned objects
        # Server should have SoA registrations for client-owned objects
        cli_missing_soa = cli['ready'] - cli['soa_reg']
        if cli_missing_soa:
            # Filter to only non-IsMine (we'd need more context to know this properly)
            print(f"\n[WARNING] Client READY but NOT SoA_REG: {len(cli_missing_soa)} objects")
            print(f"     These objects may be stuck! IDs: {sorted(list(cli_missing_soa))[:10]}")

        # GONetId reuse detection
        print("\nGONETID REUSE DETECTION:")
        print("-" * 80)

        svr_reused = {gid: count for gid, count in svr['id_usage_count'].items() if count > 5}
        cli_reused = {gid: count for gid, count in cli['id_usage_count'].items() if count > 5}

        if svr_reused:
            print(f"Server - IDs seen >5 times (possible reuse/cycling):")
            for gid, count in sorted(svr_reused.items(), key=lambda x: -x[1])[:10]:
                print(f"  GONetId {gid}: {count} events")
        else:
            print("Server: No suspicious ID reuse detected")

        if cli_reused:
            print(f"\nClient - IDs seen >5 times (possible reuse/cycling):")
            for gid, count in sorted(cli_reused.items(), key=lambda x: -x[1])[:10]:
                print(f"  GONetId {gid}: {count} events")
        else:
            print("Client: No suspicious ID reuse detected")

        # Spawn/Despawn balance
        print("\nSPAWN/DESPAWN BALANCE:")
        print("-" * 80)

        svr_active = svr['spawns'] - svr['despawns']
        cli_active = cli['spawns'] - cli['despawns']

        print(f"Server - Spawned: {len(svr['spawns'])}, Despawned: {len(svr['despawns'])}, Active: {len(svr_active)}")
        print(f"Client - Spawned: {len(cli['spawns'])}, Despawned: {len(cli['despawns'])}, Active: {len(cli_active)}")

        active_diff = svr_active.symmetric_difference(cli_active)
        if active_diff:
            print(f"\n[WARNING] Active object mismatch: {len(active_diff)} objects differ")
            svr_not_cli = svr_active - cli_active
            cli_not_svr = cli_active - svr_active
            if svr_not_cli:
                print(f"     Active on SERVER but not CLIENT: {sorted(list(svr_not_cli))[:10]}")
            if cli_not_svr:
                print(f"     Active on CLIENT but not SERVER: {sorted(list(cli_not_svr))[:10]}")


def main():
    if len(sys.argv) < 2:
        print("Usage: python analyze_health_monitor.py <log_file_path>")
        print("\nExample:")
        print('  python analyze_health_monitor.py "C:/Users/.../logs/gonet-2025-12-01.log"')
        sys.exit(1)

    analyze_log(sys.argv[1])

    # Also run cross-session analysis
    analyze_cross_session(sys.argv[1])


if __name__ == "__main__":
    main()
