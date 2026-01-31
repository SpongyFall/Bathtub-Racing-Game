#!/usr/bin/env python3
"""Analyze voluntary host migration logs to find split-brain issues."""

import re
import sys
from pathlib import Path
from collections import defaultdict

LOG_DIR = Path(r"C:/Users/shash/AppData/LocalLow/Galore Interactive/GONetSandbox/logs")

# Key patterns to search for
PATTERNS = {
    'handoff_init': re.compile(r'(graceful.*handoff|handoff.*initiat|voluntary.*migrat)', re.I),
    'handoff_accept': re.compile(r'(accept.*handoff|handoff.*accept|migration.*accept)', re.I),
    'promotion': re.compile(r'(promot|become.*server|IsServer.*true|server.*promot)', re.I),
    'demotion': re.compile(r'(demot|become.*client|IsServer.*false|server.*demot)', re.I),
    'self_promote': re.compile(r'(self.*promot|auto.*promot|timeout.*promot)', re.I),
    'grace_window': re.compile(r'(grace.*window|grace.*period|graceful.*handoff.*grace)', re.I),
    'disconnect': re.compile(r'(disconnect|connection.*lost|peer.*lost)', re.I),
    'split_brain': re.compile(r'(split.*brain|multiple.*server|both.*server)', re.I),
    'IsServer': re.compile(r'IsServer', re.I),
    'cleanup': re.compile(r'(cleanup|transient|leftover|stale|orphan)', re.I),
    'spawn': re.compile(r'(spawn|instantiat)', re.I),
    'authority': re.compile(r'(authority|authorit)', re.I),
    'dormant': re.compile(r'(dormant|standby|dormant.*server)', re.I),
}

def parse_timestamp(line):
    """Extract timestamp from log line."""
    match = re.search(r'\((\d+ \w+ \d+ \d+:\d+:\d+\.\d+)\)', line)
    if match:
        return match.group(1)
    return None

def analyze_log(filepath):
    """Analyze a single log file."""
    print(f"\n{'='*80}")
    print(f"ANALYZING: {filepath.name}")
    print(f"{'='*80}")

    results = defaultdict(list)

    try:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
    except Exception as e:
        print(f"Error reading {filepath}: {e}")
        return

    print(f"Total lines: {len(lines)}")

    # Find all matching lines
    for i, line in enumerate(lines):
        for pattern_name, pattern in PATTERNS.items():
            if pattern.search(line):
                results[pattern_name].append((i+1, line.strip()[:200]))

    # Print summary
    print("\n--- PATTERN MATCHES SUMMARY ---")
    for pattern_name, matches in sorted(results.items()):
        print(f"  {pattern_name}: {len(matches)} matches")

    # Print key events in detail
    key_patterns = ['handoff_init', 'handoff_accept', 'promotion', 'demotion',
                    'self_promote', 'grace_window', 'split_brain', 'disconnect']

    for pattern_name in key_patterns:
        if results[pattern_name]:
            print(f"\n--- {pattern_name.upper()} EVENTS ---")
            for line_num, line in results[pattern_name][:20]:  # First 20
                print(f"  L{line_num}: {line[:150]}")
            if len(results[pattern_name]) > 20:
                print(f"  ... and {len(results[pattern_name]) - 20} more")

def main():
    # Get the two most recent main log files
    log_files = sorted(LOG_DIR.glob("*-gonet-2025-12-20.log"),
                       key=lambda p: p.stat().st_mtime, reverse=True)

    print("Available log files:")
    for f in log_files:
        size_mb = f.stat().st_size / (1024*1024)
        print(f"  {f.name}: {size_mb:.2f} MB")

    for log_file in log_files[:2]:  # Analyze top 2
        analyze_log(log_file)

if __name__ == "__main__":
    main()
