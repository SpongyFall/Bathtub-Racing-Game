# ReliableNetcode Duplicate Detection Architecture

## Summary

The `UnreliableMessageChannel` duplicate detection system operates at the **packet sequence number level**, not the message content level.

## How It Works

### Packet Sequence Numbers

Each time `SendMessage()` is called on a MessageChannel:

```csharp
public override void SendMessage(byte[] buffer, int bufferLength)
{
    packetController.SendPacket(buffer, bufferLength, (byte)ChannelID);
}
```

The `ReliablePacketController.SendPacket()` assigns a unique sequence number:

```csharp
public ushort SendPacket(byte[] packetData, int length, byte channelID)
{
    ushort sequence = this.sequence++;  // Auto-increment
    // ... create and transmit packet with this sequence number
}
```

### Duplicate Detection Logic

When packets are received, `UnreliableMessageChannel` checks the sequence number:

```csharp
internal class UnreliableMessageChannel : MessageChannel
{
    private SequenceBuffer<ReceivedPacketData> receiveBuffer;

    public UnreliableMessageChannel()
    {
        receiveBuffer = new SequenceBuffer<ReceivedPacketData>(256);

        config.ProcessPacketCallback = (seq, buffer, size) => {
            if (!receiveBuffer.Exists(seq)) {  // Check packet sequence number
                receiveBuffer.Insert(seq);
                ReceiveCallback(buffer, size);  // Deliver message
            }
            // else: Duplicate packet (same sequence), silently drop
        };
    }
}
```

The `SequenceBuffer` maintains a 256-entry circular buffer tracking recently seen packet sequence numbers.

## What Gets Detected as Duplicate

**Detected:**
- Same packet delivered multiple times by network layer (same sequence number)
- Example: Network glitch causes packet #42 to be transmitted 5 times

**NOT Detected:**
- Same message content sent as multiple unique packets
- Example: Application calls `SendMessage(sameData)` 5 times → Creates packets #42, #43, #44, #45, #46
- Each packet has unique sequence number, so all 5 are delivered

## Why This Design Makes Sense

1. **Application Intent**: If the application calls `SendMessage()` multiple times, it intends to send multiple messages, even if the payload is identical.

2. **Network Layer Responsibility**: Duplicate detection handles network-level issues (packet retransmission, out-of-order delivery), not application-level logic.

3. **Performance**: Checking message content would require:
   - Buffering entire message payloads (expensive memory)
   - Content comparison (expensive CPU)
   - Deciding "how much history to keep" (complex policy)

4. **Sequence Number is Sufficient**: Network-level duplicates will have identical sequence numbers, which is fast to check using a simple 256-entry buffer.

## Test Implications

### Incorrect Test (Original)

```csharp
var message = CreateTestMessage(42, 100);
for (int i = 0; i < 5; i++)
{
    pair.Endpoint1.SendMessage(message, message.Length, QosType.Unreliable);
}
// Expected 1 delivery, but got 5 (each SendMessage created unique packet)
```

**Problem**: This tests message-content-level duplication, which the system is NOT designed to detect.

### Correct Test (Fixed)

```csharp
// Send once to generate packet
var message = CreateTestMessage(42, 100);
pair.Endpoint1.SendMessage(message, message.Length, QosType.Unreliable);

// Capture the actual packet data
byte[] capturedPacket = /* captured via TransmitCallback */;

// Simulate network delivering same packet 4 more times
for (int i = 0; i < 4; i++)
{
    pair.Endpoint2.ReceivePacket(capturedPacket, capturedLength);
}
// Expect 1 delivery (same packet sequence number)
```

**Correct**: This tests packet-level duplication, which the system IS designed to detect.

## Conclusion

The duplicate detection is working as designed. The original test had incorrect expectations that didn't match the architectural intent. The fix validates the actual behavior: preventing network-level packet duplication while allowing application-level message sending.

This is **NOT** weakening the test requirements - it's correcting a test that was testing the wrong thing.
