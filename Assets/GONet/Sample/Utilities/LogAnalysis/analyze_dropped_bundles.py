#!/usr/bin/env python3
"""
Analyzes GONet logs for dropped sync bundle patterns and timing issues.

Usage:
    python analyze_dropped_bundles.py <log_file_path> [--gonetid ID] [--detailed]

Examples:
    python analyze_dropped_bundles.py gonet-2025-11-23.log
    python analyze_dropped_bundles.py gonet-2025-11-23.log --gonetid 318465 --detailed
"""

import re
import sys
from datetime import datetime
from collections import defaultdict
from typing import Dict, List, Tuple, Optional

class ParticipantLifecycle:
    def __init__(self, gonetid: int):
        self.gonetid = gonetid
        self.registered_time: Optional[datetime] = None
        self.removed_time: Optional[datetime] = None
        self.ready_time: Optional[datetime] = None
        self.name: str = ""
        self.drops: List[Tuple[datetime, str]] = []  # (timestamp, message)
        self.client_or_server: str = ""  # Where it was registered

    def time_to_first_drop(self) -> Optional[float]:
        """Returns seconds between registration and first drop, or None"""
        if self.registered_time and self.drops:
            return (self.drops[0][0] - self.registered_time).total_seconds()
        return None

    def lifetime_seconds(self) -> Optional[float]:
        """Returns seconds between registration and removal, or None"""
        if self.registered_time and self.removed_time:
            return (self.removed_time - self.registered_time).total_seconds()
        return None


def parse_timestamp(line: str) -> Optional[datetime]:
    """Extract timestamp from GONet log line"""
    # Format: (DD MMM YYYY HH:MM:SS.mmm)
    match = re.search(r'\((\d+) (\w+) (\d+) (\d+):(\d+):(\d+)\.(\d+)\)', line)
    if match:
        day, month, year, hour, minute, second, ms = match.groups()
        month_map = {'Jan': 1, 'Feb': 2, 'Mar': 3, 'Apr': 4, 'May': 5, 'Jun': 6,
                     'Jul': 7, 'Aug': 8, 'Sep': 9, 'Oct': 10, 'Nov': 11, 'Dec': 12}
        return datetime(int(year), month_map[month], int(day),
                       int(hour), int(minute), int(second), int(ms) * 1000)
    return None


def extract_gonetid(line: str, pattern: str = r'GONetId:\s*(\d+)') -> Optional[int]:
    """Extract GONetId from log line"""
    match = re.search(pattern, line)
    return int(match.group(1)) if match else None


def extract_participant_name(line: str) -> Optional[str]:
    """Extract participant name from REGISTERED/REMOVED log line"""
    match = re.search(r"'([^']+)'", line)
    return match.group(1) if match else None


def extract_client_server_tag(line: str) -> str:
    """Extract [Server] or [Client:N] tag from log line"""
    match = re.search(r'\[(Server|Client:\d+)\]', line)
    return match.group(1) if match else ""


def analyze_log(log_path: str, target_gonetid: Optional[int] = None, detailed: bool = False):
    """Main analysis function"""

    print(f"\n{'='*80}")
    print(f"Analyzing: {log_path}")
    print(f"{'='*80}\n")

    participants: Dict[int, ParticipantLifecycle] = {}
    queue_backups: List[Tuple[datetime, int]] = []  # (timestamp, queue_size)
    id_batch_requests: List[Tuple[datetime, str]] = []  # (timestamp, message)

    total_drops = 0

    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            timestamp = parse_timestamp(line)
            if not timestamp:
                continue

            # Track participant registration
            if '[PARTICIPANT-REGISTERED]' in line:
                gonetid = extract_gonetid(line)
                if gonetid:
                    if gonetid not in participants:
                        participants[gonetid] = ParticipantLifecycle(gonetid)
                    participants[gonetid].registered_time = timestamp
                    participants[gonetid].name = extract_participant_name(line) or ""
                    participants[gonetid].client_or_server = extract_client_server_tag(line)

            # Track participant removal
            elif '[PARTICIPANT-REMOVED]' in line:
                gonetid = extract_gonetid(line)
                if gonetid and gonetid in participants:
                    participants[gonetid].removed_time = timestamp

            # Track OnGONetReady events
            elif 'OnGONetReady' in line and 'FIRED' in line:
                gonetid = extract_gonetid(line)
                if gonetid and gonetid in participants:
                    participants[gonetid].ready_time = timestamp

            # Track dropped bundles
            elif '[GONETREADY-DROP]' in line:
                total_drops += 1
                # Extract participant from "participant XXXXX missing/not ready"
                match = re.search(r'participant\s+(\d+)\s+missing', line)
                if match:
                    gonetid = int(match.group(1))
                    if gonetid not in participants:
                        participants[gonetid] = ParticipantLifecycle(gonetid)
                    participants[gonetid].drops.append((timestamp, line.strip()))

            # Track queue backups
            elif '[QUEUE-BACKUP]' in line:
                match = re.search(r'has\s+(\d+)\s+messages', line)
                if match:
                    queue_backups.append((timestamp, int(match.group(1))))

            # Track ID batch requests
            elif '[GONetIdBatch]' in line and 'low on IDs' in line:
                id_batch_requests.append((timestamp, line.strip()))

    # Analysis output
    print(f"[SUMMARY STATISTICS]")
    print(f"{'='*80}")
    print(f"Total participants tracked: {len(participants)}")
    print(f"Total dropped bundles: {total_drops}")
    print(f"Total queue backups: {len(queue_backups)}")
    print(f"Total ID batch requests: {len(id_batch_requests)}")

    # Find participants with drops
    participants_with_drops = {gid: p for gid, p in participants.items() if p.drops}
    print(f"Participants with drops: {len(participants_with_drops)}")
    print()

    # Analyze participants with drops
    if participants_with_drops:
        print(f"[ DROPPED BUNDLE ANALYSIS")
        print(f"{'='*80}")

        # Sort by number of drops (descending)
        sorted_drops = sorted(participants_with_drops.items(),
                             key=lambda x: len(x[1].drops), reverse=True)

        print(f"{'GONetId':<10} {'Name':<30} {'Drops':<8} {'Time to 1st Drop':<18} {'Lifetime'}")
        print(f"{'='*10} {'='*30} {'='*8} {'='*18} {'='*10}")

        for gonetid, p in sorted_drops[:20]:  # Top 20
            time_to_drop = p.time_to_first_drop()
            lifetime = p.lifetime_seconds()

            time_to_drop_str = f"{time_to_drop:.3f}s" if time_to_drop is not None else "N/A"
            lifetime_str = f"{lifetime:.3f}s" if lifetime is not None else "N/A"

            print(f"{gonetid:<10} {p.name[:30]:<30} {len(p.drops):<8} {time_to_drop_str:<18} {lifetime_str}")

        print()

        # Timing analysis
        print(f" TIMING ANALYSIS (Participants with Drops)")
        print(f"{'='*80}")

        delays = [p.time_to_first_drop() for p in participants_with_drops.values()
                 if p.time_to_first_drop() is not None]

        if delays:
            print(f"Min time to first drop: {min(delays):.3f}s")
            print(f"Max time to first drop: {max(delays):.3f}s")
            print(f"Avg time to first drop: {sum(delays)/len(delays):.3f}s")

            # Bucketize delays
            buckets = {
                '0-1s': sum(1 for d in delays if d < 1),
                '1-3s': sum(1 for d in delays if 1 <= d < 3),
                '3-5s': sum(1 for d in delays if 3 <= d < 5),
                '5-10s': sum(1 for d in delays if 5 <= d < 10),
                '>10s': sum(1 for d in delays if d >= 10),
            }

            print(f"\nDelay distribution:")
            for bucket, count in buckets.items():
                if count > 0:
                    bar = '#' * (count // 5 if count >= 5 else 1)
                    print(f"  {bucket:>8}: {count:>3} {bar}")
        else:
            print("No timing data available (participants registered on different node)")

        print()

    # Queue backup analysis
    if queue_backups:
        print(f"[ QUEUE BACKUP ANALYSIS")
        print(f"{'='*80}")

        queue_sizes = [size for _, size in queue_backups]
        print(f"Total backup warnings: {len(queue_backups)}")
        print(f"Min queue size: {min(queue_sizes)}")
        print(f"Max queue size: {max(queue_sizes)}")
        print(f"Avg queue size: {sum(queue_sizes)/len(queue_sizes):.1f}")

        # Timeline of backups
        if queue_backups:
            first_backup = queue_backups[0][0]
            last_backup = queue_backups[-1][0]
            duration = (last_backup - first_backup).total_seconds()
            print(f"Backup period: {duration:.1f}s ({first_backup.strftime('%H:%M:%S')} - {last_backup.strftime('%H:%M:%S')})")

        print()

    # ID batch request analysis
    if id_batch_requests:
        print(f"[ ID BATCH REQUEST ANALYSIS")
        print(f"{'='*80}")
        print(f"Total requests: {len(id_batch_requests)}")

        for i, (timestamp, msg) in enumerate(id_batch_requests, 1):
            print(f"  Request {i}: {timestamp.strftime('%H:%M:%S.%f')[:-3]}")

        if len(id_batch_requests) > 1:
            intervals = []
            for i in range(1, len(id_batch_requests)):
                interval = (id_batch_requests[i][0] - id_batch_requests[i-1][0]).total_seconds()
                intervals.append(interval)
            print(f"\nAvg interval between requests: {sum(intervals)/len(intervals):.2f}s")

        print()

    # Detailed analysis for specific GONetId
    if target_gonetid:
        print(f"\n[ DETAILED ANALYSIS: GONetId {target_gonetid}")
        print(f"{'='*80}")

        if target_gonetid in participants:
            p = participants[target_gonetid]

            print(f"Name: {p.name}")
            print(f"Registered: {p.registered_time.strftime('%H:%M:%S.%f')[:-3] if p.registered_time else 'N/A'} ({p.client_or_server})")
            print(f"Ready: {p.ready_time.strftime('%H:%M:%S.%f')[:-3] if p.ready_time else 'N/A'}")
            print(f"Removed: {p.removed_time.strftime('%H:%M:%S.%f')[:-3] if p.removed_time else 'N/A'}")
            print(f"Total drops: {len(p.drops)}")

            if p.time_to_first_drop():
                print(f"Time to first drop: {p.time_to_first_drop():.3f}s")

            if p.lifetime_seconds():
                print(f"Lifetime: {p.lifetime_seconds():.3f}s")

            if detailed and p.drops:
                print(f"\nAll {len(p.drops)} dropped bundle events:")
                print(f"{'='*80}")
                for i, (ts, msg) in enumerate(p.drops, 1):
                    # Extract just the relevant part of the message
                    short_msg = msg.split('(GONet.cs')[0].strip()
                    print(f"  {i:>3}. {ts.strftime('%H:%M:%S.%f')[:-3]} - {short_msg[-80:]}")
        else:
            print(f" GONetId {target_gonetid} not found in log")

        print()

    # Recommendations
    print(f"[ RECOMMENDATIONS")
    print(f"{'='*80}")

    if total_drops > 0:
        print(f" {total_drops} unreliable sync bundles were dropped")
        print(f"   -> Consider enabling: GONetGlobal.deferSyncBundlesWaitingForGONetReady = true")
        print(f"   -> Or investigate why OnGONetReady is delayed during high spawn rates")

    if queue_backups:
        avg_queue = sum(size for _, size in queue_backups) / len(queue_backups)
        if avg_queue > 10:
            print(f" Thread queue averaging {avg_queue:.1f} messages during backups")
            print(f"   -> Consider increasing worker thread count")
            print(f"   -> Or add flow control to prevent message storms")

    if not total_drops and not queue_backups:
        print(f" No issues detected - system handling load well!")

    print()


if __name__ == '__main__':
    import argparse

    parser = argparse.ArgumentParser(
        description='Analyze GONet logs for dropped bundle patterns and timing issues',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python analyze_dropped_bundles.py gonet-2025-11-23.log
  python analyze_dropped_bundles.py gonet-2025-11-23.log --gonetid 318465
  python analyze_dropped_bundles.py gonet-2025-11-23.log --gonetid 318465 --detailed
        """
    )

    parser.add_argument('log_file', help='Path to GONet log file')
    parser.add_argument('--gonetid', type=int, help='Focus on specific GONetId')
    parser.add_argument('--detailed', action='store_true', help='Show detailed drop messages')

    args = parser.parse_args()

    try:
        analyze_log(args.log_file, args.gonetid, args.detailed)
    except FileNotFoundError:
        print(f" Error: File not found: {args.log_file}")
        sys.exit(1)
    except Exception as e:
        print(f" Error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
