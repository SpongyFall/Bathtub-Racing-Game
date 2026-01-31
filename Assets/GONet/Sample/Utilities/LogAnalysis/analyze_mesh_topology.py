#!/usr/bin/env python3
"""
Analyze GONet mesh topology synchronization logs.

Parses [MESH-TOPO], [MESH-STATE], and [MESH-CONN] log lines to diagnose:
1. Topology sync events (send/receive) between nodes
2. Mesh state at each node over time
3. Connection state transitions
4. Topology discrepancies between nodes
5. Where mesh information gets lost during cascading failovers

Usage:
    python analyze_mesh_topology.py <log_file_path> [log_file_path2] ...

Example:
    python analyze_mesh_topology.py "C:/Users/.../logs/*-gonet-2025-12-12.log"
    python analyze_mesh_topology.py server.log client1.log client2.log

Output:
    - Per-node mesh state timeline
    - Topology sync events
    - State change events
    - Cross-node topology comparison
    - Identification of mesh discrepancies
"""

import sys
import re
import glob
from collections import defaultdict
from dataclasses import dataclass, field
from typing import List, Dict, Optional, Set, Tuple
from pathlib import Path


@dataclass
class TopologySendEvent:
    """Topology sent to a client."""
    timestamp: float
    sender_auth: int
    recipient_auth: int
    peers: List[str]  # "auth@ip:port" format
    peer_count: int


@dataclass
class TopologyBroadcastEvent:
    """New peer broadcast to existing clients."""
    timestamp: float
    sender_auth: int
    new_peer: str  # "auth@ip:port"
    recipients: List[int]


@dataclass
class TopologyRecvEvent:
    """Topology received from host."""
    timestamp: float
    receiver_auth: int
    peers: List[str]
    peer_count: int
    epoch: int


@dataclass
class TopologyRecvSummary:
    """Summary after processing received topology."""
    timestamp: float
    receiver_auth: int
    new_discoveries: int
    skipped_self: int
    skipped_connected: int


@dataclass
class MeshStateSnapshot:
    """Snapshot of mesh state at a point in time."""
    timestamp: float
    authority: int
    context: str
    outbound_conns: Dict[int, str]  # peer_id -> state
    inbound_peers: List[int]
    is_server: bool


@dataclass
class ConnectionStateChange:
    """Connection state transition."""
    timestamp: float
    my_auth: int
    peer_auth: int
    old_state: str
    new_state: str
    reason: str  # e.g., "PROMOTION"


@dataclass
class MeshTimeline:
    """Complete mesh timeline for one log file."""
    filename: str
    authority_id: int = 0

    topology_sends: List[TopologySendEvent] = field(default_factory=list)
    topology_broadcasts: List[TopologyBroadcastEvent] = field(default_factory=list)
    topology_recvs: List[TopologyRecvEvent] = field(default_factory=list)
    recv_summaries: List[TopologyRecvSummary] = field(default_factory=list)
    mesh_states: List[MeshStateSnapshot] = field(default_factory=list)
    conn_changes: List[ConnectionStateChange] = field(default_factory=list)

    # Key events for reference
    key_events: List[Tuple[float, str]] = field(default_factory=list)


def extract_timestamp(line: str) -> float:
    """Extract elapsed time from log line."""
    match = re.search(r'\(frame:\d+/([\d.]+)s\)', line)
    if match:
        return float(match.group(1))
    return 0.0


def extract_authority(line: str) -> int:
    """Extract authority ID from log line."""
    # Try myAuth= pattern first
    match = re.search(r'myAuth=(\d+)', line)
    if match:
        return int(match.group(1))

    # Try [Client:X] or [Server] patterns
    match = re.search(r'\[Client:(\d+)\]', line)
    if match:
        return int(match.group(1))

    if '[Server]' in line:
        return 1023  # Server authority

    return 0


def parse_topology_send(line: str, timestamp: float) -> Optional[TopologySendEvent]:
    """Parse [MESH-TOPO] SEND log line."""
    match = re.search(
        r'\[MESH-TOPO\] SEND to client (\d+): peers=\[([^\]]*)\] count=(\d+) myAuth=(\d+)',
        line
    )
    if match:
        peers_str = match.group(2).strip()
        peers = [p.strip() for p in peers_str.split(',') if p.strip()]
        return TopologySendEvent(
            timestamp=timestamp,
            sender_auth=int(match.group(4)),
            recipient_auth=int(match.group(1)),
            peers=peers,
            peer_count=int(match.group(3))
        )
    return None


def parse_topology_broadcast(line: str, timestamp: float) -> Optional[TopologyBroadcastEvent]:
    """Parse [MESH-TOPO] BROADCAST log line."""
    match = re.search(
        r'\[MESH-TOPO\] BROADCAST new peer (\d+@[^\s]+) to clients=\[([^\]]*)\] myAuth=(\d+)',
        line
    )
    if match:
        recipients_str = match.group(2).strip()
        recipients = [int(r.strip()) for r in recipients_str.split(',') if r.strip()]
        return TopologyBroadcastEvent(
            timestamp=timestamp,
            sender_auth=int(match.group(3)),
            new_peer=match.group(1),
            recipients=recipients
        )
    return None


def parse_topology_recv(line: str, timestamp: float) -> Optional[TopologyRecvEvent]:
    """Parse [MESH-TOPO] RECV log line."""
    match = re.search(
        r'\[MESH-TOPO\] RECV: peers=\[([^\]]*)\] count=(\d+) epoch=(\d+) myAuth=(\d+)',
        line
    )
    if match:
        peers_str = match.group(1).strip()
        peers = [p.strip() for p in peers_str.split(',') if p.strip()]
        return TopologyRecvEvent(
            timestamp=timestamp,
            receiver_auth=int(match.group(4)),
            peers=peers,
            peer_count=int(match.group(2)),
            epoch=int(match.group(3))
        )
    return None


def parse_recv_summary(line: str, timestamp: float) -> Optional[TopologyRecvSummary]:
    """Parse [MESH-TOPO] RECV summary log line."""
    match = re.search(
        r'\[MESH-TOPO\] RECV summary: newDiscoveries=(\d+) skippedSelf=(\d+) skippedConnected=(\d+) myAuth=(\d+)',
        line
    )
    if match:
        return TopologyRecvSummary(
            timestamp=timestamp,
            receiver_auth=int(match.group(4)),
            new_discoveries=int(match.group(1)),
            skipped_self=int(match.group(2)),
            skipped_connected=int(match.group(3))
        )
    return None


def parse_mesh_state(line: str, timestamp: float) -> Optional[MeshStateSnapshot]:
    """Parse [MESH-STATE] log line."""
    match = re.search(
        r'\[MESH-STATE\] ([^:]+): myAuth=(\d+) outbound=\[([^\]]*)\] inbound=\[([^\]]*)\] isServer=(\w+)',
        line
    )
    if match:
        context = match.group(1).strip()

        # Parse outbound connections (format: "auth:state,auth:state,...")
        outbound_str = match.group(3).strip()
        outbound = {}
        if outbound_str:
            for conn in outbound_str.split(','):
                conn = conn.strip()
                if ':' in conn:
                    parts = conn.split(':')
                    if len(parts) == 2:
                        try:
                            outbound[int(parts[0])] = parts[1]
                        except ValueError:
                            pass

        # Parse inbound peers (format: "auth,auth,...")
        inbound_str = match.group(4).strip()
        inbound = []
        if inbound_str:
            inbound = [int(p.strip()) for p in inbound_str.split(',') if p.strip()]

        return MeshStateSnapshot(
            timestamp=timestamp,
            authority=int(match.group(2)),
            context=context,
            outbound_conns=outbound,
            inbound_peers=inbound,
            is_server=match.group(5).lower() == 'true'
        )
    return None


def parse_conn_state_change(line: str, timestamp: float) -> Optional[ConnectionStateChange]:
    """Parse [MESH-CONN] STATE CHANGE log line."""
    match = re.search(
        r'\[MESH-CONN\] STATE CHANGE: peer=(\d+) (\w+)->(\w+)(?: \((\w+)\))? myAuth=(\d+)',
        line
    )
    if match:
        return ConnectionStateChange(
            timestamp=timestamp,
            my_auth=int(match.group(5)),
            peer_auth=int(match.group(1)),
            old_state=match.group(2),
            new_state=match.group(3),
            reason=match.group(4) or ""
        )
    return None


def detect_authority_from_log(filepath: str) -> int:
    """Detect authority ID from log file."""
    authority = 0
    with open(filepath, 'r', errors='ignore') as f:
        for i, line in enumerate(f):
            if i > 1000:  # Only check first 1000 lines
                break

            match = re.search(r'MyAuthorityId[=:]?\s*(\d+)', line)
            if match:
                authority = int(match.group(1))
                break

            match = re.search(r'myAuth=(\d+)', line)
            if match:
                authority = int(match.group(1))
                break

    return authority


def analyze_log_file(filepath: str) -> MeshTimeline:
    """Analyze a single log file for mesh topology events."""
    path = Path(filepath)
    authority = detect_authority_from_log(filepath)

    timeline = MeshTimeline(
        filename=path.name,
        authority_id=authority
    )

    with open(filepath, 'r', errors='ignore') as f:
        for line in f:
            timestamp = extract_timestamp(line)

            # Topology SEND
            if '[MESH-TOPO] SEND' in line:
                event = parse_topology_send(line, timestamp)
                if event:
                    timeline.topology_sends.append(event)
                    timeline.key_events.append((timestamp, line.strip()))

            # Topology BROADCAST
            elif '[MESH-TOPO] BROADCAST' in line:
                event = parse_topology_broadcast(line, timestamp)
                if event:
                    timeline.topology_broadcasts.append(event)
                    timeline.key_events.append((timestamp, line.strip()))

            # Topology RECV (not summary)
            elif '[MESH-TOPO] RECV:' in line:
                event = parse_topology_recv(line, timestamp)
                if event:
                    timeline.topology_recvs.append(event)
                    timeline.key_events.append((timestamp, line.strip()))

            # RECV summary
            elif '[MESH-TOPO] RECV summary' in line:
                event = parse_recv_summary(line, timestamp)
                if event:
                    timeline.recv_summaries.append(event)
                    timeline.key_events.append((timestamp, line.strip()))

            # Mesh state snapshots
            elif '[MESH-STATE]' in line:
                event = parse_mesh_state(line, timestamp)
                if event:
                    timeline.mesh_states.append(event)
                    # Don't add to key_events - too verbose

            # Connection state changes
            elif '[MESH-CONN] STATE CHANGE' in line:
                event = parse_conn_state_change(line, timestamp)
                if event:
                    timeline.conn_changes.append(event)
                    timeline.key_events.append((timestamp, line.strip()))

            # BroadcastAll events
            elif '[MESH-TOPO] BroadcastAll' in line:
                timeline.key_events.append((timestamp, line.strip()))

            # NEW peer discovered
            elif '[MESH-TOPO] NEW peer' in line:
                timeline.key_events.append((timestamp, line.strip()))

    return timeline


def print_timeline(timeline: MeshTimeline):
    """Print analysis of a single timeline."""
    print(f"\n{'='*80}")
    print(f"LOG FILE: {timeline.filename}")
    print(f"Authority: {timeline.authority_id}")
    print(f"{'='*80}")

    # Summary counts
    print(f"\nEVENT COUNTS:")
    print(f"  Topology sends:     {len(timeline.topology_sends)}")
    print(f"  Topology broadcasts:{len(timeline.topology_broadcasts)}")
    print(f"  Topology receives:  {len(timeline.topology_recvs)}")
    print(f"  Mesh state snapshots: {len(timeline.mesh_states)}")
    print(f"  Connection changes: {len(timeline.conn_changes)}")

    # Connection state changes
    if timeline.conn_changes:
        print(f"\nCONNECTION STATE CHANGES:")
        for cc in timeline.conn_changes:
            reason = f" ({cc.reason})" if cc.reason else ""
            print(f"  {cc.timestamp:>8.2f}s: peer {cc.peer_auth}: {cc.old_state} -> {cc.new_state}{reason}")

    # Topology sends
    if timeline.topology_sends:
        print(f"\nTOPOLOGY SENDS:")
        for ts in timeline.topology_sends:
            print(f"  {ts.timestamp:>8.2f}s: -> client {ts.recipient_auth}, {ts.peer_count} peers: {ts.peers}")

    # Topology receives
    if timeline.topology_recvs:
        print(f"\nTOPOLOGY RECEIVES:")
        for tr in timeline.topology_recvs:
            print(f"  {tr.timestamp:>8.2f}s: received {tr.peer_count} peers (epoch={tr.epoch}): {tr.peers}")

            # Find matching summary
            for summ in timeline.recv_summaries:
                if abs(summ.timestamp - tr.timestamp) < 0.1:
                    print(f"             -> new={summ.new_discoveries} skipSelf={summ.skipped_self} skipConn={summ.skipped_connected}")
                    break

    # Mesh state snapshots (show last few and any during failover)
    if timeline.mesh_states:
        print(f"\nMESH STATE SNAPSHOTS (last 5 + failover):")

        # Find failover-related states
        failover_states = [ms for ms in timeline.mesh_states if 'isFailover=True' in ms.context or 'BroadcastAll' in ms.context]

        # Get last 5 states
        recent_states = timeline.mesh_states[-5:]

        # Combine and dedupe
        shown_timestamps = set()
        states_to_show = []

        for ms in failover_states:
            if ms.timestamp not in shown_timestamps:
                states_to_show.append(ms)
                shown_timestamps.add(ms.timestamp)

        for ms in recent_states:
            if ms.timestamp not in shown_timestamps:
                states_to_show.append(ms)
                shown_timestamps.add(ms.timestamp)

        states_to_show.sort(key=lambda x: x.timestamp)

        for ms in states_to_show:
            outbound_str = ", ".join(f"{k}:{v}" for k, v in ms.outbound_conns.items())
            inbound_str = ", ".join(str(p) for p in ms.inbound_peers)
            server_tag = " [SERVER]" if ms.is_server else ""
            print(f"  {ms.timestamp:>8.2f}s: [{ms.context}]{server_tag}")
            print(f"             outbound: [{outbound_str}]")
            print(f"             inbound:  [{inbound_str}]")


def correlate_timelines(timelines: List[MeshTimeline]):
    """Correlate mesh events across multiple log files."""
    if len(timelines) < 2:
        return

    print(f"\n{'='*80}")
    print("CROSS-NODE MESH ANALYSIS")
    print(f"{'='*80}")

    # Build a timeline of all mesh states across all nodes
    all_states = []
    for t in timelines:
        for ms in t.mesh_states:
            all_states.append((ms.timestamp, t.authority_id, ms))

    all_states.sort(key=lambda x: x[0])

    # Find topology discrepancies
    print(f"\nTOPOLOGY CONSISTENCY CHECK:")

    # Group states by approximate time (within 1 second)
    time_groups = defaultdict(list)
    for ts, auth, state in all_states:
        time_bucket = int(ts)  # Group by second
        time_groups[time_bucket].append((auth, state))

    # Check each time bucket for discrepancies
    discrepancies_found = 0
    for time_bucket in sorted(time_groups.keys()):
        states_at_time = time_groups[time_bucket]
        if len(states_at_time) < 2:
            continue

        # Compare outbound connections
        peer_views = {}  # auth -> set of peers they know about
        for auth, state in states_at_time:
            peers = set(state.outbound_conns.keys()) | set(state.inbound_peers)
            peer_views[auth] = peers

        # Check if views are consistent
        all_peers = set()
        for peers in peer_views.values():
            all_peers |= peers

        for auth, peers in peer_views.items():
            missing = all_peers - peers - {auth}  # Exclude self
            if missing:
                discrepancies_found += 1
                print(f"  t={time_bucket}s: Authority {auth} missing peers: {missing}")

    if discrepancies_found == 0:
        print(f"  [OK] No topology discrepancies detected")
    else:
        print(f"  [WARNING] {discrepancies_found} topology discrepancies found!")

    # Show mesh view at key moments
    print(f"\nMESH VIEW AT FINAL STATE:")
    for t in timelines:
        if t.mesh_states:
            final_state = t.mesh_states[-1]
            outbound_str = ", ".join(f"{k}:{v}" for k, v in final_state.outbound_conns.items())
            inbound_str = ", ".join(str(p) for p in final_state.inbound_peers)
            server_tag = " [SERVER]" if final_state.is_server else ""
            print(f"  Authority {t.authority_id}{server_tag}:")
            print(f"    outbound: [{outbound_str}]")
            print(f"    inbound:  [{inbound_str}]")

    # Check for split-brain (multiple servers)
    servers = [t for t in timelines if t.mesh_states and t.mesh_states[-1].is_server]
    if len(servers) > 1:
        print(f"\n  [CRITICAL] SPLIT-BRAIN DETECTED!")
        print(f"  Multiple nodes believe they are server:")
        for t in servers:
            print(f"    - Authority {t.authority_id} ({t.filename})")


def find_mesh_issues(timelines: List[MeshTimeline]):
    """Find specific mesh issues that could cause failover problems."""
    print(f"\n{'='*80}")
    print("MESH ISSUE DETECTION")
    print(f"{'='*80}")

    issues = []

    for t in timelines:
        auth = t.authority_id

        # Check if any topology receives had 0 new discoveries when peers were sent
        for recv in t.topology_recvs:
            if recv.peer_count > 0:
                # Find matching summary
                for summ in t.recv_summaries:
                    if abs(summ.timestamp - recv.timestamp) < 0.1:
                        if summ.new_discoveries == 0 and summ.skipped_self == 0:
                            # All peers were skipped as already connected
                            issues.append(f"Authority {auth} at t={recv.timestamp:.2f}s: "
                                        f"Received {recv.peer_count} peers but discovered 0 "
                                        f"(skipped {summ.skipped_connected} as connected)")
                        break

        # Check for connections stuck in non-Active state during failover
        failover_states = [ms for ms in t.mesh_states if 'isFailover=True' in ms.context]
        for ms in failover_states:
            for peer, state in ms.outbound_conns.items():
                if state not in ['Connected', 'Active']:
                    issues.append(f"Authority {auth} at t={ms.timestamp:.2f}s: "
                                f"Peer {peer} in unexpected state '{state}' during failover")

        # Check for empty mesh during promotion
        broadcast_states = [ms for ms in t.mesh_states if 'BroadcastAll' in ms.context]
        for ms in broadcast_states:
            if not ms.outbound_conns and not ms.inbound_peers:
                issues.append(f"Authority {auth} at t={ms.timestamp:.2f}s: "
                            f"Empty mesh during BroadcastAll - no peers known!")

    if issues:
        print(f"\n[ISSUES FOUND]:")
        for issue in issues:
            print(f"  - {issue}")
    else:
        print(f"\n[OK] No obvious mesh issues detected")


def print_summary(timelines: List[MeshTimeline]):
    """Print overall summary."""
    print(f"\n{'='*80}")
    print("MESH TOPOLOGY ANALYSIS SUMMARY")
    print(f"{'='*80}")

    print(f"\nFiles analyzed: {len(timelines)}")

    for t in timelines:
        total_events = (len(t.topology_sends) + len(t.topology_broadcasts) +
                       len(t.topology_recvs) + len(t.conn_changes))
        print(f"  {t.filename}: Authority {t.authority_id}, {total_events} mesh events")

    # Count total topology syncs
    total_sends = sum(len(t.topology_sends) for t in timelines)
    total_recvs = sum(len(t.topology_recvs) for t in timelines)
    total_conn_changes = sum(len(t.conn_changes) for t in timelines)

    print(f"\nTotal topology sends: {total_sends}")
    print(f"Total topology receives: {total_recvs}")
    print(f"Total connection state changes: {total_conn_changes}")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    # Expand glob patterns
    log_files = []
    for pattern in sys.argv[1:]:
        expanded = glob.glob(pattern)
        if expanded:
            log_files.extend(expanded)
        else:
            log_files.append(pattern)  # Try as literal path

    timelines = []

    for filepath in log_files:
        try:
            timeline = analyze_log_file(filepath)
            # Only include files that have mesh events
            if (timeline.topology_sends or timeline.topology_recvs or
                timeline.mesh_states or timeline.conn_changes):
                timelines.append(timeline)
                print(f"[OK] Analyzed: {filepath}")
            else:
                print(f"[SKIP] No mesh events in: {filepath}")
        except FileNotFoundError:
            print(f"[ERROR] File not found: {filepath}")
        except Exception as e:
            print(f"[ERROR] Failed to analyze {filepath}: {e}")

    if not timelines:
        print("\nNo log files with mesh events found.")
        sys.exit(1)

    # Print individual analyses
    for timeline in timelines:
        print_timeline(timeline)

    # Cross-node correlation
    correlate_timelines(timelines)

    # Issue detection
    find_mesh_issues(timelines)

    # Overall summary
    print_summary(timelines)


if __name__ == "__main__":
    main()
