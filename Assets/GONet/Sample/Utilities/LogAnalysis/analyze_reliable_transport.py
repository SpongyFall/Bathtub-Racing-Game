#!/usr/bin/env python3
"""
Reliable Transport Analysis Tool for GONet
==========================================

Analyzes GONet logs to trace spawn events through the reliable transport layer.
Identifies where spawn events get lost in the transport chain.

Usage:
    python analyze_reliable_transport.py <log_file_or_directory>

Diagnostic Tags Parsed:
    Client-side (Send):
        [SPAWN-RELAY]       - Spawn event serialized and queued
        [SPAWN-TRANSPORT]   - Spawn event handed to transport layer
        [RELIABLE-SEQ]      - Message assigned reliable sequence number
        [RELIABLE-QUEUE]    - Message queued (sendBuffer full)
        [RELIABLE-XMIT]     - Packet transmitted containing message(s)
        [RELIABLE-RETR]     - Message retransmitted
        [RELIABLE-ACK]      - ACK received for packet

    Server-side (Receive):
        [RELIABLE-RECV-PKT] - Packet received
        [RELIABLE-RECV-MSG] - Message extracted from packet
        [RELIABLE-RECV-DUP] - Duplicate message ignored
        [RELIABLE-DELIVER]  - Message delivered to application
        [SPAWN-DESER]       - Spawn event deserialized
        [SPAWN-RECV]        - Spawn event processed

Analysis Output:
    - Message-level tracking: seq# → ACK status, retransmit count
    - Spawn event correlation: GONetId → transport trace
    - Lost message identification: messages sent but never ACKed/received
    - Retransmission analysis: which messages needed retransmit
"""

import re
import sys
import os
from collections import defaultdict
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Set, Tuple
from datetime import datetime

@dataclass
class ReliableMessage:
    """Tracks a single reliable message through the transport chain."""
    msg_seq: int
    bytes_size: int
    possible_gonet_id: int = 0
    timestamp_created: str = ""
    timestamp_xmit: str = ""
    timestamp_acked: str = ""
    pkt_seqs: List[int] = field(default_factory=list)  # Packets this message was transmitted in
    retransmit_count: int = 0
    acked: bool = False
    delivered: bool = False  # Server-side: was this delivered to application?

@dataclass
class SpawnEvent:
    """Tracks a spawn event through all stages."""
    gonet_id: int
    bytes_size: int = 0
    first_bytes_hex: str = ""

    # Client-side stages
    relay_timestamp: str = ""
    transport_timestamp: str = ""
    reliable_seq: int = -1

    # Server-side stages
    recv_msg_timestamp: str = ""
    deser_timestamp: str = ""
    recv_timestamp: str = ""

    # Outcome
    is_lost: bool = False
    loss_stage: str = ""  # Where it was lost

@dataclass
class ReliableTransportStats:
    """Aggregate statistics for reliable transport."""
    total_messages_sent: int = 0
    total_messages_acked: int = 0
    total_retransmissions: int = 0
    total_packets_sent: int = 0
    total_packets_recv: int = 0
    total_duplicates: int = 0
    messages_in_flight: int = 0  # Sent but not ACKed
    max_send_buffer_utilization: int = 0
    max_msg_queue_depth: int = 0

class ReliableTransportAnalyzer:
    """Analyzes reliable transport logs for message tracking."""

    def __init__(self):
        self.messages: Dict[int, ReliableMessage] = {}  # msg_seq -> message
        self.spawns: Dict[int, SpawnEvent] = {}  # gonet_id -> spawn event
        self.stats = ReliableTransportStats()

        # For correlation
        self.spawn_by_bytes: Dict[int, int] = {}  # bytes_size -> gonet_id (for simple correlation)

        # Compile regex patterns
        self.patterns = {
            'spawn_relay': re.compile(r'\[SPAWN-RELAY\].*GONetId=(\d+).*bytes=(\d+).*firstBytes=([A-Fa-f0-9]+)'),
            'spawn_transport': re.compile(r'\[SPAWN-TRANSPORT\].*bytes=(\d+)'),
            'reliable_seq': re.compile(r'\[RELIABLE-SEQ\].*seq=(\d+).*bytes=(\d+).*possibleGONetId=(\d+).*sendBuffer=(\d+)/(\d+)'),
            'reliable_queue': re.compile(r'\[RELIABLE-QUEUE\].*bytes=(\d+).*sendBuffer=(\d+)/(\d+).*msgQueue=(\d+)'),
            'reliable_xmit': re.compile(r'\[RELIABLE-XMIT\].*pktSeq=(\d+).*msgSeqs=\[([^\]]+)\].*totalBytes=(\d+).*RTT=([0-9.]+)'),
            'reliable_retr': re.compile(r'\[RELIABLE-RETR\].*msgSeq=(\d+).*attempt=(\d+).*bytes=(\d+)'),
            'reliable_ack': re.compile(r'\[RELIABLE-ACK\].*pktSeq=(\d+).*msgSeqs=\[([^\]]+)\].*RTT=([0-9.]+)'),
            'reliable_recv_pkt': re.compile(r'\[RELIABLE-RECV-PKT\].*pktSeq=(\d+).*bytes=(\d+)'),
            'reliable_recv_msg': re.compile(r'\[RELIABLE-RECV-MSG\].*msgSeq=(\d+).*bytes=(\d+).*possibleGONetId=(\d+)'),
            'reliable_recv_dup': re.compile(r'\[RELIABLE-RECV-DUP\].*msgSeq=(\d+).*bytes=(\d+)'),
            'reliable_deliver': re.compile(r'\[RELIABLE-DELIVER\].*msgSeq=(\d+).*bytes=(\d+)'),
            'spawn_deser': re.compile(r'\[SPAWN-DESER\].*GONetId=(\d+)'),
            'spawn_recv': re.compile(r'\[SPAWN-RECV\].*GONetId=(\d+)'),
            'timestamp': re.compile(r'^\[?(\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]?'),
        }

    def extract_timestamp(self, line: str) -> str:
        """Extract timestamp from log line."""
        match = self.patterns['timestamp'].search(line)
        return match.group(1) if match else ""

    def parse_line(self, line: str, source_type: str = 'unknown'):
        """Parse a single log line and update tracking state."""
        timestamp = self.extract_timestamp(line)

        # SPAWN-RELAY: Client spawning object
        match = self.patterns['spawn_relay'].search(line)
        if match:
            gonet_id = int(match.group(1))
            bytes_size = int(match.group(2))
            first_bytes = match.group(3)

            spawn = SpawnEvent(gonet_id=gonet_id, bytes_size=bytes_size, first_bytes_hex=first_bytes)
            spawn.relay_timestamp = timestamp
            self.spawns[gonet_id] = spawn
            self.spawn_by_bytes[bytes_size] = gonet_id
            return

        # SPAWN-TRANSPORT: Client sending to transport
        match = self.patterns['spawn_transport'].search(line)
        if match:
            bytes_size = int(match.group(1))
            # Try to correlate with spawn by bytes (imperfect but helpful)
            if bytes_size in self.spawn_by_bytes:
                gonet_id = self.spawn_by_bytes[bytes_size]
                if gonet_id in self.spawns:
                    self.spawns[gonet_id].transport_timestamp = timestamp
            return

        # RELIABLE-SEQ: Message assigned sequence number
        match = self.patterns['reliable_seq'].search(line)
        if match:
            msg_seq = int(match.group(1))
            bytes_size = int(match.group(2))
            possible_gonet_id = int(match.group(3))
            send_buffer_used = int(match.group(4))
            send_buffer_size = int(match.group(5))

            msg = ReliableMessage(msg_seq=msg_seq, bytes_size=bytes_size, possible_gonet_id=possible_gonet_id)
            msg.timestamp_created = timestamp
            self.messages[msg_seq] = msg
            self.stats.total_messages_sent += 1
            self.stats.max_send_buffer_utilization = max(self.stats.max_send_buffer_utilization, send_buffer_used)

            # Correlate with spawn if possible
            if possible_gonet_id in self.spawns:
                self.spawns[possible_gonet_id].reliable_seq = msg_seq
            return

        # RELIABLE-QUEUE: Message queued (sendBuffer full)
        match = self.patterns['reliable_queue'].search(line)
        if match:
            bytes_size = int(match.group(1))
            msg_queue_depth = int(match.group(4))
            self.stats.max_msg_queue_depth = max(self.stats.max_msg_queue_depth, msg_queue_depth)
            print(f"  WARNING: Message queued (sendBuffer FULL), msgQueue depth={msg_queue_depth}")
            return

        # RELIABLE-XMIT: Packet transmitted
        match = self.patterns['reliable_xmit'].search(line)
        if match:
            pkt_seq = int(match.group(1))
            msg_seqs_str = match.group(2)
            msg_seqs = [int(s.strip()) for s in msg_seqs_str.split(',') if s.strip()]

            self.stats.total_packets_sent += 1

            for msg_seq in msg_seqs:
                if msg_seq in self.messages:
                    msg = self.messages[msg_seq]
                    msg.pkt_seqs.append(pkt_seq)
                    if not msg.timestamp_xmit:
                        msg.timestamp_xmit = timestamp
            return

        # RELIABLE-RETR: Retransmission
        match = self.patterns['reliable_retr'].search(line)
        if match:
            msg_seq = int(match.group(1))
            attempt = int(match.group(2))

            self.stats.total_retransmissions += 1

            if msg_seq in self.messages:
                self.messages[msg_seq].retransmit_count = attempt
            return

        # RELIABLE-ACK: ACK received
        match = self.patterns['reliable_ack'].search(line)
        if match:
            pkt_seq = int(match.group(1))
            msg_seqs_str = match.group(2)
            msg_seqs = [int(s.strip()) for s in msg_seqs_str.split(',') if s.strip()]

            for msg_seq in msg_seqs:
                if msg_seq in self.messages:
                    msg = self.messages[msg_seq]
                    msg.acked = True
                    msg.timestamp_acked = timestamp
                    self.stats.total_messages_acked += 1
            return

        # RELIABLE-RECV-PKT: Server received packet
        match = self.patterns['reliable_recv_pkt'].search(line)
        if match:
            self.stats.total_packets_recv += 1
            return

        # RELIABLE-RECV-MSG: Server extracted message
        match = self.patterns['reliable_recv_msg'].search(line)
        if match:
            msg_seq = int(match.group(1))
            possible_gonet_id = int(match.group(3))

            if possible_gonet_id in self.spawns:
                self.spawns[possible_gonet_id].recv_msg_timestamp = timestamp
            return

        # RELIABLE-RECV-DUP: Duplicate message
        match = self.patterns['reliable_recv_dup'].search(line)
        if match:
            self.stats.total_duplicates += 1
            return

        # RELIABLE-DELIVER: Message delivered to app
        match = self.patterns['reliable_deliver'].search(line)
        if match:
            msg_seq = int(match.group(1))
            if msg_seq in self.messages:
                self.messages[msg_seq].delivered = True
            return

        # SPAWN-DESER: Server deserialized spawn
        match = self.patterns['spawn_deser'].search(line)
        if match:
            gonet_id = int(match.group(1))
            if gonet_id in self.spawns:
                self.spawns[gonet_id].deser_timestamp = timestamp
            return

        # SPAWN-RECV: Server processed spawn
        match = self.patterns['spawn_recv'].search(line)
        if match:
            gonet_id = int(match.group(1))
            if gonet_id in self.spawns:
                self.spawns[gonet_id].recv_timestamp = timestamp
            return

    def analyze_file(self, filepath: str, source_type: str = 'unknown'):
        """Analyze a single log file."""
        print(f"\nAnalyzing: {filepath}")

        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                self.parse_line(line, source_type)

    def analyze_directory(self, dirpath: str):
        """Analyze all log files in a directory."""
        for filename in sorted(os.listdir(dirpath)):
            if filename.endswith('.log') or filename.endswith('.txt'):
                filepath = os.path.join(dirpath, filename)

                # Determine source type from filename
                source_type = 'unknown'
                if 'server' in filename.lower():
                    source_type = 'server'
                elif 'client' in filename.lower():
                    source_type = 'client'

                self.analyze_file(filepath, source_type)

    def identify_lost_spawns(self) -> List[SpawnEvent]:
        """Identify spawn events that didn't complete the transport chain."""
        lost = []

        for gonet_id, spawn in self.spawns.items():
            # Check each stage
            if not spawn.relay_timestamp:
                spawn.is_lost = True
                spawn.loss_stage = "relay"
            elif spawn.reliable_seq == -1:
                spawn.is_lost = True
                spawn.loss_stage = "reliable_seq (never assigned sequence)"
            elif spawn.reliable_seq in self.messages:
                msg = self.messages[spawn.reliable_seq]
                if not msg.acked:
                    spawn.is_lost = True
                    spawn.loss_stage = f"ack (msgSeq={spawn.reliable_seq} never ACKed, retransmits={msg.retransmit_count})"

            # Server-side checks
            if spawn.relay_timestamp and not spawn.deser_timestamp and not spawn.recv_timestamp:
                spawn.is_lost = True
                if spawn.loss_stage:
                    spawn.loss_stage += " + server never received"
                else:
                    spawn.loss_stage = "server_receive (never reached server)"

            if spawn.is_lost:
                lost.append(spawn)

        return lost

    def identify_unacked_messages(self) -> List[ReliableMessage]:
        """Identify messages that were sent but never ACKed."""
        return [msg for msg in self.messages.values() if not msg.acked]

    def print_report(self):
        """Print comprehensive analysis report."""
        print("\n" + "="*80)
        print("RELIABLE TRANSPORT ANALYSIS REPORT")
        print("="*80)

        # Overall stats
        print("\n--- AGGREGATE STATISTICS ---")
        print(f"  Total messages sent:       {self.stats.total_messages_sent}")
        print(f"  Total messages ACKed:      {self.stats.total_messages_acked}")
        print(f"  ACK rate:                  {100*self.stats.total_messages_acked/max(1,self.stats.total_messages_sent):.1f}%")
        print(f"  Total retransmissions:     {self.stats.total_retransmissions}")
        print(f"  Total packets sent:        {self.stats.total_packets_sent}")
        print(f"  Total packets received:    {self.stats.total_packets_recv}")
        print(f"  Total duplicates:          {self.stats.total_duplicates}")
        print(f"  Max sendBuffer utilization:{self.stats.max_send_buffer_utilization}")
        print(f"  Max msgQueue depth:        {self.stats.max_msg_queue_depth}")

        # Unacked messages
        unacked = self.identify_unacked_messages()
        if unacked:
            print(f"\n--- UNACKED MESSAGES ({len(unacked)} total) ---")
            for msg in sorted(unacked, key=lambda m: m.msg_seq)[:20]:  # Show first 20
                print(f"  msgSeq={msg.msg_seq}: bytes={msg.bytes_size}, possibleGONetId={msg.possible_gonet_id}, retransmits={msg.retransmit_count}")
            if len(unacked) > 20:
                print(f"  ... and {len(unacked) - 20} more")
        else:
            print("\n--- ALL MESSAGES ACKed SUCCESSFULLY ---")

        # Spawn events
        print(f"\n--- SPAWN EVENT TRACKING ({len(self.spawns)} spawns) ---")

        lost_spawns = self.identify_lost_spawns()
        if lost_spawns:
            print(f"\n  LOST SPAWNS ({len(lost_spawns)} total):")
            for spawn in sorted(lost_spawns, key=lambda s: s.gonet_id):
                print(f"    GONetId={spawn.gonet_id}: bytes={spawn.bytes_size}, loss_stage='{spawn.loss_stage}'")
                if spawn.reliable_seq >= 0:
                    print(f"      reliable_seq={spawn.reliable_seq}")
        else:
            print("\n  All spawns completed successfully!")

        # Successful spawns summary
        successful = [s for s in self.spawns.values() if not s.is_lost]
        print(f"\n  SUCCESSFUL SPAWNS: {len(successful)}")

        # Loss rate
        if self.spawns:
            loss_rate = 100 * len(lost_spawns) / len(self.spawns)
            print(f"\n  SPAWN LOSS RATE: {loss_rate:.2f}% ({len(lost_spawns)}/{len(self.spawns)})")

        # Retransmission analysis
        retransmitted = [msg for msg in self.messages.values() if msg.retransmit_count > 0]
        if retransmitted:
            print(f"\n--- RETRANSMISSION ANALYSIS ({len(retransmitted)} messages retransmitted) ---")
            max_retransmits = max(msg.retransmit_count for msg in retransmitted)
            print(f"  Max retransmit count: {max_retransmits}")

            # Distribution
            retransmit_dist = defaultdict(int)
            for msg in retransmitted:
                retransmit_dist[msg.retransmit_count] += 1
            print("  Retransmit distribution:")
            for count in sorted(retransmit_dist.keys()):
                print(f"    {count} retransmits: {retransmit_dist[count]} messages")

def main():
    if len(sys.argv) < 2:
        print(__doc__)
        print("\nUsage: python analyze_reliable_transport.py <log_file_or_directory>")
        sys.exit(1)

    path = sys.argv[1]
    analyzer = ReliableTransportAnalyzer()

    if os.path.isfile(path):
        analyzer.analyze_file(path)
    elif os.path.isdir(path):
        analyzer.analyze_directory(path)
    else:
        print(f"Error: '{path}' is not a valid file or directory")
        sys.exit(1)

    analyzer.print_report()

if __name__ == '__main__':
    main()
