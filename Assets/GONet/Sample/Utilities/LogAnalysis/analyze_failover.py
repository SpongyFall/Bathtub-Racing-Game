#!/usr/bin/env python3
"""
Analyze GONet hot standby failover logs.

Parses [Failover-TRACE], [Failover], and [Heartbeat-PROC] log lines to diagnose:
1. Failover state transitions per authority
2. Vice host validity checks
3. Tiebreaker candidate evaluation
4. Emergency promotion success/failure
5. Heartbeat recovery detection

Usage:
    python analyze_failover.py <log_file_path> [log_file_path2] [log_file_path3]

Example:
    python analyze_failover.py server.log client1.log client2.log
    python analyze_failover.py "C:/Users/.../logs/12345-gonet-2025-12-07.log"

The script can analyze multiple log files to correlate failover events across
server and clients. Process ID prefixes in filenames are handled automatically.
"""

import sys
import re
from collections import defaultdict
from dataclasses import dataclass, field
from typing import List, Dict, Optional, Tuple
from pathlib import Path


@dataclass
class StateTransition:
    """A failover state transition event."""
    timestamp: float
    from_state: str
    to_state: str
    authority_id: int


@dataclass
class BeginFailoverEvent:
    """BeginFailover START event with all context."""
    timestamp: float
    my_authority: int
    is_vice_host: bool
    vice_host_authority: int
    dead_host_authority: int
    server_authority: int


@dataclass
class ViceHostValidityCheck:
    """Vice host validity check result."""
    timestamp: float
    not_zero: bool
    not_dead_host: bool
    not_server: bool
    is_valid: bool


@dataclass
class GossipNodesCheck:
    """Gossip nodes enumeration during failover."""
    timestamp: float
    nodes: List[int]
    vice_host_alive: bool


@dataclass
class TiebreakerEvaluation:
    """Tiebreaker evaluation result."""
    timestamp: float
    my_authority: int
    dead_host: int
    server_authority: int
    candidates: List[int]
    excluded: List[int]
    lowest_authority: int
    i_am_lowest: bool


@dataclass
class PromotionComplete:
    """Emergency promotion completion event."""
    timestamp: float
    new_host: int
    original_authority: int
    epoch: int
    previous_host: int


@dataclass
class HeartbeatEvent:
    """Heartbeat processing event."""
    timestamp: float
    time_since_last: float
    host_authority: int
    is_first: bool


@dataclass
class FailoverTimeline:
    """Complete failover timeline for one log file."""
    filename: str
    role: str  # SVR, CLI, or unknown
    authority_id: int = 0

    state_transitions: List[StateTransition] = field(default_factory=list)
    begin_failover: Optional[BeginFailoverEvent] = None
    vice_host_check: Optional[ViceHostValidityCheck] = None
    gossip_check: Optional[GossipNodesCheck] = None
    tiebreaker_eval: Optional[TiebreakerEvaluation] = None
    promotion_complete: Optional[PromotionComplete] = None
    heartbeats: List[HeartbeatEvent] = field(default_factory=list)

    # Raw important log lines for reference
    key_events: List[Tuple[float, str]] = field(default_factory=list)


def extract_timestamp(line: str) -> float:
    """Extract elapsed time from log line."""
    match = re.search(r'\(frame:\d+/([\d.]+)s\)', line)
    if match:
        return float(match.group(1))
    return 0.0


def parse_state_transition(line: str, timestamp: float) -> Optional[StateTransition]:
    """Parse STATE TRANSITION log line."""
    match = re.search(
        r'\[Failover-TRACE\] STATE TRANSITION: (\w+) -> (\w+) \(myAuthority=(\d+)\)',
        line
    )
    if match:
        return StateTransition(
            timestamp=timestamp,
            from_state=match.group(1),
            to_state=match.group(2),
            authority_id=int(match.group(3))
        )
    return None


def parse_begin_failover(line: str, timestamp: float) -> Optional[BeginFailoverEvent]:
    """Parse BeginFailover START log line."""
    match = re.search(
        r'\[Failover-TRACE\] BeginFailover START: '
        r'myAuthority=(\d+), isViceHost=(\w+), '
        r'lastKnownViceHostAuthorityId=(\d+), '
        r'lastKnownHostAuthorityId=(\d+), '
        r'serverAuthorityId=(\d+)',
        line
    )
    if match:
        return BeginFailoverEvent(
            timestamp=timestamp,
            my_authority=int(match.group(1)),
            is_vice_host=match.group(2).lower() == 'true',
            vice_host_authority=int(match.group(3)),
            dead_host_authority=int(match.group(4)),
            server_authority=int(match.group(5))
        )
    return None


def parse_vice_host_validity(line: str, timestamp: float) -> Optional[ViceHostValidityCheck]:
    """Parse Vice host validity check log line."""
    match = re.search(
        r'\[Failover-TRACE\] Vice host validity check: '
        r'notZero=(\w+), notDeadHost=(\w+), notServer=(\w+), '
        r'isValid=(\w+)',
        line
    )
    if match:
        return ViceHostValidityCheck(
            timestamp=timestamp,
            not_zero=match.group(1).lower() == 'true',
            not_dead_host=match.group(2).lower() == 'true',
            not_server=match.group(3).lower() == 'true',
            is_valid=match.group(4).lower() == 'true'
        )
    return None


def parse_gossip_nodes(line: str, timestamp: float) -> Optional[GossipNodesCheck]:
    """Parse Gossip nodes log line."""
    match = re.search(
        r'\[Failover-TRACE\] Gossip nodes: \[([^\]]*)\] viceHostIsAlive=(\w+)',
        line
    )
    if match:
        nodes_str = match.group(1).strip()
        nodes = []
        if nodes_str:
            nodes = [int(n.strip()) for n in nodes_str.split(',') if n.strip()]
        return GossipNodesCheck(
            timestamp=timestamp,
            nodes=nodes,
            vice_host_alive=match.group(2).lower() == 'true'
        )
    return None


def parse_tiebreaker_eval(line: str, timestamp: float) -> Optional[TiebreakerEvaluation]:
    """Parse Tiebreaker evaluation log line."""
    match = re.search(
        r'\[Failover-TRACE\] Tiebreaker evaluation: '
        r'candidates=\[([^\]]*)\] excluded=\[([^\]]*)\] '
        r'lowestAuthorityId=(\d+), iAmLowest=(\w+)',
        line
    )
    if match:
        candidates_str = match.group(1).strip()
        excluded_str = match.group(2).strip()

        candidates = [int(n.strip()) for n in candidates_str.split(',') if n.strip()]
        excluded = [int(n.strip()) for n in excluded_str.split(',') if n.strip()]

        return TiebreakerEvaluation(
            timestamp=timestamp,
            my_authority=0,  # Will be filled from context
            dead_host=0,
            server_authority=0,
            candidates=candidates,
            excluded=excluded,
            lowest_authority=int(match.group(3)),
            i_am_lowest=match.group(4).lower() == 'true'
        )
    return None


def parse_tiebreaker_start(line: str, timestamp: float) -> Optional[Dict]:
    """Parse FallbackToTiebreaker START log line."""
    match = re.search(
        r'\[Failover-TRACE\] FallbackToTiebreaker START: '
        r'myAuthority=(\d+), '
        r'lastKnownHostAuthorityId=(\d+), '
        r'serverAuthorityId=(\d+)',
        line
    )
    if match:
        return {
            'my_authority': int(match.group(1)),
            'dead_host': int(match.group(2)),
            'server_authority': int(match.group(3))
        }
    return None


def parse_promotion_complete(line: str, timestamp: float) -> Optional[PromotionComplete]:
    """Parse EMERGENCY PROMOTION COMPLETE log line."""
    match = re.search(
        r'\[Failover-TRACE\] EMERGENCY PROMOTION COMPLETE: '
        r'newHost=(\d+), originalAuthority=(\d+), '
        r'epoch=(\d+), previousHost=(\d+)',
        line
    )
    if match:
        return PromotionComplete(
            timestamp=timestamp,
            new_host=int(match.group(1)),
            original_authority=int(match.group(2)),
            epoch=int(match.group(3)),
            previous_host=int(match.group(4))
        )
    return None


def parse_heartbeat(line: str, timestamp: float) -> Optional[HeartbeatEvent]:
    """Parse Heartbeat-PROC log line."""
    match = re.search(
        r'\[Heartbeat-PROC\] Heartbeat processed: time=[\d.]+, '
        r'sinceLast=([\d.]+)s, host=(\d+), isFirst=(\w+)',
        line
    )
    if match:
        return HeartbeatEvent(
            timestamp=timestamp,
            time_since_last=float(match.group(1)),
            host_authority=int(match.group(2)),
            is_first=match.group(3).lower() == 'true'
        )
    return None


def detect_role_from_log(filepath: str) -> Tuple[str, int]:
    """Detect if log is from server or client, and get authority ID."""
    role = "UNK"
    authority = 0

    with open(filepath, 'r', errors='ignore') as f:
        for line in f:
            # Check for server indicator
            if 'IsServer? True' in line or '[SVR]' in line:
                role = "SVR"
            elif 'IsServer? False' in line or '[CLI]' in line:
                role = "CLI"

            # Try to extract authority from various log patterns
            match = re.search(r'MyAuthorityId[=:]?\s*(\d+)', line)
            if match:
                authority = int(match.group(1))

            # Also check failover traces
            match = re.search(r'myAuthority=(\d+)', line)
            if match and authority == 0:
                authority = int(match.group(1))

            # If we found both, stop early
            if role != "UNK" and authority > 0:
                break

    return role, authority


def analyze_log_file(filepath: str) -> FailoverTimeline:
    """Analyze a single log file for failover events."""
    path = Path(filepath)
    role, authority = detect_role_from_log(filepath)

    timeline = FailoverTimeline(
        filename=path.name,
        role=role,
        authority_id=authority
    )

    tiebreaker_context = None

    with open(filepath, 'r', errors='ignore') as f:
        for line in f:
            timestamp = extract_timestamp(line)

            # State transitions
            if 'STATE TRANSITION' in line:
                event = parse_state_transition(line, timestamp)
                if event:
                    timeline.state_transitions.append(event)
                    timeline.key_events.append((timestamp, line.strip()))

            # BeginFailover START
            elif 'BeginFailover START' in line:
                event = parse_begin_failover(line, timestamp)
                if event:
                    timeline.begin_failover = event
                    timeline.authority_id = event.my_authority
                    timeline.key_events.append((timestamp, line.strip()))

            # Vice host validity check
            elif 'Vice host validity check' in line:
                event = parse_vice_host_validity(line, timestamp)
                if event:
                    timeline.vice_host_check = event
                    timeline.key_events.append((timestamp, line.strip()))

            # Gossip nodes
            elif 'Gossip nodes:' in line:
                event = parse_gossip_nodes(line, timestamp)
                if event:
                    timeline.gossip_check = event
                    timeline.key_events.append((timestamp, line.strip()))

            # Tiebreaker START (context for evaluation)
            elif 'FallbackToTiebreaker START' in line:
                tiebreaker_context = parse_tiebreaker_start(line, timestamp)
                timeline.key_events.append((timestamp, line.strip()))

            # Tiebreaker evaluation
            elif 'Tiebreaker evaluation' in line:
                event = parse_tiebreaker_eval(line, timestamp)
                if event and tiebreaker_context:
                    event.my_authority = tiebreaker_context['my_authority']
                    event.dead_host = tiebreaker_context['dead_host']
                    event.server_authority = tiebreaker_context['server_authority']
                    timeline.tiebreaker_eval = event
                    timeline.key_events.append((timestamp, line.strip()))

            # Promotion complete
            elif 'EMERGENCY PROMOTION COMPLETE' in line:
                event = parse_promotion_complete(line, timestamp)
                if event:
                    timeline.promotion_complete = event
                    timeline.key_events.append((timestamp, line.strip()))

            # Heartbeats
            elif '[Heartbeat-PROC]' in line:
                event = parse_heartbeat(line, timestamp)
                if event:
                    timeline.heartbeats.append(event)

            # Other key failover messages
            elif '[Failover]' in line and '[Failover-TRACE]' not in line:
                timeline.key_events.append((timestamp, line.strip()))

    return timeline


def print_timeline(timeline: FailoverTimeline):
    """Print analysis of a single timeline."""
    print(f"\n{'='*80}")
    print(f"LOG FILE: {timeline.filename}")
    print(f"Role: {timeline.role}, Authority: {timeline.authority_id}")
    print(f"{'='*80}")

    # Heartbeat summary
    if timeline.heartbeats:
        first_hb = timeline.heartbeats[0]
        last_hb = timeline.heartbeats[-1]
        print(f"\nHEARTBEATS:")
        print(f"  Total received: {len(timeline.heartbeats)}")
        print(f"  First at: {first_hb.timestamp:.2f}s (host={first_hb.host_authority})")
        print(f"  Last at: {last_hb.timestamp:.2f}s (host={last_hb.host_authority})")

        # Check for gaps
        gaps = [hb for hb in timeline.heartbeats if hb.time_since_last > 0.5]
        if gaps:
            print(f"  [WARNING] {len(gaps)} heartbeats with >0.5s gap")
            for gap in gaps[:3]:
                print(f"    - {gap.timestamp:.2f}s: gap of {gap.time_since_last:.2f}s")

    # BeginFailover analysis
    if timeline.begin_failover:
        bf = timeline.begin_failover
        print(f"\nBEGIN FAILOVER (t={bf.timestamp:.2f}s):")
        print(f"  My Authority: {bf.my_authority}")
        print(f"  Is Vice Host: {bf.is_vice_host}")
        print(f"  Vice Host Authority: {bf.vice_host_authority}")
        print(f"  Dead Host Authority: {bf.dead_host_authority}")
        print(f"  Server Authority ID: {bf.server_authority}")

        # Diagnose issues
        if bf.vice_host_authority == 0:
            print(f"  [ISSUE] Vice host is 0 (no vice host designated)")
        if bf.vice_host_authority == bf.dead_host_authority:
            print(f"  [ISSUE] Vice host == dead host ({bf.vice_host_authority})")
        if bf.vice_host_authority == bf.server_authority:
            print(f"  [ISSUE] Vice host == server authority ({bf.server_authority})")

    # Vice host validity
    if timeline.vice_host_check:
        vhc = timeline.vice_host_check
        print(f"\nVICE HOST VALIDITY CHECK (t={vhc.timestamp:.2f}s):")
        print(f"  Not Zero: {vhc.not_zero}")
        print(f"  Not Dead Host: {vhc.not_dead_host}")
        print(f"  Not Server: {vhc.not_server}")
        print(f"  IS VALID: {vhc.is_valid}")

        if not vhc.is_valid:
            reasons = []
            if not vhc.not_zero:
                reasons.append("vice host is 0")
            if not vhc.not_dead_host:
                reasons.append("vice host is dead host")
            if not vhc.not_server:
                reasons.append("vice host is server")
            print(f"  [EXPECTED] Invalid because: {', '.join(reasons)}")
            print(f"  [EXPECTED] Should fall back to tiebreaker")

    # Gossip nodes
    if timeline.gossip_check:
        gc = timeline.gossip_check
        print(f"\nGOSSIP NODES CHECK (t={gc.timestamp:.2f}s):")
        print(f"  Nodes in gossip: {gc.nodes}")
        print(f"  Vice Host Alive: {gc.vice_host_alive}")

    # Tiebreaker evaluation
    if timeline.tiebreaker_eval:
        te = timeline.tiebreaker_eval
        print(f"\nTIEBREAKER EVALUATION (t={te.timestamp:.2f}s):")
        print(f"  My Authority: {te.my_authority}")
        print(f"  Dead Host: {te.dead_host}")
        print(f"  Candidates: {te.candidates}")
        print(f"  Excluded: {te.excluded}")
        print(f"  Lowest Authority: {te.lowest_authority}")
        print(f"  I Am Lowest: {te.i_am_lowest}")

        if te.i_am_lowest:
            print(f"  [EXPECTED] Should self-promote")
        else:
            print(f"  [EXPECTED] Should wait for authority {te.lowest_authority} to promote")

        # Check for issues
        if te.dead_host in te.candidates:
            print(f"  [BUG] Dead host {te.dead_host} is in candidates!")
        if te.server_authority in te.candidates and te.server_authority != 0:
            print(f"  [BUG] Server authority {te.server_authority} is in candidates!")

    # State transitions
    if timeline.state_transitions:
        print(f"\nSTATE TRANSITIONS:")
        for st in timeline.state_transitions:
            print(f"  {st.timestamp:>8.2f}s: {st.from_state} -> {st.to_state}")

        # Check final state
        final = timeline.state_transitions[-1]
        if final.to_state == "Complete":
            print(f"  [SUCCESS] Failover completed")
        elif final.to_state == "SelfPromoting":
            print(f"  [IN PROGRESS] Self-promoting...")
        elif final.to_state == "WaitingForViceHost":
            print(f"  [STUCK?] Waiting for vice host")
        elif final.to_state == "WaitingForTiebreaker":
            print(f"  [WAITING] Waiting for another peer to promote")

    # Promotion complete
    if timeline.promotion_complete:
        pc = timeline.promotion_complete
        print(f"\nEMERGENCY PROMOTION COMPLETE (t={pc.timestamp:.2f}s):")
        print(f"  New Host: {pc.new_host}")
        print(f"  Original Authority: {pc.original_authority}")
        print(f"  Epoch: {pc.epoch}")
        print(f"  Previous Host: {pc.previous_host}")
        print(f"  [SUCCESS] This node is now the host!")


def correlate_timelines(timelines: List[FailoverTimeline]):
    """Correlate failover events across multiple log files."""
    if len(timelines) < 2:
        return

    print(f"\n{'='*80}")
    print("CROSS-LOG CORRELATION")
    print(f"{'='*80}")

    # Find who detected host death
    detectors = [(t.filename, t.begin_failover.timestamp, t.authority_id)
                 for t in timelines if t.begin_failover]
    if detectors:
        print(f"\nHOST DEATH DETECTION:")
        for fname, ts, auth in sorted(detectors, key=lambda x: x[1]):
            print(f"  {ts:>8.2f}s: Authority {auth} ({fname})")

        times = [ts for _, ts, _ in detectors]
        if len(times) > 1:
            spread = max(times) - min(times)
            print(f"  Detection spread: {spread:.3f}s")

    # Find who promoted
    promoters = [(t.filename, t.promotion_complete.timestamp,
                  t.promotion_complete.original_authority,
                  t.promotion_complete.new_host)
                 for t in timelines if t.promotion_complete]

    if promoters:
        print(f"\nPROMOTION EVENTS:")
        for fname, ts, orig, new in promoters:
            print(f"  {ts:>8.2f}s: Authority {orig} -> {new} ({fname})")

        if len(promoters) > 1:
            print(f"  [WARNING] Multiple promotions detected!")
    elif detectors:
        print(f"\n[PROBLEM] Host death detected but no promotion completed!")

        # Check who got stuck
        for t in timelines:
            if t.state_transitions:
                final = t.state_transitions[-1]
                if final.to_state not in ["Complete", "SelfPromoting"]:
                    print(f"  Authority {t.authority_id} stuck in {final.to_state}")

    # Check tiebreaker consistency
    tiebreakers = [(t.authority_id, t.tiebreaker_eval)
                   for t in timelines if t.tiebreaker_eval]
    if len(tiebreakers) > 1:
        print(f"\nTIEBREAKER CONSISTENCY:")
        expected_winner = None
        for auth, te in tiebreakers:
            if te.i_am_lowest:
                if expected_winner and expected_winner != auth:
                    print(f"  [CONFLICT] Both {expected_winner} and {auth} think they're lowest!")
                expected_winner = auth
            print(f"  Authority {auth}: lowest={te.lowest_authority}, iAmLowest={te.i_am_lowest}")

        if expected_winner:
            print(f"  Expected winner: Authority {expected_winner}")


def print_summary(timelines: List[FailoverTimeline]):
    """Print overall summary."""
    print(f"\n{'='*80}")
    print("FAILOVER ANALYSIS SUMMARY")
    print(f"{'='*80}")

    total_files = len(timelines)
    failover_detected = sum(1 for t in timelines if t.begin_failover)
    promotions = sum(1 for t in timelines if t.promotion_complete)

    print(f"\nFiles analyzed: {total_files}")
    print(f"Detected host death: {failover_detected}")
    print(f"Successful promotions: {promotions}")

    # Check for common issues
    issues = []

    for t in timelines:
        if t.begin_failover and t.begin_failover.vice_host_authority == 0:
            issues.append(f"Authority {t.authority_id}: Vice host was 0 (unset)")

        if t.tiebreaker_eval:
            te = t.tiebreaker_eval
            if te.dead_host in te.candidates:
                issues.append(f"Authority {t.authority_id}: Dead host in tiebreaker candidates")

        if t.state_transitions:
            final = t.state_transitions[-1]
            if final.to_state == "WaitingForViceHost":
                issues.append(f"Authority {t.authority_id}: Stuck in WaitingForViceHost")
            elif final.to_state == "WaitingForTiebreaker" and not any(
                    t2.promotion_complete for t2 in timelines):
                issues.append(f"Authority {t.authority_id}: Waiting for tiebreaker but no one promoted")

    if issues:
        print(f"\n[ISSUES DETECTED]:")
        for issue in issues:
            print(f"  - {issue}")
    else:
        print(f"\n[OK] No obvious issues detected")

    # Expected outcome
    if failover_detected > 0:
        print(f"\nEXPECTED OUTCOME:")
        if promotions == 1:
            for t in timelines:
                if t.promotion_complete:
                    print(f"  Authority {t.promotion_complete.original_authority} should be new host")
        elif promotions == 0:
            # Find lowest authority
            authorities = [t.authority_id for t in timelines if t.begin_failover]
            if authorities:
                lowest = min(authorities)
                print(f"  Authority {lowest} should have promoted (lowest ID)")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    log_files = sys.argv[1:]
    timelines = []

    for filepath in log_files:
        try:
            timeline = analyze_log_file(filepath)
            timelines.append(timeline)
        except FileNotFoundError:
            print(f"[ERROR] File not found: {filepath}")
        except Exception as e:
            print(f"[ERROR] Failed to analyze {filepath}: {e}")

    if not timelines:
        print("No valid log files to analyze.")
        sys.exit(1)

    # Print individual analyses
    for timeline in timelines:
        print_timeline(timeline)

    # Cross-log correlation
    correlate_timelines(timelines)

    # Overall summary
    print_summary(timelines)


if __name__ == "__main__":
    main()
