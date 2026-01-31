#!/usr/bin/env python3
"""
Comprehensive time synchronization analysis script for GONet logs.

Analyzes time sync behavior to diagnose issues like:
- Clients syncing to far-future time
- Excessive dilation/freezing
- RawElapsedTicks vs ElapsedTicks mismatches
- Invalid RTT calculations
- Offset anomalies

Usage:
    python analyze_timesync.py <log_file_path> [--client CLIENT_ID]
    python analyze_timesync.py "C:/Users/shash/AppData/LocalLow/Galore Interactive/GONetSandbox/logs/gonet-2025-11-14.log"
    python analyze_timesync.py "C:/Users/shash/AppData/LocalLow/Galore Interactive/GONetSandbox/logs/gonet-2025-11-14.log" --client 1

Output:
    - Summary statistics for each client
    - Timeline of time sync events
    - Anomaly detection (large offsets, long dilations, far-future syncs)
    - Visual timeline of offsets and adjustments
"""

import sys
import re
from dataclasses import dataclass
from typing import List, Optional, Dict
from collections import defaultdict
from datetime import datetime
import argparse


@dataclass
class TimeSyncEvent:
    """Single time sync event from logs"""
    line_number: int
    timestamp: str
    frame: int
    frame_time: float
    machine: str  # "Server", "Client:1", "Client:2", etc.
    event_type: str  # "ProcessTimeSync", "SetFromAuthority", "Dilation", etc.

    # ProcessTimeSync fields
    uid: Optional[int] = None
    t0_ms: Optional[float] = None  # Client send time
    t1_ms: Optional[float] = None  # Server response time
    t2_ms: Optional[float] = None  # Client receive time
    rtt_ms: Optional[float] = None
    server_ahead_ms: Optional[float] = None
    force_adjustment: Optional[bool] = None

    # ProcessTimeSync MATH fields
    min_rtt_ms: Optional[float] = None
    one_way_delay_ms: Optional[float] = None
    adjusted_server_time_ms: Optional[float] = None
    client_time_now_ms: Optional[float] = None
    difference_ms: Optional[float] = None
    target_time_ms: Optional[float] = None
    updated_min_rtt: Optional[bool] = None

    # SetFromAuthority fields
    raw_ticks_ms: Optional[float] = None
    effective_ticks_ms: Optional[float] = None
    from_authority_ms: Optional[float] = None
    old_offset_ms: Optional[float] = None
    new_offset_ms: Optional[float] = None
    adjustment_ms: Optional[float] = None
    mode: Optional[str] = None  # "Immediate", "Dilation", "Interpolation"
    force_immediate: Optional[bool] = None

    # Dilation fields
    dilation_duration_ms: Optional[float] = None
    dilation_start_offset_ms: Optional[float] = None
    dilation_target_offset_ms: Optional[float] = None
    dilation_start_time_ms: Optional[float] = None
    dilation_final_offset_ms: Optional[float] = None


def parse_log_line(line):
    """Parse a GONet log line into components."""
    # Format: [Level][Machine] (Thread:N) timestamp (frame:N/Ns) message
    pattern = r'\[(\w+)\]\[([^\]]+)\].*?\(frame:(\d+)/([\d.]+)s\)\s+(.+)'
    match = re.match(pattern, line)
    if match:
        return {
            'level': match.group(1),
            'machine': match.group(2),
            'frame': int(match.group(3)),
            'frame_time': float(match.group(4)),
            'message': match.group(5)
        }
    return None


def extract_float(pattern, text):
    """Extract float value from text using regex pattern."""
    match = re.search(pattern, text)
    return float(match.group(1)) if match else None


def extract_int(pattern, text):
    """Extract int value from text using regex pattern."""
    match = re.search(pattern, text)
    return int(match.group(1)) if match else None


def extract_bool(pattern, text):
    """Extract bool value from text using regex pattern."""
    match = re.search(pattern, text)
    if match:
        value = match.group(1)
        return value.lower() == 'true'
    return None


def parse_timesync_event(line_num, timestamp, parsed) -> Optional[TimeSyncEvent]:
    """Parse a time sync event from a log line."""
    msg = parsed['message']

    # ProcessTimeSync START
    if '[TimeSync-DIAG] ProcessTimeSync START' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='ProcessTimeSync_START'
        )

        event.uid = extract_int(r'UID:\s*(-?\d+)', msg)
        event.t0_ms = extract_float(r't0\(clientSend\)=([\d.]+)ms', msg)
        event.t1_ms = extract_float(r't1\(serverResponse\)=([\d.]+)ms', msg)
        event.t2_ms = extract_float(r't2\(clientReceive\)=([\d.]+)ms', msg)
        event.rtt_ms = extract_float(r'RTT=([\d.]+)ms', msg)
        event.server_ahead_ms = extract_float(r'ServerAheadBy=([\d.]+)ms', msg)
        event.force_adjustment = extract_bool(r'ForceAdjustment=(\w+)', msg)

        return event

    # ProcessTimeSync MATH
    elif '[TimeSync-DIAG] ProcessTimeSync MATH' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='ProcessTimeSync_MATH'
        )

        event.min_rtt_ms = extract_float(r'minRtt=([\d.]+)ms', msg)
        event.one_way_delay_ms = extract_float(r'oneWayDelay=([\d.]+)ms', msg)
        event.adjusted_server_time_ms = extract_float(r'adjustedServerTime=([\d.]+)ms', msg)
        event.client_time_now_ms = extract_float(r'clientTimeNow=([\d.]+)ms', msg)
        event.difference_ms = extract_float(r'DIFFERENCE=([-\d.]+)ms', msg)
        event.target_time_ms = extract_float(r'targetTime=([\d.]+)ms', msg)
        event.updated_min_rtt = extract_bool(r'updatedMinRtt=(\w+)', msg)

        return event

    # SetFromAuthority
    elif '[TimeSync-DIAG] SetFromAuthority:' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='SetFromAuthority'
        )

        event.raw_ticks_ms = extract_float(r'RawTicks=([\d.]+)ms', msg)
        event.effective_ticks_ms = extract_float(r'EffectiveTicks=([\d.]+)ms', msg)
        event.from_authority_ms = extract_float(r'FromAuthority=([\d.]+)ms', msg)
        event.old_offset_ms = extract_float(r'OldOffset=([-\d.]+)ms', msg)
        event.new_offset_ms = extract_float(r'NewOffset=([-\d.]+)ms', msg)
        event.adjustment_ms = extract_float(r'Adjustment=([-\d.]+)ms', msg)
        event.mode = re.search(r'Mode=(\w+)', msg).group(1) if re.search(r'Mode=(\w+)', msg) else None
        event.force_immediate = extract_bool(r'ForceImmediate=(\w+)', msg)

        return event

    # Dilation triggered
    elif '*** DILATION TRIGGERED ***' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='Dilation_TRIGGERED'
        )

        event.adjustment_ms = extract_float(r'Adjustment=([-\d.]+)ms', msg)
        event.dilation_duration_ms = extract_float(r'Duration=([\d.]+)ms', msg)

        return event

    # Dilation setup
    elif '[TimeSync-DIAG-DILATION] SETUP' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='Dilation_SETUP'
        )

        event.dilation_start_offset_ms = extract_float(r'StartOffset=([-\d.]+)ms', msg)
        event.dilation_target_offset_ms = extract_float(r'TargetOffset=([-\d.]+)ms', msg)
        event.dilation_start_time_ms = extract_float(r'StartTime=([\d.]+)ms', msg)
        event.dilation_duration_ms = extract_float(r'Duration=([\d.]+)ms', msg)

        return event

    # Dilation complete
    elif '*** DILATION COMPLETE ***' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='Dilation_COMPLETE'
        )

        event.dilation_duration_ms = extract_float(r'Duration=([\d.]+)ms', msg)
        event.dilation_final_offset_ms = extract_float(r'FinalOffset=([-\d.]+)ms', msg)

        return event

    # Time sync gap closed
    elif 'TIME SYNC GAP CLOSED' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='GAP_CLOSED'
        )
        return event

    # First time sync
    elif 'FIRST time sync completed' in msg:
        event = TimeSyncEvent(
            line_number=line_num,
            timestamp=timestamp,
            frame=parsed['frame'],
            frame_time=parsed['frame_time'],
            machine=parsed['machine'],
            event_type='FIRST_SYNC'
        )
        event.uid = extract_int(r'UID:\s*(-?\d+)', msg)
        return event

    return None


def analyze_timesync_events(log_file_path: str, target_client: Optional[str] = None):
    """Main analysis function."""

    print(f"\n{'='*80}")
    print(f"Time Synchronization Analysis")
    print(f"{'='*80}")
    print(f"Log file: {log_file_path}")
    if target_client:
        print(f"Filtering: Client:{target_client}")
    print()

    # Read log file
    try:
        with open(log_file_path, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
    except Exception as e:
        print(f"ERROR: Failed to read log file: {e}")
        return

    # Parse events
    events: List[TimeSyncEvent] = []
    events_by_machine: Dict[str, List[TimeSyncEvent]] = defaultdict(list)

    for i, line in enumerate(lines, 1):
        parsed = parse_log_line(line)
        if not parsed:
            continue

        # Filter by target client if specified
        if target_client and f"Client:{target_client}" not in parsed['machine']:
            continue

        # Extract timestamp
        ts_match = re.search(r'\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}', line)
        timestamp = ts_match.group(0) if ts_match else "Unknown"

        event = parse_timesync_event(i, timestamp, parsed)
        if event:
            events.append(event)
            events_by_machine[event.machine].append(event)

    if not events:
        print("No time sync events found in log file.")
        print("Make sure diagnostic logging is enabled in GONet.cs")
        return

    print(f"Found {len(events)} time sync events across {len(events_by_machine)} machines\n")

    # Analyze each machine
    for machine in sorted(events_by_machine.keys()):
        analyze_machine_timesync(machine, events_by_machine[machine])

    # Detect anomalies
    detect_anomalies(events)


def analyze_machine_timesync(machine: str, events: List[TimeSyncEvent]):
    """Analyze time sync events for a single machine."""

    print(f"\n{'='*80}")
    print(f"{machine} - Time Sync Summary")
    print(f"{'='*80}")

    # Count event types
    event_counts = defaultdict(int)
    for event in events:
        event_counts[event.event_type] += 1

    print(f"\nEvent counts:")
    for event_type, count in sorted(event_counts.items()):
        print(f"  {event_type}: {count}")

    # Analyze SetFromAuthority events
    setfrom_events = [e for e in events if e.event_type == 'SetFromAuthority']
    if setfrom_events:
        print(f"\n--- SetFromAuthority Analysis ---")

        immediate_count = sum(1 for e in setfrom_events if e.mode == 'Immediate')
        dilation_count = sum(1 for e in setfrom_events if e.mode == 'Dilation')
        interpolation_count = sum(1 for e in setfrom_events if e.mode == 'Interpolation')

        print(f"  Immediate adjustments: {immediate_count}")
        print(f"  Dilation adjustments: {dilation_count}")
        print(f"  Interpolation adjustments: {interpolation_count}")

        # Find largest adjustments
        adjustments = [(e, e.adjustment_ms) for e in setfrom_events if e.adjustment_ms is not None]
        if adjustments:
            max_adjustment = max(adjustments, key=lambda x: abs(x[1]))
            print(f"\n  Largest adjustment: {max_adjustment[1]:.2f}ms ({max_adjustment[0].mode}) at frame {max_adjustment[0].frame}")
            print(f"    OldOffset: {max_adjustment[0].old_offset_ms:.2f}ms")
            print(f"    NewOffset: {max_adjustment[0].new_offset_ms:.2f}ms")

            # Show timeline around largest adjustment
            print(f"\n  Timeline around largest adjustment:")
            show_timeline_around_event(events, max_adjustment[0], context=3)

    # Analyze dilation events
    dilation_events = [e for e in events if 'Dilation' in e.event_type]
    if dilation_events:
        print(f"\n--- Dilation Analysis ---")

        triggered = [e for e in dilation_events if e.event_type == 'Dilation_TRIGGERED']
        completed = [e for e in dilation_events if e.event_type == 'Dilation_COMPLETE']

        print(f"  Dilation triggered: {len(triggered)} times")
        print(f"  Dilation completed: {len(completed)} times")

        if triggered:
            durations = [e.dilation_duration_ms for e in triggered if e.dilation_duration_ms]
            if durations:
                total_dilation_ms = sum(durations)
                avg_duration = total_dilation_ms / len(durations)
                max_duration = max(durations)
                print(f"  Total dilation time: {total_dilation_ms:.2f}ms ({total_dilation_ms/1000:.2f}s)")
                print(f"  Average dilation duration: {avg_duration:.2f}ms")
                print(f"  Max dilation duration: {max_duration:.2f}ms ({max_duration/1000:.2f}s)")

                # Find longest dilation
                longest_idx = durations.index(max_duration)
                longest_event = triggered[longest_idx]
                print(f"\n  Longest dilation at frame {longest_event.frame}:")
                print(f"    Duration: {longest_event.dilation_duration_ms:.2f}ms ({longest_event.dilation_duration_ms/1000:.2f}s)")
                print(f"    Adjustment: {longest_event.adjustment_ms:.2f}ms")

    # Analyze ProcessTimeSync events
    process_events = [e for e in events if e.event_type == 'ProcessTimeSync_START']
    if process_events:
        print(f"\n--- ProcessTimeSync Analysis ---")

        rtts = [e.rtt_ms for e in process_events if e.rtt_ms is not None]
        if rtts:
            print(f"  RTT stats:")
            print(f"    Min: {min(rtts):.2f}ms")
            print(f"    Max: {max(rtts):.2f}ms")
            print(f"    Avg: {sum(rtts)/len(rtts):.2f}ms")

        server_ahead_values = [e.server_ahead_ms for e in process_events if e.server_ahead_ms is not None]
        if server_ahead_values:
            print(f"  Server ahead by:")
            print(f"    Min: {min(server_ahead_values):.2f}ms")
            print(f"    Max: {max(server_ahead_values):.2f}ms")
            print(f"    Avg: {sum(server_ahead_values)/len(server_ahead_values):.2f}ms")


def show_timeline_around_event(events: List[TimeSyncEvent], target_event: TimeSyncEvent, context: int = 3):
    """Show timeline of events around a specific event."""

    idx = events.index(target_event)
    start = max(0, idx - context)
    end = min(len(events), idx + context + 1)

    for i in range(start, end):
        event = events[i]
        marker = " >>>" if event == target_event else "    "
        print(f"{marker} Frame {event.frame:6d} | {event.event_type:25s} | {format_event_details(event)}")


def format_event_details(event: TimeSyncEvent) -> str:
    """Format event details for display."""
    if event.event_type == 'SetFromAuthority':
        return f"Mode={event.mode}, Adjustment={event.adjustment_ms:.2f}ms, NewOffset={event.new_offset_ms:.2f}ms"
    elif event.event_type == 'Dilation_TRIGGERED':
        return f"Duration={event.dilation_duration_ms:.2f}ms, Adjustment={event.adjustment_ms:.2f}ms"
    elif event.event_type == 'ProcessTimeSync_START':
        return f"RTT={event.rtt_ms:.2f}ms, ServerAheadBy={event.server_ahead_ms:.2f}ms"
    elif event.event_type == 'ProcessTimeSync_MATH':
        return f"Difference={event.difference_ms:.2f}ms, TargetTime={event.target_time_ms:.2f}ms"
    else:
        return ""


def detect_anomalies(events: List[TimeSyncEvent]):
    """Detect anomalies in time sync behavior."""

    print(f"\n{'='*80}")
    print(f"Anomaly Detection")
    print(f"{'='*80}")

    anomalies_found = False

    # Detect large offsets (>5 seconds)
    large_offset_threshold = 5000.0  # 5 seconds
    large_offsets = [e for e in events if e.event_type == 'SetFromAuthority'
                     and e.new_offset_ms is not None
                     and abs(e.new_offset_ms) > large_offset_threshold]

    if large_offsets:
        anomalies_found = True
        print(f"\n[ANOMALY] Large time offsets detected (>{large_offset_threshold/1000}s):")
        for event in large_offsets[:10]:  # Show first 10
            print(f"  {event.machine} @ frame {event.frame}: NewOffset={event.new_offset_ms:.2f}ms ({event.new_offset_ms/1000:.2f}s)")
            print(f"    Mode: {event.mode}, Adjustment: {event.adjustment_ms:.2f}ms")
            print(f"    RawTicks: {event.raw_ticks_ms:.2f}ms, FromAuthority: {event.from_authority_ms:.2f}ms")
            print()

    # Detect long dilations (>3 seconds)
    long_dilation_threshold = 3000.0  # 3 seconds
    long_dilations = [e for e in events if e.event_type == 'Dilation_TRIGGERED'
                      and e.dilation_duration_ms is not None
                      and e.dilation_duration_ms > long_dilation_threshold]

    if long_dilations:
        anomalies_found = True
        print(f"\n[ANOMALY] Long dilation periods detected (>{long_dilation_threshold/1000}s):")
        for event in long_dilations[:10]:
            print(f"  {event.machine} @ frame {event.frame}: Duration={event.dilation_duration_ms:.2f}ms ({event.dilation_duration_ms/1000:.2f}s)")
            print(f"    Adjustment: {event.adjustment_ms:.2f}ms")
            print()

    # Detect far-future syncs (target time >> client time)
    far_future_events = []
    for event in events:
        if event.event_type == 'ProcessTimeSync_MATH':
            if event.target_time_ms and event.client_time_now_ms:
                delta = event.target_time_ms - event.client_time_now_ms
                if delta > 5000.0:  # More than 5 seconds into future
                    far_future_events.append((event, delta))

    if far_future_events:
        anomalies_found = True
        print(f"\n[ANOMALY] Far-future time sync detected (>5s ahead):")
        for event, delta in far_future_events[:10]:
            print(f"  {event.machine} @ frame {event.frame}: TargetTime is {delta:.2f}ms ({delta/1000:.2f}s) ahead of ClientTimeNow")
            print(f"    ClientTimeNow: {event.client_time_now_ms:.2f}ms")
            print(f"    TargetTime: {event.target_time_ms:.2f}ms")
            print(f"    Difference: {event.difference_ms:.2f}ms")
            print()

    # Detect immediate mode overuse
    immediate_events = [e for e in events if e.event_type == 'SetFromAuthority' and e.mode == 'Immediate']
    if len(immediate_events) > 10:
        anomalies_found = True
        print(f"\n[ANOMALY] Excessive immediate mode adjustments: {len(immediate_events)}")
        print(f"  This may indicate time sync instability or incorrect implementation")
        print(f"  Expected: Immediate mode during initial sync, then interpolation/dilation")
        print()

    if not anomalies_found:
        print("\n[OK] No anomalies detected. Time sync appears healthy.")


def main():
    parser = argparse.ArgumentParser(description='Analyze GONet time synchronization logs')
    parser.add_argument('log_file', help='Path to GONet log file')
    parser.add_argument('--client', type=str, help='Filter events for specific client (e.g., "1" for Client:1)')

    args = parser.parse_args()

    analyze_timesync_events(args.log_file, args.client)


if __name__ == '__main__':
    main()
