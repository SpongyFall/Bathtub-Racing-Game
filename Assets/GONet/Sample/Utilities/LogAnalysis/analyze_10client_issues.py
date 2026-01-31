#!/usr/bin/env python3
"""
Analyze 10-client test issues:
1. Frozen GONetParticipant
2. 0/0 mesh status on first client
3. Unexpected self-promotion

Usage:
    python analyze_10client_issues.py <log_directory>
"""

import os
import sys
import re
from collections import defaultdict
from datetime import datetime
import glob

LOG_DIR = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs"

class ClientInfo:
    def __init__(self, pid):
        self.pid = pid
        self.authority = None
        self.role = None  # 'Host' or 'Client'
        self.init_time = None
        self.heartbeats_received = 0
        self.heartbeats_sent = 0
        self.mesh_status = []  # [(time, connected, total)]
        self.self_promoted = False
        self.promotion_reason = None
        self.promotion_time = None
        self.last_heartbeat_time = None
        self.heartbeat_gap_max = 0
        self.authority_changes = []  # [(time, old, new)]
        self.sync_data_received = False
        self.gonet_ready_events = []  # [(time, gonetid)]
        self.errors = []

def analyze_log(filepath, client):
    """Analyze a single log file."""
    prev_heartbeat_time = None

    try:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            line_num = 0
            for line in f:
                line_num += 1

                # Extract timestamp
                time_match = re.search(r'frame:\d+/([\d.]+)s\)', line)
                elapsed_time = float(time_match.group(1)) if time_match else 0

                # Get role
                role_match = re.search(r'Role: (\w+)', line)
                if role_match and client.role is None:
                    client.role = role_match.group(1)

                # Get authority ID changes
                auth_match = re.search(r'\[Client:(\d+)\]', line)
                if auth_match:
                    new_auth = int(auth_match.group(1))
                    if client.authority != new_auth:
                        client.authority_changes.append((elapsed_time, client.authority, new_auth))
                        client.authority = new_auth

                # Check for MyAuthorityId in init
                myauth_match = re.search(r'MyAuthorityId=(\d+)', line)
                if myauth_match and client.init_time is None:
                    client.init_time = elapsed_time

                # Count heartbeats received
                if '[Heartbeat-RECV]' in line:
                    client.heartbeats_received += 1
                    if prev_heartbeat_time is not None:
                        gap = elapsed_time - prev_heartbeat_time
                        if gap > client.heartbeat_gap_max:
                            client.heartbeat_gap_max = gap
                    prev_heartbeat_time = elapsed_time
                    client.last_heartbeat_time = elapsed_time

                # Count heartbeats sent
                if '[Heartbeat-SEND]' in line or 'SendHeartbeat' in line:
                    client.heartbeats_sent += 1

                # Track mesh status
                mesh_match = re.search(r'Mesh: (\d+)/(\d+)', line)
                if mesh_match:
                    connected = int(mesh_match.group(1))
                    total = int(mesh_match.group(2))
                    client.mesh_status.append((elapsed_time, connected, total))

                # Track self-promotion
                if 'self-promot' in line.lower():
                    client.self_promoted = True
                    client.promotion_time = elapsed_time
                    if 'tiebreaker' in line.lower():
                        client.promotion_reason = 'tiebreaker'
                    elif 'vice host' in line.lower():
                        client.promotion_reason = 'vice_host'
                    else:
                        client.promotion_reason = 'unknown'

                # Track promotion reasons
                if 'Vice host is invalid' in line:
                    client.errors.append((elapsed_time, 'VICE_HOST_INVALID', line.strip()))

                if 'deadHost' in line or 'host timeout' in line.lower():
                    client.errors.append((elapsed_time, 'HOST_TIMEOUT', line.strip()))

                # Track sync data
                if 'SYNC-DATA' in line or 'SyncBundle' in line:
                    client.sync_data_received = True

                # Track OnGONetReady
                if 'OnGONetReady' in line or 'DeserializeInitAllCompleted' in line:
                    gonetid_match = re.search(r'GONetId[=:]?\s*(\d+)', line)
                    gonetid = gonetid_match.group(1) if gonetid_match else 'unknown'
                    client.gonet_ready_events.append((elapsed_time, gonetid))

                # Track errors
                if '[Log:Error]' in line:
                    client.errors.append((elapsed_time, 'ERROR', line.strip()[:200]))

                # Stop after enough data (200MB files are huge)
                if line_num > 500000:
                    break

    except Exception as e:
        print(f"Error reading {filepath}: {e}")

def find_logs(log_dir, date_pattern="2025-12-11"):
    """Find all log files matching pattern."""
    pattern = os.path.join(log_dir, f"*-gonet-{date_pattern}.log")
    return glob.glob(pattern)

def main():
    if len(sys.argv) > 1:
        log_dir = sys.argv[1]
    else:
        log_dir = LOG_DIR

    log_files = find_logs(log_dir)

    if not log_files:
        print(f"No log files found in {log_dir}")
        sys.exit(1)

    print(f"Found {len(log_files)} log files")
    print("=" * 80)

    clients = {}

    for filepath in log_files:
        pid = os.path.basename(filepath).split('-')[0]
        client = ClientInfo(pid)
        print(f"Analyzing {pid}...", end=" ", flush=True)
        analyze_log(filepath, client)
        clients[pid] = client
        print(f"Authority: {client.authority}, Role: {client.role}")

    print("\n" + "=" * 80)
    print("ANALYSIS SUMMARY")
    print("=" * 80)

    # Sort by authority
    sorted_clients = sorted(clients.values(), key=lambda c: c.authority if c.authority else 9999)

    # Print summary table
    print("\n## Client Summary:")
    print(f"{'PID':<10} {'Auth':<6} {'Role':<8} {'HB Recv':<10} {'HB Gap':<10} {'Mesh':<12} {'Promoted':<10}")
    print("-" * 80)

    for c in sorted_clients:
        mesh_str = f"{c.mesh_status[-1][1]}/{c.mesh_status[-1][2]}" if c.mesh_status else "N/A"
        promoted_str = c.promotion_reason if c.self_promoted else "No"
        print(f"{c.pid:<10} {c.authority or 'N/A':<6} {c.role or 'N/A':<8} {c.heartbeats_received:<10} {c.heartbeat_gap_max:<10.2f} {mesh_str:<12} {promoted_str:<10}")

    # Issue 1: Authority ID 0
    print("\n## ISSUE 1: Authority ID Anomalies")
    for c in sorted_clients:
        if c.authority_changes:
            print(f"   PID {c.pid}: Authority changes: {c.authority_changes}")
        if c.authority == 0:
            print(f"   !!! PID {c.pid} has authority 0 (INVALID)")

    # Issue 2: Mesh status 0/0
    print("\n## ISSUE 2: Mesh Status 0/0")
    for c in sorted_clients:
        if c.mesh_status:
            final_mesh = c.mesh_status[-1]
            if final_mesh[1] == 0 and final_mesh[2] == 0:
                print(f"   !!! PID {c.pid} (Auth {c.authority}) has mesh 0/0")
                # Show mesh history
                print(f"       First 5 mesh updates: {c.mesh_status[:5]}")
                print(f"       Last 5 mesh updates: {c.mesh_status[-5:]}")

    # Issue 3: Self-promotion
    print("\n## ISSUE 3: Self-Promotion Events")
    for c in sorted_clients:
        if c.self_promoted:
            print(f"   !!! PID {c.pid} (Auth {c.authority}) self-promoted via {c.promotion_reason} at t={c.promotion_time:.2f}s")
            # Show relevant errors leading up to promotion
            pre_promotion_errors = [e for e in c.errors if e[0] < c.promotion_time + 1]
            if pre_promotion_errors:
                print(f"       Errors before promotion:")
                for t, etype, msg in pre_promotion_errors[-5:]:
                    print(f"         [{t:.2f}s] {etype}: {msg[:100]}")

    # Issue 4: Heartbeat analysis
    print("\n## ISSUE 4: Heartbeat Analysis")
    for c in sorted_clients:
        if c.heartbeat_gap_max > 2.0:
            print(f"   !!! PID {c.pid} (Auth {c.authority}) had max heartbeat gap of {c.heartbeat_gap_max:.2f}s")
        if c.heartbeats_received == 0 and c.role != 'Host':
            print(f"   !!! PID {c.pid} (Auth {c.authority}) received NO heartbeats!")

    # Issue 5: GONetReady analysis
    print("\n## ISSUE 5: GONetReady Events")
    for c in sorted_clients:
        print(f"   PID {c.pid} (Auth {c.authority}): {len(c.gonet_ready_events)} ready events")
        if not c.gonet_ready_events:
            print(f"      !!! No GONetReady events - possible frozen state")

if __name__ == '__main__':
    main()
