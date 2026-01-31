#!/usr/bin/env python3
"""Compare spawn flow between client and server to identify where spawns are lost"""

import re
from collections import defaultdict

def main():
    client_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-sure-works-now-under-excessive-load-too\84500-gonet-2025-12-02.log"
    server_log = r"C:\Users\shash\AppData\LocalLow\Galore Interactive\GONetSandbox\logs\reliable-sure-works-now-under-excessive-load-too\25416-gonet-2025-12-02.log"

    # Extract GONetIds from client at each stage
    print("=== CLIENT SPAWN FLOW ===\n")

    client_relay = set()
    client_enqueue = set()
    client_transport = set()
    client_compress = set()

    with open(client_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            # SPAWN-RELAY
            match = re.search(r'\[SPAWN-RELAY\].*GONetId=(\d+)', line)
            if match:
                client_relay.add(int(match.group(1)))

            # SPAWN-ENQUEUE
            match = re.search(r'\[SPAWN-ENQUEUE\].*GONetId=(\d+)', line)
            if match:
                client_enqueue.add(int(match.group(1)))

            # SPAWN-TRANSPORT
            match = re.search(r'\[SPAWN-TRANSPORT\].*GONetId=(\d+)', line)
            if match:
                client_transport.add(int(match.group(1)))

            # SPAWN-COMPRESS
            match = re.search(r'\[SPAWN-COMPRESS\].*GONetId=(\d+)', line)
            if match:
                client_compress.add(int(match.group(1)))

    print(f"SPAWN-RELAY (serialized): {len(client_relay)}")
    print(f"SPAWN-ENQUEUE (to queue): {len(client_enqueue)}")
    print(f"SPAWN-TRANSPORT (from queue): {len(client_transport)}")
    print(f"SPAWN-COMPRESS (after compress): {len(client_compress)}")

    # Extract GONetIds from server at each stage
    print("\n=== SERVER SPAWN FLOW ===\n")

    server_recv_msg = set()  # from enhanced RECV-MSG
    server_recv_decomp = set()  # from enhanced RECV-DECOMP
    server_enqueue = set()  # NEW: from ENQUEUE-PRE
    server_dequeue = set()  # NEW: from DEQUEUE
    server_deser = set()
    server_recv = set()

    with open(server_log, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            # NEW: RECV-MSG with GONetId extraction
            match = re.search(r'\[RECV-MSG\].*GONetId=(\d+)', line)
            if match:
                gid = int(match.group(1))
                if gid > 0 and gid < 10000000:  # Filter out garbage values
                    server_recv_msg.add(gid)

            # RECV-DECOMP with GONetId extraction
            match = re.search(r'\[RECV-DECOMP\].*GONetId=(\d+)', line)
            if match:
                gid = int(match.group(1))
                if gid > 0 and gid < 10000000:  # Filter out garbage values
                    server_recv_decomp.add(gid)

            # NEW: ENQUEUE-PRE with GONetId extraction
            match = re.search(r'\[ENQUEUE-PRE\].*GONetId=(\d+)', line)
            if match:
                gid = int(match.group(1))
                if gid > 0 and gid < 10000000:
                    server_enqueue.add(gid)

            # NEW: DEQUEUE with GONetId extraction
            match = re.search(r'\[DEQUEUE\].*GONetId=(\d+)', line)
            if match:
                gid = int(match.group(1))
                if gid > 0 and gid < 10000000:
                    server_dequeue.add(gid)

            # SPAWN-DESER
            match = re.search(r'\[SPAWN-DESER(?:-OTHER)?\].*GONetId=(\d+)', line)
            if match:
                server_deser.add(int(match.group(1)))

            # SPAWN-RECV
            match = re.search(r'\[SPAWN-RECV\].*GONetId=(\d+)', line)
            if match:
                server_recv.add(int(match.group(1)))

    print(f"RECV-MSG (received raw): {len(server_recv_msg)}")
    print(f"RECV-DECOMP (after decompress): {len(server_recv_decomp)}")
    print(f"ENQUEUE-PRE (before enqueue): {len(server_enqueue)}")
    print(f"DEQUEUE (dequeued): {len(server_dequeue)}")
    print(f"SPAWN-DESER (deserialized): {len(server_deser)}")
    print(f"SPAWN-RECV (processed): {len(server_recv)}")

    # Compare at each stage
    print("\n=== COMPARISON ===\n")

    # GONetIds that client compressed but server never saw in RECV-MSG
    lost_before_recv = client_compress - server_recv_msg
    print(f"Lost before RECV-MSG (compress but not recv_msg): {len(lost_before_recv)}")
    if lost_before_recv:
        print(f"First 20: {sorted(lost_before_recv)[:20]}")

    # GONetIds in RECV-MSG but not in RECV-DECOMP
    lost_at_decomp = server_recv_msg - server_recv_decomp
    print(f"\nLost at decompression (recv_msg but not recv_decomp): {len(lost_at_decomp)}")
    if lost_at_decomp:
        print(f"First 20: {sorted(lost_at_decomp)[:20]}")

    # GONetIds in RECV-DECOMP but not in ENQUEUE (lost before enqueue)
    lost_before_enqueue = server_recv_decomp - server_enqueue
    print(f"\nLost before enqueue (recv_decomp but not enqueue): {len(lost_before_enqueue)}")
    if lost_before_enqueue:
        print(f"First 20: {sorted(lost_before_enqueue)[:20]}")

    # GONetIds in ENQUEUE but not in DEQUEUE (lost in queue)
    lost_in_queue = server_enqueue - server_dequeue
    print(f"\nLost in queue (enqueue but not dequeue): {len(lost_in_queue)}")
    if lost_in_queue:
        print(f"First 20: {sorted(lost_in_queue)[:20]}")

    # GONetIds in DEQUEUE but not in SPAWN-DESER (lost after dequeue)
    lost_after_dequeue = server_dequeue - server_deser
    print(f"\nLost after dequeue (dequeue but not deser): {len(lost_after_dequeue)}")
    if lost_after_dequeue:
        print(f"First 20: {sorted(lost_after_dequeue)[:20]}")

    # Overall: client relay but server never deserialized
    lost = client_relay - server_deser
    print(f"\nTotal lost (relay but not deser): {len(lost)}")
    if lost:
        print(f"First 20 lost GONetIds: {sorted(lost)[:20]}")

    # GONetIds in enqueue but not in transport (queue issue)
    queue_lost = client_enqueue - client_transport
    print(f"\nLost in client queue (enqueue but not transport): {len(queue_lost)}")
    if queue_lost:
        print(f"First 20: {sorted(queue_lost)[:20]}")

    # Check if the stuck objects are in the lost set
    print(f"\n=== STUCK OBJECTS ANALYSIS ===\n")
    # Get first 10 lost GONetIds for detailed analysis
    lost_list = sorted(lost)[:10] if lost else []
    for gid in lost_list:
        in_relay = gid in client_relay
        in_client_enqueue = gid in client_enqueue
        in_transport = gid in client_transport
        in_compress = gid in client_compress
        in_recv_msg = gid in server_recv_msg
        in_recv_decomp = gid in server_recv_decomp
        in_server_enqueue = gid in server_enqueue
        in_server_dequeue = gid in server_dequeue
        in_deser = gid in server_deser
        print(f"GONetId {gid}:")
        print(f"  CLIENT: relay={in_relay}, enqueue={in_client_enqueue}, transport={in_transport}, compress={in_compress}")
        print(f"  SERVER: recv_msg={in_recv_msg}, recv_decomp={in_recv_decomp}, enqueue={in_server_enqueue}, dequeue={in_server_dequeue}, deser={in_deser}")

if __name__ == "__main__":
    main()
