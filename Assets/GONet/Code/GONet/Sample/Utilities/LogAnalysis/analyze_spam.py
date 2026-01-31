#!/usr/bin/env python3
"""Analyze GONet logs for spammy/verbose patterns."""

import re
import sys
from collections import Counter
from pathlib import Path

def normalize_message(line):
    """Normalize a log line by removing variable parts (numbers, IDs, etc.)."""
    # Remove timestamp prefix
    line = re.sub(r'^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+ ', '', line)
    # Remove frame numbers like [12345]
    line = re.sub(r'\[\d+\]', '[N]', line)
    # Remove GONetId values
    line = re.sub(r'GONetId:\s*\d+', 'GONetId: N', line)
    line = re.sub(r'gonetId:\s*\d+', 'gonetId: N', line)
    # Remove authority IDs
    line = re.sub(r'authorityId:\s*\d+', 'authorityId: N', line)
    line = re.sub(r'AuthorityId:\s*\d+', 'AuthorityId: N', line)
    line = re.sub(r'OwnerAuthorityId:\s*\d+', 'OwnerAuthorityId: N', line)
    # Remove connection IDs
    line = re.sub(r'connectionId:\s*\d+', 'connectionId: N', line)
    line = re.sub(r'RemoteConnectionId:\s*\d+', 'RemoteConnectionId: N', line)
    # Remove elapsed time values
    line = re.sub(r'elapsedSeconds:\s*[\d.]+', 'elapsedSeconds: N', line)
    line = re.sub(r'ElapsedSeconds:\s*[\d.]+', 'ElapsedSeconds: N', line)
    # Remove hex addresses
    line = re.sub(r'0x[0-9a-fA-F]+', '0xN', line)
    # Remove GUIDs
    line = re.sub(r'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', 'GUID', line)
    # Remove generic numbers (but keep some context)
    line = re.sub(r':\s*\d+\.?\d*', ': N', line)
    line = re.sub(r'=\s*\d+\.?\d*', '= N', line)
    # Remove byte counts
    line = re.sub(r'\d+ bytes?', 'N bytes', line)
    return line.strip()

def analyze_log(log_path, top_n=50):
    """Analyze log file and return top N most frequent patterns."""
    pattern_counts = Counter()
    raw_examples = {}

    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            normalized = normalize_message(line)
            pattern_counts[normalized] += 1
            # Keep one raw example for each pattern
            if normalized not in raw_examples:
                raw_examples[normalized] = line[:200]  # Truncate long lines

    return pattern_counts.most_common(top_n), raw_examples

if __name__ == '__main__':
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

    log_path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\64232-gonet-2025-12-18.log"

    print(f"Analyzing: {log_path}")
    print("=" * 80)

    top_patterns, examples = analyze_log(log_path)

    total_lines = sum(count for _, count in top_patterns)

    print(f"\nTop {len(top_patterns)} most frequent log patterns:\n")
    for i, (pattern, count) in enumerate(top_patterns, 1):
        print(f"\n{'='*80}")
        print(f"#{i}: {count:,} occurrences")
        print(f"Pattern: {pattern[:150]}...")
        print(f"Example: {examples[pattern]}")
