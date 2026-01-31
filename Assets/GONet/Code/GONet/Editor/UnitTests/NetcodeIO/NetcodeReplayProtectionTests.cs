using NUnit.Framework;
using NetcodeIO.NET.Internal;

namespace GONet.Tests.Netcode_IO
{
    /// <summary>
    /// Unit tests for NetcodeReplayProtection class.
    /// Tests the sliding window replay protection mechanism used to prevent duplicate packet processing.
    /// </summary>
    [TestFixture]
    public class NetcodeReplayProtectionTests
    {
        private const int NETCODE_REPLAY_PROTECTION_BUFFER_SIZE = 256;

        [Test]
        public void TestReplayProtection_FirstPacket_NotDuplicate()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act
            bool isDuplicate = replay.AlreadyReceived(0);

            // Assert
            Assert.IsFalse(isDuplicate, "First packet (sequence 0) should not be marked as duplicate");
            Assert.AreEqual(0, replay.mostRecentSequence, "mostRecentSequence should be updated to 0");
        }

        [Test]
        public void TestReplayProtection_DuplicatePacket_Detected()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();
            replay.AlreadyReceived(100);

            // Act
            bool isDuplicate = replay.AlreadyReceived(100);

            // Assert
            Assert.IsTrue(isDuplicate, "Duplicate packet (sequence 100) should be detected");
        }

        [Test]
        public void TestReplayProtection_SequentialPackets_NoDuplicates()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act & Assert - sequential packets should all be accepted
            for (ulong i = 0; i < 100; i++)
            {
                bool isDuplicate = replay.AlreadyReceived(i);
                Assert.IsFalse(isDuplicate, $"Sequential packet {i} should not be duplicate");
                Assert.AreEqual(i, replay.mostRecentSequence, $"mostRecentSequence should advance to {i}");
            }
        }

        [Test]
        public void TestReplayProtection_OutOfOrderPackets_AcceptedWithinWindow()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - receive packets out of order within sliding window
            replay.AlreadyReceived(100);
            bool isDuplicate95 = replay.AlreadyReceived(95);
            bool isDuplicate105 = replay.AlreadyReceived(105);
            bool isDuplicate98 = replay.AlreadyReceived(98);

            // Assert
            Assert.IsFalse(isDuplicate95, "Out-of-order packet 95 should be accepted (within window)");
            Assert.IsFalse(isDuplicate105, "Out-of-order packet 105 should be accepted");
            Assert.IsFalse(isDuplicate98, "Out-of-order packet 98 should be accepted (within window)");
            Assert.AreEqual(105, replay.mostRecentSequence, "mostRecentSequence should track highest sequence");
        }

        [Test]
        public void TestReplayProtection_MostRecentSequenceUpdates_OnNewHighestSequence()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - receive packets with increasing highest sequence
            replay.AlreadyReceived(50);
            Assert.AreEqual(50, replay.mostRecentSequence, "mostRecentSequence should be 50");

            replay.AlreadyReceived(40); // Lower sequence, shouldn't update
            Assert.AreEqual(50, replay.mostRecentSequence, "mostRecentSequence should remain 50");

            replay.AlreadyReceived(100); // Higher sequence, should update
            Assert.AreEqual(100, replay.mostRecentSequence, "mostRecentSequence should be 100");

            replay.AlreadyReceived(75); // Lower sequence, shouldn't update
            Assert.AreEqual(100, replay.mostRecentSequence, "mostRecentSequence should remain 100");
        }

        [Test]
        public void TestReplayProtection_TooOldPacket_RejectedOutsideSlidingWindow()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - advance window far enough to make early packets "too old"
            ulong highSequence = 1000;
            replay.AlreadyReceived(highSequence);

            // Packet outside sliding window (1000 - 256 = 744, so 743 and below are too old)
            ulong tooOldSequence = highSequence - NETCODE_REPLAY_PROTECTION_BUFFER_SIZE - 1;
            bool isTooOld = replay.AlreadyReceived(tooOldSequence);

            // Assert
            Assert.IsTrue(isTooOld, $"Packet {tooOldSequence} should be rejected as too old (outside window)");
        }

        [Test]
        public void TestReplayProtection_EdgeOfSlidingWindow_Accepted()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - test packet at exact edge of sliding window
            ulong highSequence = 1000;
            replay.AlreadyReceived(highSequence);

            // Packet at edge of window (exactly at boundary)
            ulong edgeSequence = highSequence - NETCODE_REPLAY_PROTECTION_BUFFER_SIZE + 1;
            bool isEdgeAccepted = replay.AlreadyReceived(edgeSequence);

            // Assert
            Assert.IsFalse(isEdgeAccepted, $"Packet {edgeSequence} at edge of window should be accepted");
        }

        [Test]
        public void TestReplayProtection_SpecialPacket_Bit63Set_AlwaysAccepted()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - special packet with bit 63 set bypasses replay protection
            ulong specialPacket = (ulong)1 << 63;
            bool isSpecial1 = replay.AlreadyReceived(specialPacket);
            bool isSpecial2 = replay.AlreadyReceived(specialPacket);

            // Assert
            Assert.IsFalse(isSpecial1, "Special packet (bit 63 set) should always return false");
            Assert.IsFalse(isSpecial2, "Special packet should always return false even on repeat");
            Assert.AreEqual(0, replay.mostRecentSequence, "Special packets should not update mostRecentSequence");
        }

        [Test]
        public void TestReplayProtection_BufferWrapAround_HandlesCollisions()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - test buffer index collision (two sequences mapping to same buffer slot)
            ulong sequence1 = 100;
            ulong sequence2 = sequence1 + NETCODE_REPLAY_PROTECTION_BUFFER_SIZE; // 356

            replay.AlreadyReceived(sequence1);
            bool isCollision = replay.AlreadyReceived(sequence2);

            // Assert - sequence2 is higher, so it overwrites the slot
            Assert.IsFalse(isCollision, "Higher sequence should overwrite same buffer slot");

            // Now sequence1 should be detected as "old" (lower than slot value)
            bool isOldAfterOverwrite = replay.AlreadyReceived(sequence1);
            Assert.IsTrue(isOldAfterOverwrite, "Lower sequence should be rejected after slot overwritten");
        }

        [Test]
        public void TestReplayProtection_Reset_ClearsAllState()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();
            replay.AlreadyReceived(100);
            replay.AlreadyReceived(200);
            replay.AlreadyReceived(300);

            // Act
            replay.Reset();

            // Assert
            Assert.AreEqual(0, replay.mostRecentSequence, "Reset should clear mostRecentSequence");

            // Previously received packets should now be accepted again
            bool is100Duplicate = replay.AlreadyReceived(100);
            Assert.IsFalse(is100Duplicate, "After reset, previously received packet should be accepted");
        }

        [Test]
        public void TestReplayProtection_SlidingWindowAdvancement_RejectsOldPackets()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - establish initial window
            replay.AlreadyReceived(100);
            bool isValid = replay.AlreadyReceived(90); // Within window
            Assert.IsFalse(isValid, "Packet 90 should be accepted (within window)");

            // Advance window significantly
            replay.AlreadyReceived(500);

            // Now packet 90 is outside window (500 - 256 = 244, so 243 and below rejected)
            bool isOutsideWindow = replay.AlreadyReceived(90);

            // Assert
            Assert.IsTrue(isOutsideWindow, "Packet 90 should now be rejected (window advanced)");
        }

        [Test]
        public void TestReplayProtection_MassOutOfOrder_AllAcceptedOnce()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - receive packets in reverse order
            for (ulong i = 200; i > 100; i--)
            {
                bool isDuplicate = replay.AlreadyReceived(i);
                Assert.IsFalse(isDuplicate, $"First reception of packet {i} should not be duplicate");
            }

            // Assert - all packets should be marked as duplicates on second reception
            for (ulong i = 200; i > 100; i--)
            {
                bool isDuplicate = replay.AlreadyReceived(i);
                Assert.IsTrue(isDuplicate, $"Second reception of packet {i} should be duplicate");
            }
        }

        [Test]
        public void TestReplayProtection_BugFix_OutOfOrderAdvancesMostRecentSequence()
        {
            // This test validates the critical bug fix where mostRecentSequence
            // must be updated BEFORE checking buffer slots to prevent the sliding
            // window from getting stuck.

            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - receive packets out of order: 100, then 50, then 150
            replay.AlreadyReceived(100);
            Assert.AreEqual(100, replay.mostRecentSequence, "Step 1: mostRecentSequence should be 100");

            replay.AlreadyReceived(50);
            Assert.AreEqual(100, replay.mostRecentSequence, "Step 2: mostRecentSequence should remain 100 (50 < 100)");

            replay.AlreadyReceived(150);
            Assert.AreEqual(150, replay.mostRecentSequence, "Step 3: mostRecentSequence should advance to 150");

            // Assert - without the fix, packets could be incorrectly rejected
            // With the fix, mostRecentSequence correctly tracks the highest seen sequence
            bool is140Accepted = replay.AlreadyReceived(140);
            Assert.IsFalse(is140Accepted, "Packet 140 should be accepted (within window from 150)");
        }

        [Test]
        public void TestReplayProtection_SequenceZero_HandledCorrectly()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act
            bool isFirstZero = replay.AlreadyReceived(0);
            bool isSecondZero = replay.AlreadyReceived(0);

            // Assert
            Assert.IsFalse(isFirstZero, "First reception of sequence 0 should be accepted");
            Assert.IsTrue(isSecondZero, "Second reception of sequence 0 should be duplicate");
            Assert.AreEqual(0, replay.mostRecentSequence, "mostRecentSequence should be 0");
        }

        [Test]
        public void TestReplayProtection_MaxULongWithoutBit63_HandledCorrectly()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - max ulong value with bit 63 clear (not a special packet)
            ulong maxValidSequence = ulong.MaxValue & ~((ulong)1 << 63); // Clear bit 63
            bool isAccepted = replay.AlreadyReceived(maxValidSequence);

            // Assert
            Assert.IsFalse(isAccepted, "Max valid sequence should be accepted");
            Assert.AreEqual(maxValidSequence, replay.mostRecentSequence, "mostRecentSequence should be max valid");
        }

        [Test]
        public void TestReplayProtection_RapidSequenceAdvancement_WindowTracksCorrectly()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - jump forward in large increments
            ulong[] sequences = { 1000, 2000, 3000, 4000, 5000 };

            foreach (var seq in sequences)
            {
                bool isDuplicate = replay.AlreadyReceived(seq);
                Assert.IsFalse(isDuplicate, $"First reception of sequence {seq} should be accepted");
                Assert.AreEqual(seq, replay.mostRecentSequence, $"mostRecentSequence should be {seq}");
            }

            // Assert - packets between jumps should be rejected if outside window
            ulong oldPacket = 4000 - NETCODE_REPLAY_PROTECTION_BUFFER_SIZE - 1;
            bool isTooOld = replay.AlreadyReceived(oldPacket);
            Assert.IsTrue(isTooOld, $"Packet {oldPacket} should be rejected (outside window from 5000)");
        }

        [Test]
        public void TestReplayProtection_BufferSlotOverwrite_DetectsCorrectDuplicate()
        {
            // Arrange
            var replay = new NetcodeReplayProtection();

            // Act - fill buffer slot, then test overwrite behavior
            ulong seq1 = 50;
            ulong seq2 = seq1 + NETCODE_REPLAY_PROTECTION_BUFFER_SIZE; // Same buffer index

            replay.AlreadyReceived(seq1);
            int index = (int)(seq1 % NETCODE_REPLAY_PROTECTION_BUFFER_SIZE);
            Assert.AreEqual(seq1, replay.receivedPackets[index], "Buffer should store seq1");

            // Receive seq2 (higher, same slot)
            replay.AlreadyReceived(seq2);
            Assert.AreEqual(seq2, replay.receivedPackets[index], "Buffer should now store seq2 (overwrites seq1)");

            // Assert - seq1 should now be detected as "already received" due to slot comparison
            bool isSeq1Old = replay.AlreadyReceived(seq1);
            Assert.IsTrue(isSeq1Old, "seq1 should be detected as old (slot has higher value)");
        }
    }
}
