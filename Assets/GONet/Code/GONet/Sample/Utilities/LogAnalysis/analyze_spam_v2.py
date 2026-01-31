#!/usr/bin/env python3
"""Analyze GONet logs for spammy/verbose patterns - version 2."""

import re
import sys
import io
from collections import Counter, defaultdict
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

def extract_source_and_tag(line):
    """Extract source file:line and tag (if any) from log line."""
    # Extract source file and line
    source_match = re.search(r'\((\w+\.cs):(\d+)\)', line)
    source = f"{source_match.group(1)}:{source_match.group(2)}" if source_match else "unknown"

    # Extract tag in brackets like [BUNDLE-MISSING-PARTICIPANT] or [SteamworksTransport]
    tag_match = re.search(r'\[([A-Z][A-Z0-9_-]+)\]', line)
    tag = tag_match.group(1) if tag_match else ""

    # Get log level
    level_match = re.search(r'\[Log:(\w+)\]', line)
    level = level_match.group(1) if level_match else "Unknown"

    return source, tag, level

def get_message_key(line):
    """Get a normalized message key for grouping."""
    # Remove timestamp prefix
    line = re.sub(r'^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+ ', '', line)
    # Remove frame info in parens
    line = re.sub(r'\(frame:\d+/[\d.]+s\)', '', line)
    # Remove time in parens
    line = re.sub(r'\(\d+ \w+ \d{4} \d{2}:\d{2}:\d{2}\.\d+\)', '', line)
    # Remove thread info
    line = re.sub(r'\(Thread:\d+\)', '', line)

    # Check if this is a continuation line (no [Log:X] prefix)
    if not re.match(r'\[Log:', line):
        return None  # Skip continuation lines for now

    # Extract just the message portion after the source file
    msg_match = re.search(r'\(\w+\.cs:\d+\)\s*(.+)', line)
    if msg_match:
        msg = msg_match.group(1)
        # Normalize numbers
        msg = re.sub(r'\b\d+\.?\d*\b', 'N', msg)
        # Normalize GUIDs
        msg = re.sub(r'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', 'GUID', msg)
        return msg[:100]  # First 100 chars

    return line[:100]

def analyze_log(log_path):
    """Analyze log file and return statistics."""
    source_counts = Counter()
    tag_counts = Counter()
    level_counts = Counter()
    source_level_counts = defaultdict(Counter)
    examples = {}

    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if not line or not '[Log:' in line:
                continue

            source, tag, level = extract_source_and_tag(line)
            key = f"{source}|{tag}" if tag else source

            source_counts[key] += 1
            source_level_counts[source][level] += 1
            if tag:
                tag_counts[tag] += 1
            level_counts[level] += 1

            if key not in examples:
                examples[key] = line[:250]

    return source_counts, tag_counts, level_counts, source_level_counts, examples

if __name__ == '__main__':
    log_path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\64232-gonet-2025-12-18.log"

    print(f"Analyzing: {log_path}")
    print("=" * 100)

    source_counts, tag_counts, level_counts, source_level_counts, examples = analyze_log(log_path)

    total = sum(source_counts.values())
    print(f"\nTotal log lines analyzed: {total:,}")

    print(f"\n{'='*100}")
    print("BY LOG LEVEL:")
    print("=" * 100)
    for level, count in level_counts.most_common():
        pct = 100.0 * count / total
        print(f"  {level:12} {count:>10,} ({pct:5.1f}%)")

    print(f"\n{'='*100}")
    print("TOP 40 SPAMMIEST SOURCES (file:line|tag):")
    print("=" * 100)
    for i, (source, count) in enumerate(source_counts.most_common(40), 1):
        pct = 100.0 * count / total
        print(f"\n#{i}: {count:,} occurrences ({pct:.1f}%)")
        print(f"Source: {source}")
        print(f"Example: {examples[source][:200]}")

    print(f"\n{'='*100}")
    print("TOP TAGS:")
    print("=" * 100)
    for tag, count in tag_counts.most_common(20):
        pct = 100.0 * count / total
        print(f"  [{tag:40}] {count:>10,} ({pct:5.1f}%)")

    # Show Debug vs Warning breakdown per source
    print(f"\n{'='*100}")
    print("DEBUG LOGS BY SOURCE (these are candidates for removal):")
    print("=" * 100)
    debug_sources = [(src, counts['Debug']) for src, counts in source_level_counts.items() if counts['Debug'] > 0]
    debug_sources.sort(key=lambda x: -x[1])
    for src, count in debug_sources[:30]:
        print(f"  {src:45} {count:>10,} Debug logs")
