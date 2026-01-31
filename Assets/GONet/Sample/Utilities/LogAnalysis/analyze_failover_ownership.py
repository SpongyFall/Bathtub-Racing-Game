#!/usr/bin/env python3
"""
Forensic analysis of failover ownership semantics.
Analyzes ProcessSpawnerDeath, GONetLocal lifecycle, and IsGONetReady status during failover.
"""

import re
import sys
from collections import defaultdict
from datetime import datetime

class FailoverOwnershipAnalyzer:
    def __init__(self):
        self.gnp_spawner_info = {}  # GONetId -> {name, spawnerPersistentId, ...}
        self.destroyed_gnps = []
        self.kept_gnps = []
        self.preserved_gnps = []
        self.gonetlocal_lookups = []  # Tracks GONetLocal lookup additions/removals
        self.host_not_ready = []
        self.isready_diagnostics = []
        self.authority_changes = []
        self.migrate_events = []
        self.process_spawner_death = {}
        self.gnp_failover_periodic = []  # Periodic GNP-FAILOVER logs
        self.errors = []
        self.warnings = []
        self.log_role = "UNK"
        self.log_authority = 0
        self.promotion_info = {}

    def parse_timestamp(self, line):
        """Extract timestamp from log line."""
        match = re.search(r'\((\d+\.\d+)s\)', line)
        if match:
            return float(match.group(1))
        return 0.0

    def parse_frame(self, line):
        """Extract frame number from log line."""
        match = re.search(r'\(frame:(\d+)/', line)
        if match:
            return int(match.group(1))
        return 0

    def analyze_log(self, filepath):
        """Analyze a single log file."""
        print(f"\n{'='*80}")
        print(f"ANALYZING: {filepath.split('/')[-1]}")
        print(f"{'='*80}")

        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()

        for line in lines:
            self.parse_line(line)

        self.print_analysis()

    def parse_line(self, line):
        """Parse a single log line."""
        timestamp = self.parse_timestamp(line)
        frame = self.parse_frame(line)

        # Detect role/authority
        if '[Client:' in line:
            match = re.search(r'\[Client:(\d+)\]', line)
            if match:
                self.log_authority = int(match.group(1))
                self.log_role = "CLIENT"
        elif '[Server]' in line:
            self.log_role = "SERVER"
            self.log_authority = 1023

        # ProcessSpawnerDeath scanning
        if '[Failover] ProcessSpawnerDeath scanning' in line:
            match = re.search(r'spawner ([A-F0-9]+)\. GNP count: (\d+)', line)
            if match:
                self.process_spawner_death = {
                    'timestamp': timestamp,
                    'spawner_persistent_id': match.group(1),
                    'gnp_count': int(match.group(2))
                }

        # ProcessSpawnerDeath complete
        if '[Failover] ProcessSpawnerDeath complete' in line:
            match = re.search(r'destroyed=(\d+), survived=(\d+)', line)
            if match:
                self.process_spawner_death['destroyed'] = int(match.group(1))
                self.process_spawner_death['survived'] = int(match.group(2))

        # GNP SpawnerPersistentId info
        if "GNP '" in line and 'SpawnerPersistentId:' in line:
            match = re.search(r"GNP '([^']+)' \(GONetId: (\d+)\) SpawnerPersistentId: ([A-F0-9]+)", line)
            if match:
                name, gonet_id, spawner_id = match.groups()
                is_scene_immune = '(SCENE-IMMUNE)' in line
                self.gnp_spawner_info[int(gonet_id)] = {
                    'name': name,
                    'gonet_id': int(gonet_id),
                    'spawner_persistent_id': spawner_id,
                    'is_scene_immune': is_scene_immune,
                    'timestamp': timestamp
                }

        # Keeping objects (IsMine=true)
        if "[Failover] Keeping '" in line and 'IsMine=true' in line:
            match = re.search(r"Keeping '([^']+)' \(GONetId: (\d+)\)", line)
            if match:
                self.kept_gnps.append({
                    'name': match.group(1),
                    'gonet_id': int(match.group(2)),
                    'reason': 'IsMine=true (adopted by new host)',
                    'timestamp': timestamp
                })

        # Keeping objects (belonged to client before promotion)
        if "[Failover] Keeping '" in line and 'belonged to this client' in line:
            match = re.search(r"Keeping '([^']+)' \(GONetId: (\d+)\).*OwnerAuthorityId=(\d+)", line)
            if match:
                self.kept_gnps.append({
                    'name': match.group(1),
                    'gonet_id': int(match.group(2)),
                    'reason': f'Pre-promotion ownership (OwnerAuthorityId={match.group(3)})',
                    'timestamp': timestamp
                })

        # Destroyed objects
        if "[Failover] Destroying '" in line:
            match = re.search(r"Destroying '([^']+)' \(GONetId: (\d+), SpawnerPersistentId: ([A-F0-9]+)\)", line)
            if match:
                self.destroyed_gnps.append({
                    'name': match.group(1),
                    'gonet_id': int(match.group(2)),
                    'spawner_persistent_id': match.group(3),
                    'timestamp': timestamp
                })

        # Preserved objects (DestroyWhenSpawnerLeaves=false)
        if "[Failover] Preserved '" in line:
            match = re.search(r"Preserved '([^']+)' \(GONetId: (\d+)\)", line)
            if match:
                self.preserved_gnps.append({
                    'name': match.group(1),
                    'gonet_id': int(match.group(2)),
                    'reason': 'DestroyWhenSpawnerLeaves=false',
                    'timestamp': timestamp
                })

        # GONetLocal lookup additions
        if '[GONetLocal] GONetLocal added to lookup' in line:
            match = re.search(r'OwnerAuthorityId: (\d+)', line)
            if match:
                self.gonetlocal_lookups.append({
                    'action': 'ADD',
                    'authority_id': int(match.group(1)),
                    'timestamp': timestamp,
                    'frame': frame
                })

        # GONetLocal server authority mapping
        if '[GONetLocal] Server authority mapping' in line:
            if 'already exists' in line:
                match = re.search(r'for (\d+)', line)
                if match:
                    self.gonetlocal_lookups.append({
                        'action': 'UPDATE',
                        'authority_id': int(match.group(1)),
                        'timestamp': timestamp,
                        'frame': frame
                    })

        # HOST-NOT-READY errors
        if '[HOST-NOT-READY]' in line:
            match = re.search(r"\[HOST-NOT-READY\] '([^']+)' \(GONetId=(\d+)\) not ready: (.+)$", line)
            if match:
                self.host_not_ready.append({
                    'name': match.group(1),
                    'gonet_id': int(match.group(2)),
                    'reason': match.group(3).strip(),
                    'timestamp': timestamp,
                    'frame': frame
                })

        # Migrated GNPs
        if '[Failover] Migrated GNP' in line:
            match = re.search(r"Migrated GNP '([^']+)' \(GONetId: (\d+)\): IsMine=(\w+), OwnerAuthorityId=(\d+), MyAuthorityId=(\d+)", line)
            if match:
                self.migrate_events.append({
                    'name': match.group(1),
                    'gonet_id': int(match.group(2)),
                    'is_mine': match.group(3),
                    'owner_authority_id': int(match.group(4)),
                    'my_authority_id': int(match.group(5)),
                    'timestamp': timestamp
                })

        # Authority promotion
        if '[Failover] Authority promoted from' in line:
            match = re.search(r'from (\d+) to (\d+)', line)
            if match:
                self.authority_changes.append({
                    'from': int(match.group(1)),
                    'to': int(match.group(2)),
                    'timestamp': timestamp
                })

        # Emergency promotion complete
        if 'EMERGENCY PROMOTION COMPLETE' in line:
            match = re.search(r'newHost=(\d+).*originalAuthority=(\d+).*epoch=(\d+).*previousHost=(\d+).*migratedGNPs=(\d+)', line)
            if match:
                self.promotion_info = {
                    'new_host': int(match.group(1)),
                    'original_authority': int(match.group(2)),
                    'epoch': int(match.group(3)),
                    'previous_host': int(match.group(4)),
                    'migrated_gnps': int(match.group(5)),
                    'timestamp': timestamp
                }

        # GNP-FAILOVER periodic logs
        if '[GNP-FAILOVER] Periodic:' in line:
            match = re.search(r'GONetId=(\d+), Owner=(\d+), IsMine=(\w+), IsReady=(\w+), MyAuth=(\d+), IsServer=(\w+)', line)
            if match:
                self.gnp_failover_periodic.append({
                    'gonet_id': int(match.group(1)),
                    'owner': int(match.group(2)),
                    'is_mine': match.group(3),
                    'is_ready': match.group(4),
                    'my_auth': int(match.group(5)),
                    'is_server': match.group(6),
                    'timestamp': timestamp
                })

        # Errors
        if '[Log:Error]' in line:
            self.errors.append({
                'message': line.strip(),
                'timestamp': timestamp
            })

        # Warnings about destruction
        if 'GONetParticipant being destroyed and IsMine is false' in line:
            match = re.search(r'GONetId: (\d+)', line)
            if match:
                self.warnings.append({
                    'type': 'DESTROY_NOT_MINE',
                    'gonet_id': int(match.group(1)),
                    'timestamp': timestamp
                })

    def print_analysis(self):
        """Print the analysis results."""
        print(f"\nLog Role: {self.log_role}, Authority: {self.log_authority}")

        # ProcessSpawnerDeath summary
        if self.process_spawner_death:
            print(f"\n{'='*60}")
            print("PROCESS SPAWNER DEATH")
            print(f"{'='*60}")
            print(f"  Timestamp: {self.process_spawner_death.get('timestamp', 'N/A')}s")
            print(f"  Dead Spawner: {self.process_spawner_death.get('spawner_persistent_id', 'N/A')}")
            print(f"  GNPs Scanned: {self.process_spawner_death.get('gnp_count', 'N/A')}")
            print(f"  Destroyed: {self.process_spawner_death.get('destroyed', 'N/A')}")
            print(f"  Survived: {self.process_spawner_death.get('survived', 'N/A')}")

        # GNP Spawner Info
        if self.gnp_spawner_info:
            print(f"\n{'='*60}")
            print("GNP SPAWNER INFORMATION")
            print(f"{'='*60}")
            for gonet_id, info in sorted(self.gnp_spawner_info.items()):
                immune = "(SCENE-IMMUNE)" if info['is_scene_immune'] else ""
                print(f"  [{gonet_id}] {info['name']}: Spawner={info['spawner_persistent_id']} {immune}")

        # Kept (survived) objects
        if self.kept_gnps:
            print(f"\n{'='*60}")
            print("OBJECTS KEPT (SURVIVED FAILOVER)")
            print(f"{'='*60}")
            for gnp in self.kept_gnps:
                print(f"  [{gnp['gonet_id']}] {gnp['name']}")
                print(f"       Reason: {gnp['reason']}")

        # Destroyed objects
        if self.destroyed_gnps:
            print(f"\n{'='*60}")
            print("OBJECTS DESTROYED DURING FAILOVER")
            print(f"{'='*60}")
            for gnp in self.destroyed_gnps:
                print(f"  [{gnp['gonet_id']}] {gnp['name']}")
                print(f"       Spawner: {gnp['spawner_persistent_id']}")

        # Preserved objects
        if self.preserved_gnps:
            print(f"\n{'='*60}")
            print("OBJECTS PRESERVED (DestroyWhenSpawnerLeaves=false)")
            print(f"{'='*60}")
            for gnp in self.preserved_gnps:
                print(f"  [{gnp['gonet_id']}] {gnp['name']}")

        # Migrated GNPs
        if self.migrate_events:
            print(f"\n{'='*60}")
            print("MIGRATED GNPs (Server-Owned)")
            print(f"{'='*60}")
            for event in self.migrate_events:
                print(f"  [{event['gonet_id']}] {event['name']}")
                print(f"       IsMine={event['is_mine']}, OwnerAuth={event['owner_authority_id']}, MyAuth={event['my_authority_id']}")

        # GONetLocal lookup changes
        if self.gonetlocal_lookups:
            print(f"\n{'='*60}")
            print("GONETLOCAL LOOKUP CHANGES")
            print(f"{'='*60}")
            for lookup in self.gonetlocal_lookups:
                print(f"  {lookup['timestamp']:.3f}s [{lookup['action']}] Authority {lookup['authority_id']} (frame {lookup['frame']})")

        # HOST-NOT-READY issues
        if self.host_not_ready:
            print(f"\n{'='*60}")
            print("HOST-NOT-READY ISSUES")
            print(f"{'='*60}")
            for issue in self.host_not_ready:
                print(f"  {issue['timestamp']:.3f}s [{issue['gonet_id']}] {issue['name']}")
                print(f"       Reason: {issue['reason']}")

        # Authority changes
        if self.authority_changes:
            print(f"\n{'='*60}")
            print("AUTHORITY CHANGES")
            print(f"{'='*60}")
            for change in self.authority_changes:
                print(f"  {change['timestamp']:.3f}s: {change['from']} -> {change['to']}")

        # Promotion info
        if self.promotion_info:
            print(f"\n{'='*60}")
            print("PROMOTION COMPLETE")
            print(f"{'='*60}")
            print(f"  New Host: {self.promotion_info['new_host']}")
            print(f"  Original Authority: {self.promotion_info['original_authority']}")
            print(f"  Epoch: {self.promotion_info['epoch']}")
            print(f"  Migrated GNPs: {self.promotion_info['migrated_gnps']}")

        # GNP-FAILOVER periodic (sample first and last few)
        if self.gnp_failover_periodic:
            print(f"\n{'='*60}")
            print(f"GNP-FAILOVER PERIODIC LOGS ({len(self.gnp_failover_periodic)} total)")
            print(f"{'='*60}")

            # Find transitions
            prev_state = None
            transitions = []
            for log in self.gnp_failover_periodic:
                state = (log['is_mine'], log['is_ready'], log['my_auth'], log['is_server'])
                if state != prev_state:
                    transitions.append(log)
                    prev_state = state

            if transitions:
                print("  State Transitions:")
                for t in transitions[:10]:  # First 10 transitions
                    print(f"    {t['timestamp']:.2f}s: IsMine={t['is_mine']}, IsReady={t['is_ready']}, MyAuth={t['my_auth']}, IsServer={t['is_server']}")
                if len(transitions) > 10:
                    print(f"    ... ({len(transitions)-10} more transitions)")

        # Warnings
        if self.warnings:
            print(f"\n{'='*60}")
            print(f"WARNINGS ({len(self.warnings)} total)")
            print(f"{'='*60}")
            for w in self.warnings[:10]:
                print(f"  {w['timestamp']:.3f}s: {w['type']} - GONetId {w['gonet_id']}")

        # Error summary
        error_types = defaultdict(int)
        for err in self.errors:
            # Categorize errors
            if 'GossipManager.Update' in err['message']:
                error_types['GossipManager.Update'] += 1
            elif 'Operation is not valid' in err['message']:
                error_types['Invalid Operation'] += 1
            else:
                error_types['Other'] += 1

        if error_types:
            print(f"\n{'='*60}")
            print(f"ERROR SUMMARY ({len(self.errors)} total)")
            print(f"{'='*60}")
            for error_type, count in sorted(error_types.items(), key=lambda x: -x[1]):
                print(f"  {error_type}: {count}")


def main():
    if len(sys.argv) < 2:
        print("Usage: python analyze_failover_ownership.py <logfile1> [logfile2] ...")
        print("\nAnalyzes ProcessSpawnerDeath, GONetLocal lifecycle, and IsGONetReady during failover.")
        sys.exit(1)

    for filepath in sys.argv[1:]:
        analyzer = FailoverOwnershipAnalyzer()
        try:
            analyzer.analyze_log(filepath)
        except Exception as e:
            print(f"Error analyzing {filepath}: {e}")
            import traceback
            traceback.print_exc()

    print(f"\n{'='*80}")
    print("ANALYSIS COMPLETE")
    print(f"{'='*80}")


if __name__ == '__main__':
    main()
