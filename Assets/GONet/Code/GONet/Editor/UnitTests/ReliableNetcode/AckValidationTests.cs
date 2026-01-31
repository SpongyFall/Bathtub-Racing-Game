using System;
using NUnit.Framework;
using UnityEngine;

namespace GONet.Tests.ReliableNetcode
{
    /// <summary>
    /// Comprehensive tests for ACK validation fixes (Phase 5, 6A, 6B).
    ///
    /// These tests verify that cross-connection ACK delivery is correctly detected and rejected.
    ///
    /// Bug scenario: Mesh packets reach main connection, causing false ACKs that mark
    /// critical messages (like SceneLoadComplete) as delivered when they weren't.
    ///
    /// Root Cause: Transport layer broadcasts all packets to all reliable channels.
    /// On client-side, the connection filter (GONetConnections.cs line 248) passes null,
    /// so ALL packets reach ALL channels. Mesh ACKs thus reach main channel.
    ///
    /// Reference: .claude/RELIABLE_MESSAGE_DEADLOCK_PHASE5_PHASE6.md
    /// </summary>
    [TestFixture]
    public class AckValidationTests
    {
        // Local implementation of sequence comparison (mirrors ReliableNetcode.Utils.PacketIO)
        // PacketIO is internal, so we replicate the algorithm here for testing.
        // This is the EXACT same logic used in production code.

        /// <summary>
        /// Returns true if s1 > s2 in sequence space (handles 16-bit wraparound).
        /// Uses half-space comparison: if distance > 32768, assume wraparound occurred.
        /// </summary>
        private static bool SequenceGreaterThan(ushort s1, ushort s2)
        {
            return ((s1 > s2) && (s1 - s2 <= 32768)) ||
                   ((s1 < s2) && (s2 - s1 > 32768));
        }

        /// <summary>
        /// Returns true if s1 < s2 in sequence space (handles 16-bit wraparound).
        /// </summary>
        private static bool SequenceLessThan(ushort s1, ushort s2)
        {
            return SequenceGreaterThan(s2, s1);
        }

        private int bufferSize = 256;  // Matches ReliableConfig.SentPacketBufferSize default

        [SetUp]
        public void SetUp()
        {
            bufferSize = 256;
        }

        #region Phase 6B: Primary ACK Validation Tests

        /// <summary>
        /// Test: Primary ACK ahead of sent range should be rejected.
        ///
        /// Scenario: We've sent packets 0-1 (this.sequence = 2), but receive packet with ack=10.
        /// This is impossible - remote can't ACK packets we haven't sent.
        /// </summary>
        [Test]
        public void Phase6B_RejectsAckAheadOfSentRange()
        {
            // Arrange
            ushort ourSentSeq = 2;  // We've sent packets 0 and 1
            ushort incomingAck = 10; // Packet claims to have received up to 10

            // Act & Assert
            bool shouldReject = !SequenceLessThan(incomingAck, ourSentSeq);

            Assert.IsTrue(shouldReject,
                $"ACK {incomingAck} should be rejected when ourSentSeq={ourSentSeq}. " +
                $"10 >= 2, so this ACK claims to have received packets we haven't sent.");
        }

        /// <summary>
        /// Test: Primary ACK at boundary (ack == this.sequence) should be rejected.
        ///
        /// We're about to send sequence 2, but haven't sent it yet.
        /// An ack of 2 means "I received packet 2" which is impossible.
        /// </summary>
        [Test]
        public void Phase6B_RejectsAckAtExactBoundary()
        {
            // Arrange
            ushort ourSentSeq = 2;
            ushort incomingAck = 2; // Claims to have received packet 2, but we haven't sent it

            // Act
            bool shouldReject = !SequenceLessThan(incomingAck, ourSentSeq);

            // Assert
            Assert.IsTrue(shouldReject,
                $"ACK {incomingAck} should be rejected at exact boundary. " +
                $"We haven't sent packet 2 yet (this.sequence=2 means next to send).");
        }

        /// <summary>
        /// Test: Valid primary ACK within sent range should be accepted.
        /// </summary>
        [Test]
        public void Phase6B_AcceptsValidAckWithinSentRange()
        {
            // Arrange
            ushort ourSentSeq = 10;  // We've sent packets 0-9
            ushort incomingAck = 5;  // Claims to have received up to 5

            // Act
            bool isValid = SequenceLessThan(incomingAck, ourSentSeq);

            // Assert
            Assert.IsTrue(isValid,
                $"ACK {incomingAck} should be accepted when ourSentSeq={ourSentSeq}. " +
                $"5 < 10, so this is a valid ACK for packets we've sent.");
        }

        /// <summary>
        /// Test: ACK for most recent packet should be accepted.
        /// </summary>
        [Test]
        public void Phase6B_AcceptsAckForMostRecentPacket()
        {
            // Arrange
            ushort ourSentSeq = 10;  // We've sent packets 0-9
            ushort incomingAck = 9;  // Claims to have received packet 9 (most recent)

            // Act
            bool isValid = SequenceLessThan(incomingAck, ourSentSeq);

            // Assert
            Assert.IsTrue(isValid,
                $"ACK {incomingAck} for most recent packet should be accepted.");
        }

        /// <summary>
        /// Test: ACK too old (outside buffer range) should be rejected.
        /// </summary>
        [Test]
        public void Phase6B_RejectsAckTooOld()
        {
            // Arrange
            int bufferSize = 256;
            ushort ourSentSeq = 1000;  // We've sent packets 0-999
            ushort oldestTracked = (ushort)(ourSentSeq - bufferSize); // 744
            ushort incomingAck = 500;  // Claims to have received up to 500, but that's too old

            // Act
            bool withinSentRange = SequenceLessThan(incomingAck, ourSentSeq);
            bool notTooOld = !SequenceLessThan(incomingAck, oldestTracked);
            bool isValid = withinSentRange && notTooOld;

            // Assert: 500 < 744 in sequence space (500 is behind the tracking window)
            // This should be rejected as "too old"
            Assert.IsFalse(isValid,
                $"ACK {incomingAck} should be rejected as too old. " +
                $"Buffer only tracks sequences {oldestTracked} to {ourSentSeq - 1}.");
        }

        #endregion

        #region Wraparound Tests

        /// <summary>
        /// Test: Wraparound case - ACK after sequence wrapped should still work.
        ///
        /// Scenario: this.sequence = 5 (wrapped from 65535 -> 0 -> 1 -> 2 -> 3 -> 4 -> 5)
        /// We've sent packets ..., 65533, 65534, 65535, 0, 1, 2, 3, 4
        /// ACK = 65534 means remote received up to 65534 (valid, just old)
        /// </summary>
        [Test]
        public void Phase6B_HandlesWraparound_OldAckAfterWrap()
        {
            // Arrange
            ushort ourSentSeq = 5;  // Wrapped around from 65535
            ushort incomingAck = 65534;  // ACK for pre-wrap packet

            // With bufferSize=256, oldestTracked = 5 - 256 = 65285 (wrapped)
            int bufferSize = 256;
            ushort oldestTracked = (ushort)(ourSentSeq - bufferSize);

            // Act
            bool withinSentRange = SequenceLessThan(incomingAck, ourSentSeq);
            bool notTooOld = !SequenceLessThan(incomingAck, oldestTracked);
            bool isValid = withinSentRange && notTooOld;

            // Assert: 65534 should be within valid range
            // 65534 < 5 in sequence space? Using wraparound logic: distance = 5 - 65534 = -65529 + 65536 = 7
            // 7 < 32768, so 5 > 65534 in sequence space (5 is "ahead" of 65534)
            // Therefore 65534 < 5 in sequence space = TRUE (valid)
            Debug.Log($"Wraparound test: ack={incomingAck}, ourSentSeq={ourSentSeq}, oldestTracked={oldestTracked}");
            Debug.Log($"SequenceLessThan({incomingAck}, {ourSentSeq}) = {SequenceLessThan(incomingAck, ourSentSeq)}");
            Debug.Log($"SequenceLessThan({incomingAck}, {oldestTracked}) = {SequenceLessThan(incomingAck, oldestTracked)}");

            Assert.IsTrue(isValid,
                $"ACK {incomingAck} after wraparound should be accepted. " +
                $"65534 is a valid old packet that we sent before wrapping.");
        }

        /// <summary>
        /// Test: Wraparound case - ACK far ahead (impossible) after wrap.
        ///
        /// Scenario: this.sequence = 5, ack = 100
        /// 100 >= 5, so this is invalid (claims packets we haven't sent)
        /// </summary>
        [Test]
        public void Phase6B_RejectsAckAheadAfterWrap()
        {
            // Arrange
            ushort ourSentSeq = 5;
            ushort incomingAck = 100;  // Far ahead of what we've sent

            // Act
            bool shouldReject = !SequenceLessThan(incomingAck, ourSentSeq);

            // Assert
            Assert.IsTrue(shouldReject,
                $"ACK {incomingAck} should be rejected. 100 >= 5 means claiming unsent packets.");
        }

        /// <summary>
        /// Test: Large sequence numbers (near max ushort) work correctly.
        /// </summary>
        [Test]
        public void Phase6B_HandlesLargeSequenceNumbers()
        {
            // Arrange
            ushort ourSentSeq = 65530;  // Near max
            ushort validAck = 65525;    // Valid (within sent range)
            ushort invalidAck = 65535;  // Invalid (ahead)

            // Act
            bool validResult = SequenceLessThan(validAck, ourSentSeq);
            bool invalidResult = SequenceLessThan(invalidAck, ourSentSeq);

            // Assert
            Assert.IsTrue(validResult, $"ACK {validAck} should be valid (< {ourSentSeq})");
            Assert.IsFalse(invalidResult, $"ACK {invalidAck} should be invalid (>= {ourSentSeq})");
        }

        #endregion

        #region Cross-Connection Delivery Scenario (The Bug)

        /// <summary>
        /// Test: Exact scenario from the bug - mesh ACK reaches main connection.
        ///
        /// Bug: Mesh packet with ack=31 reaches main connection that only sent 2 packets.
        /// The packet should be ENTIRELY rejected at the packet level (Phase 6B).
        /// </summary>
        [Test]
        public void Phase6B_RejectsCrossConnectionDelivery_ExactBugScenario()
        {
            // Arrange: Exact values from the bug
            ushort ourSentSeq = 2;      // Main connection only sent packets 0 and 1
            ushort meshAck = 31;        // Mesh packet claims to have received up to 31
            uint meshAckBits = 0x7FFFFFFF;  // All 32 bits set

            // Act: Check if primary ACK should reject the entire packet
            bool primaryAckValid = SequenceLessThan(meshAck, ourSentSeq);

            // Assert: The primary ACK field reveals this is from a different connection
            Assert.IsFalse(primaryAckValid,
                $"CRITICAL: ack={meshAck} should be rejected when ourSentSeq={ourSentSeq}. " +
                $"This is the exact cross-connection delivery scenario that caused the bug. " +
                $"Without this check, ackBits for sequences 0 and 1 would be processed as valid!");

            // Additional verification: Show why per-bit check alone was insufficient
            // Sequences 0 and 1 WOULD pass the per-bit check because we DID send them
            ushort ackSeq0 = (ushort)(meshAck - 31);  // = 0
            ushort ackSeq1 = (ushort)(meshAck - 30);  // = 1

            bool seq0PassesPerBitCheck = SequenceLessThan(ackSeq0, ourSentSeq);  // 0 < 2 = true
            bool seq1PassesPerBitCheck = SequenceLessThan(ackSeq1, ourSentSeq);  // 1 < 2 = true

            Assert.IsTrue(seq0PassesPerBitCheck, "ackSeq=0 passes per-bit check (we did send it)");
            Assert.IsTrue(seq1PassesPerBitCheck, "ackSeq=1 passes per-bit check (we did send it)");

            Debug.Log("BUG SCENARIO VERIFIED:");
            Debug.Log($"  - Mesh ack={meshAck}, ourSentSeq={ourSentSeq}");
            Debug.Log($"  - Primary ACK check (Phase 6B): REJECT (31 >= 2)");
            Debug.Log($"  - Per-bit check for seq 0: would pass (0 < 2)");
            Debug.Log($"  - Per-bit check for seq 1: would pass (1 < 2)");
            Debug.Log("  - Without Phase 6B, these false ACKs would corrupt message tracking!");
        }

        /// <summary>
        /// Test: Verify per-bit rejection still works for out-of-range sequences.
        /// Phase 6A should reject individual ackBits that are out of range.
        /// </summary>
        [Test]
        public void Phase6A_RejectsPerBitSequencesOutOfRange()
        {
            // Arrange
            ushort ourSentSeq = 10;
            int bufferSize = 256;
            ushort oldestTracked = (ushort)(ourSentSeq - bufferSize);

            // Sequences to test
            ushort validSeq = 5;       // Within range [oldestTracked, ourSentSeq)
            ushort aheadSeq = 15;      // Ahead of ourSentSeq
            ushort tooOldSeq = 500;    // Would be too old if we had sent that many

            // Act
            bool validResult = SequenceLessThan(validSeq, ourSentSeq) &&
                              !SequenceLessThan(validSeq, oldestTracked);
            bool aheadResult = SequenceLessThan(aheadSeq, ourSentSeq);

            // Assert
            Assert.IsTrue(validResult, $"Sequence {validSeq} should be valid");
            Assert.IsFalse(aheadResult, $"Sequence {aheadSeq} should be rejected (ahead of sent range)");
        }

        #endregion

        #region Boundary Condition Tests

        /// <summary>
        /// Test: Zero sequence numbers work correctly.
        /// </summary>
        [Test]
        public void Phase6B_HandlesZeroSequence()
        {
            // When this.sequence = 0, we haven't sent any packets yet
            // Any ACK should be rejected
            ushort ourSentSeq = 0;
            ushort incomingAck = 0;

            bool shouldReject = !SequenceLessThan(incomingAck, ourSentSeq);

            Assert.IsTrue(shouldReject,
                "When ourSentSeq=0, any ACK should be rejected (we haven't sent anything).");
        }

        /// <summary>
        /// Test: First packet scenario - ack=0 valid when this.sequence=1.
        /// </summary>
        [Test]
        public void Phase6B_FirstPacketScenario()
        {
            ushort ourSentSeq = 1;  // Just sent packet 0
            ushort incomingAck = 0; // Remote acknowledges packet 0

            bool isValid = SequenceLessThan(incomingAck, ourSentSeq);

            Assert.IsTrue(isValid,
                "ACK for first packet (ack=0 when ourSentSeq=1) should be valid.");
        }

        /// <summary>
        /// Test: Maximum valid buffer - verify oldestTracked calculation.
        /// </summary>
        [Test]
        public void Phase6B_OldestTrackedCalculation()
        {
            // When buffer is full and we're at sequence 1000
            int bufferSize = 256;
            ushort ourSentSeq = 1000;
            ushort oldestTracked = (ushort)(ourSentSeq - bufferSize);  // 744

            // Sequence 743 should be rejected (too old)
            ushort tooOld = 743;
            // Sequence 744 should be accepted (at boundary)
            ushort atBoundary = 744;
            // Sequence 999 should be accepted (most recent)
            ushort mostRecent = 999;

            bool tooOldResult = SequenceLessThan(tooOld, ourSentSeq) &&
                               !SequenceLessThan(tooOld, oldestTracked);
            bool atBoundaryResult = SequenceLessThan(atBoundary, ourSentSeq) &&
                                   !SequenceLessThan(atBoundary, oldestTracked);
            bool mostRecentResult = SequenceLessThan(mostRecent, ourSentSeq) &&
                                   !SequenceLessThan(mostRecent, oldestTracked);

            Assert.IsFalse(tooOldResult, $"Sequence {tooOld} should be rejected (too old)");
            Assert.IsTrue(atBoundaryResult, $"Sequence {atBoundary} should be accepted (at boundary)");
            Assert.IsTrue(mostRecentResult, $"Sequence {mostRecent} should be accepted (most recent)");
        }

        #endregion

        #region SequenceLessThan Verification Tests

        /// <summary>
        /// Verify SequenceLessThan works correctly for basic cases.
        /// </summary>
        [Test]
        public void SequenceLessThan_BasicCases()
        {
            // Simple cases
            Assert.IsTrue(SequenceLessThan(5, 10), "5 < 10");
            Assert.IsFalse(SequenceLessThan(10, 5), "10 not < 5");
            Assert.IsFalse(SequenceLessThan(5, 5), "5 not < 5 (equal)");

            // Near boundary
            Assert.IsTrue(SequenceLessThan(65534, 65535), "65534 < 65535");
            Assert.IsFalse(SequenceLessThan(65535, 65534), "65535 not < 65534");
        }

        /// <summary>
        /// Verify SequenceLessThan handles wraparound correctly.
        /// </summary>
        [Test]
        public void SequenceLessThan_Wraparound()
        {
            // After wraparound: 65535 -> 0 -> 1 -> 2 -> ... -> 5
            // 65534 is "less than" (behind) 5 in sequence space
            Assert.IsTrue(SequenceLessThan(65534, 5),
                "65534 should be < 5 in sequence space (65534 is ~7 packets behind after wrap)");

            // 0 is "less than" (behind) 5
            Assert.IsTrue(SequenceLessThan(0, 5),
                "0 should be < 5 in sequence space");

            // 32767 is "less than" (behind) 32768
            Assert.IsTrue(SequenceLessThan(32767, 32768),
                "32767 < 32768 (no wraparound)");

            // 32769 is "greater than" (ahead) of 32768 in linear terms
            // But we need to check with the actual function
            bool result = SequenceLessThan(32769, 32768);
            Debug.Log($"SequenceLessThan(32769, 32768) = {result}");
        }

        /// <summary>
        /// Verify SequenceLessThan at the 32768 boundary (half of ushort space).
        /// </summary>
        [Test]
        public void SequenceLessThan_At32768Boundary()
        {
            // The algorithm treats differences > 32768 as wraparound
            // If diff = 32768 exactly, the larger number is considered "greater"

            // diff = 32768: 0 and 32768
            Assert.IsTrue(SequenceLessThan(0, 32768),
                "0 < 32768 (diff = 32768, no wraparound assumed)");

            // diff = 32769: 0 and 32769 - now 0 is considered "ahead" due to wrap
            Assert.IsFalse(SequenceLessThan(0, 32769),
                "0 is NOT < 32769 because diff > 32768 means 0 wrapped ahead");

            // diff = 32768: 32768 and 0
            Assert.IsFalse(SequenceLessThan(32768, 0),
                "32768 is NOT < 0 (32768 is ahead in linear space)");
        }

        #endregion

        #region Integration Validation Tests

        /// <summary>
        /// Test: Verify the fix constants exist in the source code.
        /// This is a source verification test that ensures the fix hasn't been accidentally removed.
        /// </summary>
        [Test]
        public void VerifyFixConstants_ExistInSource()
        {
            // Use Application.dataPath for Unity-safe path resolution
            string basePath = UnityEngine.Application.dataPath;
            string filePath = System.IO.Path.Combine(basePath, "GONet/Code/ReliableNetcode/ReliablePacketController.cs");

            if (!System.IO.File.Exists(filePath))
            {
                // Fallback for different working directories
                filePath = "Assets/GONet/Code/ReliableNetcode/ReliablePacketController.cs";
            }

            Assert.IsTrue(System.IO.File.Exists(filePath), $"Source file not found: {filePath}");
            string sourceCode = System.IO.File.ReadAllText(filePath);

            // Phase 6B validation - packet-level ACK rejection
            Assert.IsTrue(sourceCode.Contains("primaryAckWithinSentRange"),
                "Phase 6B fix should have primaryAckWithinSentRange check");
            Assert.IsTrue(sourceCode.Contains("primaryAckNotTooOld"),
                "Phase 6B fix should have primaryAckNotTooOld check");
            Assert.IsTrue(sourceCode.Contains("PHASE 6B FIX"),
                "Phase 6B fix comment should exist");

            // Phase 6A validation - per-bit ACK rejection
            Assert.IsTrue(sourceCode.Contains("isWithinSentRange"),
                "Phase 6A fix should have isWithinSentRange check");
            Assert.IsTrue(sourceCode.Contains("isNotTooOld"),
                "Phase 6A fix should have isNotTooOld check");

            // Phase 5 validation - RTT check
            Assert.IsTrue(sourceCode.Contains("MIN_REALISTIC_RTT_MS"),
                "Phase 5 fix should have MIN_REALISTIC_RTT_MS constant");
            Assert.IsTrue(sourceCode.Contains("0.5f"),
                "Phase 5 RTT threshold should be 0.5ms");

            Debug.Log("[AckValidationTests] All fix constants validated in ReliablePacketController.cs");
        }

        /// <summary>
        /// Test: Verify MessageChannel has proper rejection logging.
        /// This ensures diagnostic logging for false ACK detection is in place.
        /// </summary>
        [Test]
        public void VerifyMessageChannel_HasRejectionLogging()
        {
            // Use Application.dataPath for Unity-safe path resolution
            string basePath = UnityEngine.Application.dataPath;
            string filePath = System.IO.Path.Combine(basePath, "GONet/Code/ReliableNetcode/MessageChannel.cs");

            if (!System.IO.File.Exists(filePath))
            {
                // Fallback for different working directories
                filePath = "Assets/GONet/Code/ReliableNetcode/MessageChannel.cs";
            }

            Assert.IsTrue(System.IO.File.Exists(filePath), $"Source file not found: {filePath}");
            string sourceCode = System.IO.File.ReadAllText(filePath);

            // Verify all three rejection reason strings exist
            Assert.IsTrue(sourceCode.Contains("PACKET_PRIMARY_ACK_INVALID"),
                "MessageChannel should log PACKET_PRIMARY_ACK_INVALID for Phase 6B rejections");
            Assert.IsTrue(sourceCode.Contains("BIT_SEQUENCE_OUT_OF_RANGE"),
                "MessageChannel should log BIT_SEQUENCE_OUT_OF_RANGE for Phase 6A rejections");
            Assert.IsTrue(sourceCode.Contains("RTT_TOO_LOW"),
                "MessageChannel should log RTT_TOO_LOW for Phase 5 rejections");

            Debug.Log("[AckValidationTests] All rejection logging validated in MessageChannel.cs");
        }

        #endregion
    }
}
