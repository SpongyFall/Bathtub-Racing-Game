/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using ReliableNetcode;
using ReliableNetcode.Utils;
using System;
using System.Collections.Generic;

namespace GONet.Editor.UnitTests.ReliableChannel
{
    /// <summary>
    /// Tests to verify the reliable channel's buffer handling does NOT cause aliasing.
    /// These tests verify that the issue is in the GONet compression/send path, not in ReliableNetcode.
    /// </summary>
    [TestFixture]
    public class ReliableBufferAliasingTests
    {
        /// <summary>
        /// Verifies that SequenceBuffer.Insert returns independent BufferedPacket instances
        /// for different sequence numbers (no aliasing at the reliable layer).
        /// </summary>
        [Test]
        public void SequenceBuffer_Insert_ReturnsIndependentPackets()
        {
            const int BUFFER_SIZE = 256;
            var buffer = new SequenceBuffer<TestPacket>(BUFFER_SIZE);

            // Insert multiple packets with different sequences
            var packets = new TestPacket[10];
            for (ushort seq = 0; seq < 10; seq++)
            {
                packets[seq] = buffer.Insert(seq);
                packets[seq].Data = seq * 100;  // Unique data
            }

            // Verify each packet still has its unique data
            for (ushort seq = 0; seq < 10; seq++)
            {
                var retrieved = buffer.Find(seq);
                Assert.IsNotNull(retrieved, $"Packet {seq} should be found");
                Assert.AreEqual(seq * 100, retrieved.Data,
                    $"Packet {seq} should have data {seq * 100}, got {retrieved.Data}. " +
                    $"This would indicate buffer aliasing in SequenceBuffer.");
            }
        }

        /// <summary>
        /// Verifies that rapid sequential inserts don't cause buffer overwrites
        /// when sequences don't wrap around.
        /// </summary>
        [Test]
        public void SequenceBuffer_RapidInserts_NoOverwrite()
        {
            const int BUFFER_SIZE = 1024;  // Same as GONet's reliable buffer
            var buffer = new SequenceBuffer<TestPacket>(BUFFER_SIZE);

            // Simulate rapid-fire message sending (like spawn batch)
            const int MESSAGE_COUNT = 100;
            var allPackets = new List<TestPacket>();

            for (ushort seq = 0; seq < MESSAGE_COUNT; seq++)
            {
                var packet = buffer.Insert(seq);
                packet.Data = 1000 + seq;
                packet.Buffer = new byte[80];
                // Write unique pattern
                packet.Buffer[0] = (byte)(seq & 0xFF);
                packet.Buffer[1] = (byte)((seq >> 8) & 0xFF);
                allPackets.Add(packet);
            }

            // Verify all packets retain their unique data
            for (ushort seq = 0; seq < MESSAGE_COUNT; seq++)
            {
                var packet = buffer.Find(seq);
                Assert.IsNotNull(packet, $"Packet {seq} should exist");
                Assert.AreEqual(1000 + seq, packet.Data, $"Packet {seq} data mismatch");
                Assert.AreEqual(seq & 0xFF, packet.Buffer[0], $"Packet {seq} buffer[0] mismatch");
                Assert.AreEqual((seq >> 8) & 0xFF, packet.Buffer[1], $"Packet {seq} buffer[1] mismatch");
            }
        }

        /// <summary>
        /// Verifies that ByteBuffer copies data correctly and doesn't share underlying arrays.
        /// </summary>
        [Test]
        public void ByteBuffer_BufferCopy_CreatesIndependentCopy()
        {
            var source = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var buffer = new ByteBuffer();
            buffer.SetSize(10);

            // Copy data
            buffer.BufferCopy(source, 0, 0, 10);

            // Modify source
            source[0] = 99;

            // Buffer should be unchanged
            Assert.AreEqual(1, buffer[0],
                "ByteBuffer should have independent copy, not reference to source");
        }

        /// <summary>
        /// Simulates the exact reliable channel message flow to verify no aliasing.
        /// </summary>
        [Test]
        public void ReliableChannel_SimulatedMessageFlow_NoAliasing()
        {
            const int MESSAGE_SIZE = 84;  // 80 spawn + 4 compression header
            const int MESSAGE_COUNT = 10;

            // Simulate the sendBuffer
            var sendBuffer = new SequenceBuffer<BufferedPacketMock>(256);

            // Create unique messages (simulating compressed spawn data)
            for (ushort seq = 0; seq < MESSAGE_COUNT; seq++)
            {
                // This simulates GONetConnections.SendMessageOverChannel flow:
                // 1. Borrow buffer from pool (simulated by creating new array)
                byte[] sourceBuffer = new byte[MESSAGE_SIZE];
                sourceBuffer[0] = 0x50;  // Compression header
                sourceBuffer[1] = 0x00;
                sourceBuffer[2] = 0x50;
                sourceBuffer[3] = 0x00;
                sourceBuffer[4] = 0x0D;  // Spawn event type
                sourceBuffer[5] = 0x0D;
                sourceBuffer[6] = 0x00;
                sourceBuffer[7] = 0x00;
                // Unique GONetId at offset 8
                uint gonetId = (uint)(27647 + seq * 1024);
                sourceBuffer[8] = (byte)(gonetId & 0xFF);
                sourceBuffer[9] = (byte)((gonetId >> 8) & 0xFF);
                sourceBuffer[10] = (byte)((gonetId >> 16) & 0xFF);
                sourceBuffer[11] = (byte)((gonetId >> 24) & 0xFF);

                // 2. Insert into sendBuffer (this is what ReliableMessageChannel.SendMessage does)
                var packet = sendBuffer.Insert(seq);
                packet.buffer.SetSize(MESSAGE_SIZE);

                // 3. Copy data (this is what WriteBuffer does in the real code)
                packet.buffer.BufferCopy(sourceBuffer, 0, 0, MESSAGE_SIZE);

                // 4. Simulate returning sourceBuffer to pool (clear it to prove independence)
                Array.Clear(sourceBuffer, 0, MESSAGE_SIZE);
            }

            // NOW verify each packet has unique content (not affected by source clearing)
            for (ushort seq = 0; seq < MESSAGE_COUNT; seq++)
            {
                var packet = sendBuffer.Find(seq);
                Assert.IsNotNull(packet, $"Packet {seq} should exist");

                // Extract GONetId from packet buffer
                uint extractedId = (uint)(
                    packet.buffer[8] |
                    (packet.buffer[9] << 8) |
                    (packet.buffer[10] << 16) |
                    (packet.buffer[11] << 24)
                );

                uint expectedId = (uint)(27647 + seq * 1024);
                Assert.AreEqual(expectedId, extractedId,
                    $"Packet {seq}: GONetId should be {expectedId}, got {extractedId}. " +
                    $"If this fails, the reliable channel has buffer aliasing (unlikely based on code review).");
            }
        }

        /// <summary>
        /// Test helper class to simulate BufferedPacket
        /// </summary>
        private class TestPacket
        {
            public int Data;
            public byte[] Buffer;
        }

        /// <summary>
        /// Mock of BufferedPacket for testing
        /// </summary>
        private class BufferedPacketMock
        {
            public ByteBuffer buffer = new ByteBuffer();
            public double time;
        }
    }
}
