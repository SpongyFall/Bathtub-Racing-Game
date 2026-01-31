/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using NUnit.Framework;
using GONet.Utils;
using System;
using System.Linq;
using System.Collections.Generic;

namespace GONet.Editor.UnitTests.Utils
{
    /// <summary>
    /// CRITICAL REGRESSION TESTS: Compression + Serialization Integration
    ///
    /// **THE BUG (Fixed Dec 2025):**
    /// When messages smaller than 100 bytes were "compressed" (actually just header-wrapped),
    /// compressedBytesUsed was incorrectly set to the borrowed buffer size (~105 bytes) instead
    /// of the actual data size (data + 4-byte header = ~84 bytes for 80-byte spawn messages).
    ///
    /// **THE IMPACT:**
    /// - ~5% of client-spawned server-owned objects never reached the server
    /// - 80-byte spawn messages were sent with ~21 bytes of garbage appended
    /// - When multiple spawn events were batched in a single reliable packet, garbage bytes
    ///   corrupted message boundaries, causing server to lose/misparse some spawn events
    ///
    /// **THE FIX:**
    /// Compression.cs line 93: Changed from `sizeToBorrow` to `uncompressedBytesUsed + HEADER_LENTGH`
    ///
    /// These tests ensure this bug never regresses.
    /// </summary>
    [TestFixture]
    public class CompressionSerializationIntegrationTests
    {
        private byte[] CreatePatternedData(int size, byte pattern)
        {
            byte[] data = new byte[size];
            for (int i = 0; i < size; i++)
            {
                data[i] = (byte)((pattern + i) % 256);
            }
            return data;
        }

        private byte[] CreateRandomData(int size, int seed = 42)
        {
            var random = new Random(seed);
            var data = new byte[size];
            random.NextBytes(data);
            return data;
        }

        #region Size Accuracy Tests

        /// <summary>
        /// REGRESSION TEST: Verifies exact output size for small messages.
        /// This directly tests the bug that caused spawn message loss.
        /// </summary>
        [Test]
        public void SmallMessage_CompressionOutputSize_IsExactlyDataPlusHeader()
        {
            const int HEADER_SIZE = 4;

            // Test all sizes that triggered the bug (< 100 bytes, not compressed)
            int[] criticalSizes = { 10, 20, 40, 60, 80, 90, 100 };

            foreach (int size in criticalSizes)
            {
                byte[] original = CreateRandomData(size);

                LZ4CompressionSupport.Instance.Compress(
                    original, (ushort)size,
                    out byte[] compressed, out ushort compressedBytesUsed
                );

                // THE CRITICAL CHECK: Output size must be exactly data + header
                int expectedSize = size + HEADER_SIZE;
                Assert.AreEqual(expectedSize, compressedBytesUsed,
                    $"REGRESSION! Size {size}: compressedBytesUsed should be {expectedSize}, got {compressedBytesUsed}. " +
                    $"If this fails, the spawn message loss bug has returned!");

                // Verify borrowed buffer is larger (proves we're returning actual size, not buffer size)
                Assert.Greater(compressed.Length, compressedBytesUsed,
                    $"Size {size}: Buffer should be larger than used size");

                SerializationUtils.ReturnByteArray(compressed);
            }
        }

        /// <summary>
        /// REGRESSION TEST: The exact scenario that caused 5% spawn loss.
        /// Physics Cube Projectile spawn messages are exactly 80 bytes.
        /// </summary>
        [Test]
        public void SpawnMessage_80Bytes_OutputSizeExactly84()
        {
            const int SPAWN_MESSAGE_SIZE = 80;
            const int HEADER_SIZE = 4;
            const int EXPECTED_OUTPUT = SPAWN_MESSAGE_SIZE + HEADER_SIZE; // 84 bytes

            byte[] spawnData = CreateRandomData(SPAWN_MESSAGE_SIZE);

            LZ4CompressionSupport.Instance.Compress(
                spawnData, SPAWN_MESSAGE_SIZE,
                out byte[] compressed, out ushort compressedBytesUsed
            );

            Assert.AreEqual(EXPECTED_OUTPUT, compressedBytesUsed,
                $"CRITICAL REGRESSION! 80-byte spawn should produce 84 bytes, got {compressedBytesUsed}. " +
                $"The bug that caused ~5% spawn loss has returned!");

            SerializationUtils.ReturnByteArray(compressed);
        }

        #endregion

        #region Data Integrity Tests

        /// <summary>
        /// REGRESSION TEST: Verifies no garbage bytes after the actual data.
        /// The bug caused pooled buffer contents to leak into the output.
        /// </summary>
        [Test]
        public void SmallMessage_NoGarbageBytes_AfterData()
        {
            const int SIZE = 80;
            const int HEADER_SIZE = 4;

            // Create data with a distinct pattern
            byte[] original = new byte[SIZE];
            for (int i = 0; i < SIZE; i++)
            {
                original[i] = (byte)(0xAA ^ i); // Predictable pattern
            }

            LZ4CompressionSupport.Instance.Compress(
                original, SIZE,
                out byte[] compressed, out ushort compressedBytesUsed
            );

            // Verify the data portion exactly matches original
            for (int i = 0; i < SIZE; i++)
            {
                Assert.AreEqual(original[i], compressed[HEADER_SIZE + i],
                    $"Byte {i}: Expected 0x{original[i]:X2}, got 0x{compressed[HEADER_SIZE + i]:X2}. " +
                    $"Data corruption in compressed output!");
            }

            // The fix ensures compressedBytesUsed stops at the actual data boundary
            Assert.AreEqual(SIZE + HEADER_SIZE, compressedBytesUsed,
                "compressedBytesUsed should not include garbage bytes beyond actual data");

            SerializationUtils.ReturnByteArray(compressed);
        }

        /// <summary>
        /// REGRESSION TEST: Sequential compressions must not leak data between messages.
        /// This simulates multiple spawn events in quick succession.
        /// </summary>
        [Test]
        public void SequentialCompressions_NoDataLeakageBetweenMessages()
        {
            const int MESSAGE_SIZE = 80;
            const int HEADER_SIZE = 4;
            const int MESSAGE_COUNT = 20;

            // Create messages with distinct patterns that would be obvious if mixed
            byte[][] messages = new byte[MESSAGE_COUNT][];
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                messages[i] = new byte[MESSAGE_SIZE];
                // Each message has unique pattern: message i fills with value (i * 13)
                byte fillValue = (byte)(i * 13);
                for (int j = 0; j < MESSAGE_SIZE; j++)
                {
                    messages[i][j] = (byte)(fillValue + j);
                }
            }

            // Compress each message and immediately verify content
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                LZ4CompressionSupport.Instance.Compress(
                    messages[i], MESSAGE_SIZE,
                    out byte[] compressed, out ushort compressedBytesUsed
                );

                // Verify exact size
                Assert.AreEqual(MESSAGE_SIZE + HEADER_SIZE, compressedBytesUsed,
                    $"Message {i}: Size should be {MESSAGE_SIZE + HEADER_SIZE}, got {compressedBytesUsed}");

                // Verify content matches this message, not previous ones
                for (int j = 0; j < MESSAGE_SIZE; j++)
                {
                    Assert.AreEqual(messages[i][j], compressed[HEADER_SIZE + j],
                        $"Message {i}, byte {j}: Expected 0x{messages[i][j]:X2}, got 0x{compressed[HEADER_SIZE + j]:X2}. " +
                        $"Data leaked from another message!");
                }

                // Return to pool (buffer will be reused by next iteration)
                SerializationUtils.ReturnByteArray(compressed);
            }
        }

        #endregion

        #region Round-Trip Tests

        /// <summary>
        /// Verifies complete round-trip integrity for spawn-sized messages.
        /// </summary>
        [Test]
        public void SpawnSizedMessage_RoundTrip_PreservesExactData()
        {
            const int SPAWN_SIZE = 80;

            byte[] original = CreateRandomData(SPAWN_SIZE);

            // Compress
            LZ4CompressionSupport.Instance.Compress(
                original, SPAWN_SIZE,
                out byte[] compressed, out ushort compressedBytesUsed
            );

            // Decompress
            LZ4CompressionSupport.Instance.Uncompress(
                compressed, compressedBytesUsed,
                out byte[] decompressed, out ushort decompressedSize
            );

            // Verify exact match
            Assert.AreEqual(SPAWN_SIZE, decompressedSize, "Decompressed size should match original");
            CollectionAssert.AreEqual(original, decompressed.Take(decompressedSize).ToArray(),
                "Decompressed data should exactly match original");

            SerializationUtils.ReturnByteArray(compressed);
            SerializationUtils.ReturnByteArray(decompressed);
        }

        /// <summary>
        /// Tests multiple small messages with varying sizes in rapid succession.
        /// Simulates burst of spawn events during gameplay.
        /// </summary>
        [Test]
        public void BurstOfSmallMessages_AllRoundTripCorrectly()
        {
            const int BURST_SIZE = 50;
            var random = new Random(12345);

            // Simulate burst of varying small messages (like different spawn types)
            int[] sizes = new int[BURST_SIZE];
            byte[][] originals = new byte[BURST_SIZE][];

            for (int i = 0; i < BURST_SIZE; i++)
            {
                sizes[i] = random.Next(40, 100); // 40-99 bytes (all below compression threshold)
                originals[i] = CreateRandomData(sizes[i], i);
            }

            // Compress all
            byte[][] compressed = new byte[BURST_SIZE][];
            ushort[] compressedSizes = new ushort[BURST_SIZE];

            for (int i = 0; i < BURST_SIZE; i++)
            {
                LZ4CompressionSupport.Instance.Compress(
                    originals[i], (ushort)sizes[i],
                    out compressed[i], out compressedSizes[i]
                );

                // Verify exact size (CRITICAL - the bug would fail here)
                int expectedSize = sizes[i] + 4; // data + header
                Assert.AreEqual(expectedSize, compressedSizes[i],
                    $"Message {i} (size {sizes[i]}): Expected compressed size {expectedSize}, got {compressedSizes[i]}");
            }

            // Decompress and verify all
            for (int i = 0; i < BURST_SIZE; i++)
            {
                LZ4CompressionSupport.Instance.Uncompress(
                    compressed[i], compressedSizes[i],
                    out byte[] decompressed, out ushort decompressedSize
                );

                Assert.AreEqual(sizes[i], decompressedSize,
                    $"Message {i}: Decompressed size should be {sizes[i]}, got {decompressedSize}");
                CollectionAssert.AreEqual(originals[i], decompressed.Take(decompressedSize).ToArray(),
                    $"Message {i}: Data corrupted after round-trip");

                SerializationUtils.ReturnByteArray(decompressed);
            }

            // Cleanup
            foreach (var comp in compressed)
            {
                SerializationUtils.ReturnByteArray(comp);
            }
        }

        #endregion

        #region Boundary Condition Tests

        /// <summary>
        /// Tests at the exact compression threshold boundary.
        /// Messages of 100 bytes and below should NOT be compressed (only header-wrapped).
        /// Messages of 101 bytes and above SHOULD be compressed.
        /// </summary>
        [Test]
        public void CompressionThreshold_BoundaryBehavior()
        {
            const int HEADER_SIZE = 4;
            const int THRESHOLD = 100; // ONLY_COMPRESS_IF_LARGER_THAN_BYTE_COUNT

            // Test at threshold: 100 bytes should NOT be compressed
            byte[] atThreshold = CreatePatternedData(THRESHOLD, 0xBB);
            LZ4CompressionSupport.Instance.Compress(
                atThreshold, THRESHOLD,
                out byte[] compAtThreshold, out ushort compSizeAtThreshold
            );

            // 100 bytes input should produce exactly 104 bytes output (no compression, just header)
            Assert.AreEqual(THRESHOLD + HEADER_SIZE, compSizeAtThreshold,
                $"At threshold ({THRESHOLD} bytes): Should produce exactly {THRESHOLD + HEADER_SIZE} bytes, got {compSizeAtThreshold}");

            // Read header to verify not compressed
            uint headerAtThreshold = System.BitConverter.ToUInt32(compAtThreshold, 0);
            bool isCompressedAtThreshold = (headerAtThreshold & 0x80000000) != 0;
            Assert.IsFalse(isCompressedAtThreshold,
                "100-byte message should NOT be compressed (header flag should be 0)");

            SerializationUtils.ReturnByteArray(compAtThreshold);

            // Test above threshold: 101 bytes SHOULD be compressed
            byte[] aboveThreshold = CreatePatternedData(101, 0xCC);
            LZ4CompressionSupport.Instance.Compress(
                aboveThreshold, 101,
                out byte[] compAboveThreshold, out ushort compSizeAboveThreshold
            );

            // Read header to verify IS compressed
            uint headerAboveThreshold = System.BitConverter.ToUInt32(compAboveThreshold, 0);
            bool isCompressedAboveThreshold = (headerAboveThreshold & 0x80000000) != 0;
            Assert.IsTrue(isCompressedAboveThreshold,
                "101-byte message SHOULD be compressed (header flag should be 1)");

            SerializationUtils.ReturnByteArray(compAboveThreshold);
        }

        /// <summary>
        /// Tests that the compression header correctly stores both sizes.
        /// </summary>
        [Test]
        public void CompressionHeader_StoresCorrectSizes()
        {
            const int TEST_SIZE = 80;
            const int HEADER_SIZE = 4;

            byte[] original = CreateRandomData(TEST_SIZE);

            LZ4CompressionSupport.Instance.Compress(
                original, TEST_SIZE,
                out byte[] compressed, out ushort compressedBytesUsed
            );

            // Parse header
            uint header = System.BitConverter.ToUInt32(compressed, 0);
            uint headerMask = 0x7FFFFFFF; // Remove compression bit
            uint headerBodySizesOnly = header & headerMask;
            ushort compressedBodySizeFromHeader = (ushort)(headerBodySizesOnly >> 16);
            ushort uncompressedSizeFromHeader = (ushort)((headerBodySizesOnly << 16) >> 16);

            // For uncompressed data, body size should equal uncompressed size
            Assert.AreEqual(TEST_SIZE, compressedBodySizeFromHeader,
                $"Header compressed body size should be {TEST_SIZE} (same as original for non-compressed)");
            Assert.AreEqual(TEST_SIZE, uncompressedSizeFromHeader,
                $"Header uncompressed size should be {TEST_SIZE}");

            // Total output should be body + header
            Assert.AreEqual(compressedBodySizeFromHeader + HEADER_SIZE, compressedBytesUsed,
                "compressedBytesUsed should equal header's body size + 4-byte header");

            SerializationUtils.ReturnByteArray(compressed);
        }

        #endregion

        #region Pool Interaction Tests

        /// <summary>
        /// Verifies that borrowed buffers are larger than needed but output size is accurate.
        /// This is the core of the bug - borrowing big but returning accurate size.
        /// </summary>
        [Test]
        public void BorrowedBuffer_LargerThanOutput_ButOutputSizeAccurate()
        {
            const int MESSAGE_SIZE = 80;
            const int HEADER_SIZE = 4;

            byte[] original = CreateRandomData(MESSAGE_SIZE);

            LZ4CompressionSupport.Instance.Compress(
                original, MESSAGE_SIZE,
                out byte[] compressed, out ushort compressedBytesUsed
            );

            // The borrowed buffer should be significantly larger than needed
            // (LZ4Codec.MaximumOutputLength(80) + 4 is about 105 bytes)
            Assert.Greater(compressed.Length, compressedBytesUsed,
                "Borrowed buffer should be larger than actual used size");

            // But the reported size should be exact
            Assert.AreEqual(MESSAGE_SIZE + HEADER_SIZE, compressedBytesUsed,
                $"compressedBytesUsed should be exactly {MESSAGE_SIZE + HEADER_SIZE}, not the buffer size {compressed.Length}");

            // Document the difference that caused the bug
            int wastedBytes = compressed.Length - compressedBytesUsed;
            UnityEngine.Debug.Log(
                $"[COMPRESSION-TEST] Buffer borrowed: {compressed.Length}, Actual used: {compressedBytesUsed}, " +
                $"Difference: {wastedBytes} bytes (these would have been garbage bytes before the fix)");

            SerializationUtils.ReturnByteArray(compressed);
        }

        /// <summary>
        /// Stress test: Many rapid compressions with pool reuse.
        /// Verifies no contamination from pool buffer reuse.
        /// </summary>
        [Test]
        public void StressTest_RapidCompressions_NoPoolContamination()
        {
            const int ITERATION_COUNT = 100;
            const int HEADER_SIZE = 4;
            var random = new Random(54321);

            for (int i = 0; i < ITERATION_COUNT; i++)
            {
                int size = random.Next(20, 100); // Below compression threshold
                byte[] original = new byte[size];

                // Fill with iteration-specific pattern
                for (int j = 0; j < size; j++)
                {
                    original[j] = (byte)((i * 17 + j) % 256);
                }

                LZ4CompressionSupport.Instance.Compress(
                    original, (ushort)size,
                    out byte[] compressed, out ushort compressedBytesUsed
                );

                // Exact size check
                Assert.AreEqual(size + HEADER_SIZE, compressedBytesUsed,
                    $"Iteration {i}: Size mismatch");

                // Content verification
                for (int j = 0; j < size; j++)
                {
                    Assert.AreEqual(original[j], compressed[HEADER_SIZE + j],
                        $"Iteration {i}, byte {j}: Content mismatch - possible pool contamination");
                }

                // Return to pool for reuse in next iteration
                SerializationUtils.ReturnByteArray(compressed);
            }
        }

        #endregion
    }
}
