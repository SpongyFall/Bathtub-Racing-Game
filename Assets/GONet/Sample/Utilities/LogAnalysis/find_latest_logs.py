#!/usr/bin/env python3
"""
Find the latest GONet log files from a log directory.

Scans the log directory for files matching the pattern *-gonet-YYYY-MM-DD.log
and groups them by date, showing the most recent ones.

Usage:
    python find_latest_logs.py [log_directory]

If no directory is specified, uses the default GONet log location:
    C:/Users/{username}/AppData/LocalLow/Galore Interactive/GONetSandbox/logs/

Example:
    python find_latest_logs.py
    python find_latest_logs.py "D:/custom/logs/path"
"""

import sys
import os
from pathlib import Path
from datetime import datetime
from collections import defaultdict
import re


def get_default_log_dir() -> Path:
    """Get the default GONet log directory."""
    username = os.getenv('USERNAME') or os.getenv('USER') or 'user'
    return Path(f"C:/Users/{username}/AppData/LocalLow/Galore Interactive/GONetSandbox/logs")


def parse_log_filename(filename: str) -> dict:
    """
    Parse GONet log filename to extract components.

    Format: {process_id}-gonet-{date}.log
    Example: 12345-gonet-2025-12-07.log
    """
    # Pattern: optional process ID prefix, then gonet-YYYY-MM-DD.log
    match = re.match(r'^(\d+)?-?gonet-(\d{4}-\d{2}-\d{2})\.log$', filename)
    if match:
        return {
            'process_id': match.group(1) or 'unknown',
            'date': match.group(2),
            'filename': filename
        }

    # Also try just gonet-YYYY-MM-DD.log without process ID
    match = re.match(r'^gonet-(\d{4}-\d{2}-\d{2})\.log$', filename)
    if match:
        return {
            'process_id': 'unknown',
            'date': match.group(1),
            'filename': filename
        }

    return None


def find_logs(log_dir: Path) -> dict:
    """
    Find all GONet log files grouped by date.

    Returns: dict of date -> list of (filepath, process_id, size, mtime)
    """
    logs_by_date = defaultdict(list)

    if not log_dir.exists():
        print(f"[WARNING] Log directory does not exist: {log_dir}")
        return logs_by_date

    for filepath in log_dir.glob("*gonet*.log"):
        parsed = parse_log_filename(filepath.name)
        if parsed:
            stat = filepath.stat()
            logs_by_date[parsed['date']].append({
                'path': filepath,
                'process_id': parsed['process_id'],
                'size': stat.st_size,
                'mtime': datetime.fromtimestamp(stat.st_mtime)
            })

    return logs_by_date


def format_size(size_bytes: int) -> str:
    """Format file size in human-readable format."""
    if size_bytes < 1024:
        return f"{size_bytes} B"
    elif size_bytes < 1024 * 1024:
        return f"{size_bytes / 1024:.1f} KB"
    else:
        return f"{size_bytes / (1024 * 1024):.1f} MB"


def main():
    # Determine log directory
    if len(sys.argv) > 1:
        log_dir = Path(sys.argv[1])
    else:
        log_dir = get_default_log_dir()

    print(f"Scanning: {log_dir}")
    print("=" * 80)

    logs_by_date = find_logs(log_dir)

    if not logs_by_date:
        print("No GONet log files found.")
        print(f"\nExpected format: *-gonet-YYYY-MM-DD.log or gonet-YYYY-MM-DD.log")
        return

    # Sort dates descending (most recent first)
    sorted_dates = sorted(logs_by_date.keys(), reverse=True)

    print(f"\nFound {sum(len(v) for v in logs_by_date.values())} log files across {len(sorted_dates)} dates")
    print()

    # Show most recent 3 dates
    for date in sorted_dates[:3]:
        logs = logs_by_date[date]
        print(f"DATE: {date} ({len(logs)} files)")
        print("-" * 80)

        # Sort by modification time descending
        logs.sort(key=lambda x: x['mtime'], reverse=True)

        for log in logs:
            mtime_str = log['mtime'].strftime("%H:%M:%S")
            size_str = format_size(log['size'])
            print(f"  [{log['process_id']:>8}] {mtime_str} {size_str:>10}  {log['path'].name}")

        print()

    # Print command to analyze latest logs
    if sorted_dates:
        latest_date = sorted_dates[0]
        latest_logs = logs_by_date[latest_date]

        print("=" * 80)
        print("SUGGESTED COMMANDS")
        print("=" * 80)

        # Single file analysis
        if latest_logs:
            latest = max(latest_logs, key=lambda x: x['mtime'])
            print(f"\nAnalyze most recent log:")
            print(f'  python analyze_failover.py "{latest["path"]}"')

        # Multi-file analysis (if multiple logs same date)
        if len(latest_logs) > 1:
            paths = ' '.join(f'"{log["path"]}"' for log in latest_logs)
            print(f"\nAnalyze all {len(latest_logs)} logs from {latest_date}:")
            print(f'  python analyze_failover.py {paths}')

        # Just print paths for copy-paste
        print(f"\nLog file paths for {latest_date}:")
        for log in latest_logs:
            print(f'  "{log["path"]}"')


if __name__ == "__main__":
    main()
