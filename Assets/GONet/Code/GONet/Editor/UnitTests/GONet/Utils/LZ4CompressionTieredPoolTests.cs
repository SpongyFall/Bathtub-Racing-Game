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

namespace GONet.Editor.UnitTests.Utils
{
    [TestFixture]
    public class LZ4CompressionTieredPoolTests
    {
        private byte[] CreateTestData(int size, byte seed = 42)
        {
            var data = new byte[size];
            var random = new Random(seed);
            random.NextBytes(data);
            return data;
        }

        [Test]
        public void Compress_TinyData_PreservesIntegrity()
        {
            // Test compression with tiny data (RPC parameter size)
            byte[] original = CreateTestData(10);

            LZ4CompressionSupport.Instance.Compress(
                original, (ushort)original.Length,
                out byte[] compressed, out ushort compressedSize
            );

            LZ4CompressionSupport.Instance.Uncompress(
                compressed, compressedSize,
                out byte[] uncompressed, out ushort uncompressedSize
            );

            Assert.AreEqual(original.Length, uncompressedSize, "Uncompressed size should match original");
            CollectionAssert.AreEqual(original, uncompressed.Take(uncompressedSize).ToArray(),
                "Data integrity should be preserved");

            SerializationUtils.ReturnByteArray(compressed);
            SerializationUtils.ReturnByteArray(uncompressed);
        }

        [Test]
        public void Compress_SmallData_PreservesIntegrity()
        {
            // Test with small message size (500 bytes)
            byte[] original = CreateTestData(500);

            LZ4CompressionSupport.Instance.Compress(
                original, (ushort)original.Length,
                out byte[] compressed, out ushort compressedSize
            );

            LZ4CompressionSupport.Instance.Uncompress(
                compressed, compressedSize,
                out byte[] uncompressed, out ushort uncompressedSize
            );

            Assert.AreEqual(original.Length, uncompressedSize);
            CollectionAssert.AreEqual(original, uncompressed.Take(uncompressedSize).ToArray());

            SerializationUtils.ReturnByteArray(compressed);
            SerializationUtils.ReturnByteArray(uncompressed);
        }

        [Test]
        public void Compress_MediumData_PreservesIntegrity()
        {
            // Test with medium network message (5KB)
            byte[] original = CreateTestData(5000);

            LZ4CompressionSupport.Instance.Compress(
                original, (ushort)original.Length,
                out byte[] compressed, out ushort compressedSize
            );

            LZ4CompressionSupport.Instance.Uncompress(
                compressed, compressedSize,
                out byte[] uncompressed, out ushort uncompressedSize
            );

            Assert.AreEqual(original.Length, uncompressedSize);
            CollectionAssert.AreEqual(original, uncompressed.Take(uncompressedSize).ToArray());

            SerializationUtils.ReturnByteArray(compressed);
            SerializationUtils.ReturnByteArray(uncompressed);
        }

        [Test]
        public void Compress_LargeData_PreservesIntegrity()
        {
            // Test with large bundle (32KB - within ushort limit of 65535)
            byte[] original = CreateTestData(32000);

            LZ4CompressionSupport.Instance.Compress(
                original, (ushort)original.Length,
                out byte[] compressed, out ushort compressedSize
            );

            LZ4CompressionSupport.Instance.Uncompress(
                compressed, compressedSize,
                out byte[] uncompressed, out ushort uncompressedSize
            );

            Assert.AreEqual(original.Length, uncompressedSize);
            CollectionAssert.AreEqual(original, uncompressed.Take(uncompressedSize).ToArray());

            SerializationUtils.ReturnByteArray(compressed);
            SerializationUtils.ReturnByteArray(uncompressed);
        }

        [Test]
        public void Compress_VariableSizedBuffers_HeaderStoresLogicalSize()
        {
            // Verify that LZ4 header stores logical sizes, not buffer sizes
            // This proves TieredArrayPool compatibility

            int[] testSizes = { 5, 50, 500, 5000 };

            foreach (int size in testSizes)
            {
                byte[] original = CreateTestData(size);

                LZ4CompressionSupport.Instance.Compress(
                    original, (ushort)size,
                    out byte[] compressed, out ushort compressedSize
                );

                // Read header (first 4 bytes)
                uint header = System.BitConverter.ToUInt32(compressed, 0);
                uint headerMask = 0x7FFFFFFF; // Remove compression bit
                uint headerBodySizesOnly = header & headerMask;
                ushort compressedBodySize = (ushort)(headerBodySizesOnly >> 16);
                ushort uncompressedSizeFromHeader = (ushort)((headerBodySizesOnly << 16) >> 16);

                // Header should store the LOGICAL size, not the buffer size
                Assert.AreEqual(size, uncompressedSizeFromHeader,
                    $"Header should store logical size {size}, not buffer size");

                // Decompress and verify
                LZ4CompressionSupport.Instance.Uncompress(
                    compressed, compressedSize,
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(size, uncompressedSize, "Decompressed size should match original");
                CollectionAssert.AreEqual(
                    original.Take(size),
                    uncompressed.Take(uncompressedSize),
                    "Data should match exactly"
                );

                SerializationUtils.ReturnByteArray(compressed);
                SerializationUtils.ReturnByteArray(uncompressed);
            }
        }

        [Test]
        public void Compress_MultipleRounds_DifferentTiers_AllSucceed()
        {
            // Simulate real-world scenario: multiple compressions from different tiers

            for (int round = 0; round < 10; round++)
            {
                // Tiny tier
                byte[] tiny = CreateTestData(10, (byte)round);
                LZ4CompressionSupport.Instance.Compress(tiny, (ushort)10, out var compTiny, out var compTinySize);
                LZ4CompressionSupport.Instance.Uncompress(compTiny, compTinySize, out var uncompTiny, out var uncompTinySize);
                Assert.AreEqual(10, uncompTinySize);
                CollectionAssert.AreEqual(tiny, uncompTiny.Take(10).ToArray());
                SerializationUtils.ReturnByteArray(compTiny);
                SerializationUtils.ReturnByteArray(uncompTiny);

                // Small tier
                byte[] small = CreateTestData(500, (byte)round);
                LZ4CompressionSupport.Instance.Compress(small, 500, out var compSmall, out var compSmallSize);
                LZ4CompressionSupport.Instance.Uncompress(compSmall, compSmallSize, out var uncompSmall, out var uncompSmallSize);
                Assert.AreEqual(500, uncompSmallSize);
                CollectionAssert.AreEqual(small, uncompSmall.Take(500).ToArray());
                SerializationUtils.ReturnByteArray(compSmall);
                SerializationUtils.ReturnByteArray(uncompSmall);

                // Medium tier
                byte[] medium = CreateTestData(5000, (byte)round);
                LZ4CompressionSupport.Instance.Compress(medium, 5000, out var compMedium, out var compMediumSize);
                LZ4CompressionSupport.Instance.Uncompress(compMedium, compMediumSize, out var uncompMedium, out var uncompMediumSize);
                Assert.AreEqual(5000, uncompMediumSize);
                CollectionAssert.AreEqual(medium, uncompMedium.Take(5000).ToArray());
                SerializationUtils.ReturnByteArray(compMedium);
                SerializationUtils.ReturnByteArray(uncompMedium);
            }
        }

        [Test]
        public void Compress_BelowThreshold_DoesNotCompress()
        {
            // Data below 100 bytes should not be compressed (only header added)
            byte[] tiny = CreateTestData(50);

            LZ4CompressionSupport.Instance.Compress(
                tiny, 50,
                out byte[] result, out ushort resultSize
            );

            // Result should be original data + 4-byte header (minimum)
            // Note: Actual size depends on LZ4 implementation details
            Assert.LessOrEqual(resultSize, 100,
                "Small data below threshold should not be significantly larger");
            Assert.GreaterOrEqual(resultSize, 54,
                "Should be at least original size + header");

            SerializationUtils.ReturnByteArray(result);
        }

        /// <summary>
        /// REGRESSION TEST: Verifies fix for spawn message loss bug.
        /// Prior to fix, compressedBytesUsed was set to the borrowed buffer size instead of
        /// actual used size, causing garbage bytes to be sent on the wire.
        /// </summary>
        [Test]
        public void Compress_BelowThreshold_CompressedBytesUsed_EqualsExactDataPlusHeader()
        {
            // Test sizes typical of spawn messages (80 bytes) and other small reliable messages
            int[] belowThresholdSizes = { 10, 20, 50, 70, 80, 90, 100 };
            const int HEADER_SIZE = 4;

            foreach (int size in belowThresholdSizes)
            {
                byte[] data = CreateTestData(size);

                LZ4CompressionSupport.Instance.Compress(
                    data, (ushort)size,
                    out byte[] compressed, out ushort compressedBytesUsed
                );

                // CRITICAL: compressedBytesUsed must be EXACTLY data + header, not buffer size
                ushort expectedSize = (ushort)(size + HEADER_SIZE);
                Assert.AreEqual(expectedSize, compressedBytesUsed,
                    $"Size {size}: compressedBytesUsed should be exactly {expectedSize} (data + 4-byte header), " +
                    $"not the borrowed buffer size. Got {compressedBytesUsed}");

                // Verify the borrowed buffer is larger (proving we're not just returning buffer size)
                Assert.Greater(compressed.Length, compressedBytesUsed,
                    $"Size {size}: Borrowed buffer ({compressed.Length}) should be larger than used size ({compressedBytesUsed})");

                // Verify round-trip still works
                LZ4CompressionSupport.Instance.Uncompress(
                    compressed, compressedBytesUsed,
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(size, uncompressedSize);
                CollectionAssert.AreEqual(data, uncompressed.Take(size).ToArray());

                SerializationUtils.ReturnByteArray(compressed);
                SerializationUtils.ReturnByteArray(uncompressed);
            }
        }

        /// <summary>
        /// REGRESSION TEST: Simulates exact spawn message scenario that caused 5% spawn loss.
        /// 80-byte spawn messages were sent with ~25 bytes of garbage appended.
        /// </summary>
        [Test]
        public void Compress_SpawnMessageSize_80Bytes_NoGarbageBytes()
        {
            // Exact size of Physics Cube Projectile spawn messages
            const int SPAWN_MESSAGE_SIZE = 80;
            const int HEADER_SIZE = 4;

            byte[] spawnData = CreateTestData(SPAWN_MESSAGE_SIZE);

            LZ4CompressionSupport.Instance.Compress(
                spawnData, SPAWN_MESSAGE_SIZE,
                out byte[] compressed, out ushort compressedBytesUsed
            );

            // The bug: compressedBytesUsed was ~105 instead of 84
            // MaximumOutputLength(80) + 4 = ~105, but actual data is only 84 bytes
            Assert.AreEqual(SPAWN_MESSAGE_SIZE + HEADER_SIZE, compressedBytesUsed,
                $"80-byte spawn message should produce exactly 84 bytes output, got {compressedBytesUsed}. " +
                "If this fails, garbage bytes from pooled buffer are being sent on the wire.");

            // Verify the actual data bytes match (not garbage)
            for (int i = 0; i < SPAWN_MESSAGE_SIZE; i++)
            {
                Assert.AreEqual(spawnData[i], compressed[HEADER_SIZE + i],
                    $"Byte {i} mismatch: spawn data should be copied verbatim after header");
            }

            SerializationUtils.ReturnByteArray(compressed);
        }

        /// <summary>
        /// REGRESSION TEST: Compression threshold must check actual bytes used, not buffer size.
        /// Prior bug at line 74 checked uncompressed.Length instead of uncompressedBytesUsed.
        /// This caused 80-byte spawns in 128-byte buffers to be incorrectly compressed.
        /// </summary>
        [Test]
        public void Compress_SmallDataInLargeBuffer_ShouldNotCompress()
        {
            const int DATA_SIZE = 80;  // Below 100-byte threshold
            const int BUFFER_SIZE = 128;  // Typical pool tier size
            const int HEADER_SIZE = 4;

            // Simulate pooled buffer scenario: buffer larger than data
            byte[] largeBuffer = new byte[BUFFER_SIZE];
            byte[] originalData = CreateTestData(DATA_SIZE);
            Buffer.BlockCopy(originalData, 0, largeBuffer, 0, DATA_SIZE);

            LZ4CompressionSupport.Instance.Compress(
                largeBuffer, DATA_SIZE,  // Pass large buffer but only 80 bytes of data
                out byte[] compressed, out ushort compressedBytesUsed
            );

            // CRITICAL: Must NOT compress (80 < 100 threshold)
            // Output should be exactly DATA_SIZE + HEADER_SIZE
            Assert.AreEqual(DATA_SIZE + HEADER_SIZE, compressedBytesUsed,
                $"REGRESSION! 80-byte data in 128-byte buffer should NOT be compressed. " +
                $"Expected {DATA_SIZE + HEADER_SIZE} bytes, got {compressedBytesUsed}. " +
                $"Compression threshold is checking buffer.Length instead of bytesUsed!");

            // Verify round-trip
            LZ4CompressionSupport.Instance.Uncompress(
                compressed, compressedBytesUsed,
                out byte[] uncompressed, out ushort uncompressedSize
            );

            Assert.AreEqual(DATA_SIZE, uncompressedSize);
            CollectionAssert.AreEqual(originalData, uncompressed.Take(uncompressedSize).ToArray());

            SerializationUtils.ReturnByteArray(compressed);
            SerializationUtils.ReturnByteArray(uncompressed);
        }

        /// <summary>
        /// REGRESSION TEST: Multiple sequential compressions should not leak data between messages.
        /// Prior bug could cause data from one message to appear in another due to buffer reuse.
        /// </summary>
        [Test]
        public void Compress_SequentialSmallMessages_NoDataLeakage()
        {
            const int MESSAGE_SIZE = 80;
            const int HEADER_SIZE = 4;
            const int MESSAGE_COUNT = 10;

            // Create distinct messages with known patterns
            byte[][] messages = new byte[MESSAGE_COUNT][];
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                messages[i] = new byte[MESSAGE_SIZE];
                // Fill with distinct pattern: message i has all bytes = i * 10
                for (int j = 0; j < MESSAGE_SIZE; j++)
                {
                    messages[i][j] = (byte)(i * 10 + (j % 10));
                }
            }

            // Compress and decompress each message, verifying no cross-contamination
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                LZ4CompressionSupport.Instance.Compress(
                    messages[i], MESSAGE_SIZE,
                    out byte[] compressed, out ushort compressedBytesUsed
                );

                // Verify exact size (no garbage)
                Assert.AreEqual(MESSAGE_SIZE + HEADER_SIZE, compressedBytesUsed,
                    $"Message {i}: Size should be exactly {MESSAGE_SIZE + HEADER_SIZE}");

                // Decompress and verify exact content
                LZ4CompressionSupport.Instance.Uncompress(
                    compressed, compressedBytesUsed,
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(MESSAGE_SIZE, uncompressedSize);
                CollectionAssert.AreEqual(messages[i], uncompressed.Take(MESSAGE_SIZE).ToArray(),
                    $"Message {i}: Content should match exactly, no data from other messages");

                SerializationUtils.ReturnByteArray(compressed);
                SerializationUtils.ReturnByteArray(uncompressed);
            }
        }

        [Test]
        public void Compress_AboveThreshold_DoesCompress()
        {
            // Data above 100 bytes should be compressed
            // Create highly compressible data (repeating pattern)
            byte[] compressible = new byte[500];
            for (int i = 0; i < compressible.Length; i++)
            {
                compressible[i] = (byte)(i % 10);
            }

            LZ4CompressionSupport.Instance.Compress(
                compressible, 500,
                out byte[] result, out ushort resultSize
            );

            // Compressed size should be less than original (for compressible data)
            Assert.Less(resultSize, 500, "Compressible data above threshold should be compressed");

            // Verify decompression works
            LZ4CompressionSupport.Instance.Uncompress(
                result, resultSize,
                out byte[] uncompressed, out ushort uncompressedSize
            );

            Assert.AreEqual(500, uncompressedSize);
            CollectionAssert.AreEqual(compressible, uncompressed.Take(500).ToArray());

            SerializationUtils.ReturnByteArray(result);
            SerializationUtils.ReturnByteArray(uncompressed);
        }

        [Test]
        public void Compress_IncompressibleData_AboveThreshold_SucceedsWithWorstCaseExpansion()
        {
            // CRITICAL TEST: Verifies the fix for LZ4 "corrupted block" errors
            // Incompressible random data causes worst-case LZ4 expansion
            // This test ensures we allocate enough buffer space for LZ4's output

            // Test at boundary of tiny/small tier (128-200 bytes)
            int[] criticalSizes = { 128, 150, 200, 256, 500, 1000 };

            foreach (int size in criticalSizes)
            {
                // Create incompressible random data
                byte[] incompressible = CreateTestData(size);

                // This should NOT throw "LZ4 block is corrupted, or invalid length has been given"
                LZ4CompressionSupport.Instance.Compress(
                    incompressible, (ushort)size,
                    out byte[] compressed, out ushort compressedSize
                );

                Assert.Greater(compressedSize, 0, $"Compressed size should be > 0 for {size} bytes");

                // Verify the compressed data includes proper header
                Assert.GreaterOrEqual(compressedSize, 4, "Should have at least 4-byte header");

                // Decompress and verify integrity
                LZ4CompressionSupport.Instance.Uncompress(
                    compressed, compressedSize,
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(size, uncompressedSize, $"Uncompressed size should match original for {size} bytes");
                CollectionAssert.AreEqual(
                    incompressible.Take(size).ToArray(),
                    uncompressed.Take(uncompressedSize).ToArray(),
                    $"Data integrity should be preserved for {size} bytes"
                );

                SerializationUtils.ReturnByteArray(compressed);
                SerializationUtils.ReturnByteArray(uncompressed);
            }
        }

        [Test]
        public void Compress_EdgeCaseSizes_JustAboveThreshold_AllSucceed()
        {
            // Test sizes just above compression threshold (100 bytes)
            // These are most likely to trigger buffer allocation bugs
            int[] edgeSizes = { 101, 105, 110, 120, 127, 128, 129 };

            foreach (int size in edgeSizes)
            {
                byte[] data = CreateTestData(size);

                // Should not throw
                LZ4CompressionSupport.Instance.Compress(
                    data, (ushort)size,
                    out byte[] compressed, out ushort compressedSize
                );

                LZ4CompressionSupport.Instance.Uncompress(
                    compressed, compressedSize,
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(size, uncompressedSize, $"Size {size} should round-trip correctly");
                CollectionAssert.AreEqual(data, uncompressed.Take(size).ToArray(),
                    $"Data integrity for size {size}");

                SerializationUtils.ReturnByteArray(compressed);
                SerializationUtils.ReturnByteArray(uncompressed);
            }
        }

        [Test]
        public void Compress_TierBoundaries_AllTiersHandleCorrectly()
        {
            // Test at exact tier boundaries to verify pool allocation
            // Tiny: 8-128, Small: 128-1024, Medium: 1024-12288, Large: 12288-65536
            //
            // IMPORTANT: LZ4CompressionSupport has a header limitation:
            // - Max uncompressed size: 65,535 bytes (16-bit)
            // - Max compressed size: 32,767 bytes (15-bit)
            // Random incompressible data can expand to ~1.01x original size
            // So we test up to ~32KB to stay within compressed size limit
            int[] tierBoundaries = {
                8, 127, 128, 129,           // Tiny boundaries
                1023, 1024, 1025,           // Small boundaries
                12287, 12288, 12289,        // Medium boundaries
                20000, 32000                // Large tier (within header limits)
            };

            foreach (int size in tierBoundaries)
            {
                byte[] data = CreateTestData(size);

                LZ4CompressionSupport.Instance.Compress(
                    data, (ushort)size,
                    out byte[] compressed, out ushort compressedSize
                );

                LZ4CompressionSupport.Instance.Uncompress(
                    compressed, compressedSize,
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(size, uncompressedSize, $"Tier boundary {size} should work correctly");
                CollectionAssert.AreEqual(data, uncompressed.Take(size).ToArray(),
                    $"Data integrity at tier boundary {size}");

                SerializationUtils.ReturnByteArray(compressed);
                SerializationUtils.ReturnByteArray(uncompressed);
            }
        }

        [Test]
        public void Compress_StressTest_ManyRapidCompressions_NoCorruption()
        {
            // Stress test: Rapid compressions across all tiers
            // Simulates high-frequency network traffic
            var random = new Random(12345);

            for (int i = 0; i < 100; i++)
            {
                // Random size between 10 and 5000 bytes
                int size = random.Next(10, 5000);
                byte[] data = CreateTestData(size, (byte)i);

                LZ4CompressionSupport.Instance.Compress(
                    data, (ushort)size,
                    out byte[] compressed, out ushort compressedSize
                );

                LZ4CompressionSupport.Instance.Uncompress(
                    compressed, compressedSize,
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(size, uncompressedSize, $"Iteration {i} size {size} failed");
                CollectionAssert.AreEqual(data, uncompressed.Take(size).ToArray(),
                    $"Iteration {i} size {size} data corrupted");

                SerializationUtils.ReturnByteArray(compressed);
                SerializationUtils.ReturnByteArray(uncompressed);
            }
        }

        [Test]
        public void Compress_HeaderSizeLimit_ThrowsForTooLargeData()
        {
            // DOCUMENTS KNOWN LIMITATION: LZ4CompressionSupport has header size constraints
            // - 15-bit compressed size: max 32,767 bytes
            // - 16-bit uncompressed size: max 65,535 bytes
            //
            // For incompressible data, compressed size ≈ uncompressed size + overhead
            // So effective max is ~32KB for random data
            //
            // MTU_x32 (44,800 bytes) exceeds this limit and will throw ArgumentOutOfRangeException
            // This is EXPECTED behavior - not a bug

            byte[] tooLarge = CreateTestData(44800); // MTU_x32

            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            {
                LZ4CompressionSupport.Instance.Compress(
                    tooLarge, (ushort)44800,
                    out byte[] compressed, out ushort compressedSize
                );
            }, "Data larger than ~32KB should throw ArgumentOutOfRangeException due to header size limits");
        }

        [Test]
        public void Compress_MaxSafeSize_32KB_Succeeds()
        {
            // Verify that 32KB (max safe size for incompressible data) works correctly
            // This is the practical upper limit for LZ4CompressionSupport with random data

            byte[] maxSafeSize = CreateTestData(32000);

            // Should NOT throw
            LZ4CompressionSupport.Instance.Compress(
                maxSafeSize, 32000,
                out byte[] compressed, out ushort compressedSize
            );

            LZ4CompressionSupport.Instance.Uncompress(
                compressed, compressedSize,
                out byte[] uncompressed, out ushort uncompressedSize
            );

            Assert.AreEqual(32000, uncompressedSize, "32KB should compress/decompress successfully");
            CollectionAssert.AreEqual(maxSafeSize, uncompressed.Take(32000).ToArray(),
                "Data integrity at max safe size");

            SerializationUtils.ReturnByteArray(compressed);
            SerializationUtils.ReturnByteArray(uncompressed);
        }

        /// <summary>
        /// CRITICAL REGRESSION TEST: Simulates rapid-fire spawn scenario.
        /// Tests that compressed buffers returned to pool are not reused before data is copied.
        /// This is the exact scenario causing 6% spawn message loss.
        /// </summary>
        [Test]
        public void Compress_RapidFireSpawns_BufferContentsAreIndependent()
        {
            const int SPAWN_COUNT = 10;
            const int SPAWN_SIZE = 80;  // Exact size of missing spawns
            const int HEADER_SIZE = 4;

            // Simulate 10 spawn events with unique GONetIds (like the real scenario)
            // Each spawn has a unique 4-byte GONetId at offset 4
            byte[][] spawnMessages = new byte[SPAWN_COUNT][];
            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                spawnMessages[i] = new byte[SPAWN_SIZE];
                // Set spawn event header: 0D 0D 00 00
                spawnMessages[i][0] = 0x0D;
                spawnMessages[i][1] = 0x0D;
                spawnMessages[i][2] = 0x00;
                spawnMessages[i][3] = 0x00;
                // Set unique GONetId at offset 4 (little-endian)
                uint gonetId = (uint)(27647 + (i * 1024));  // Unique IDs like the real scenario
                spawnMessages[i][4] = (byte)(gonetId & 0xFF);
                spawnMessages[i][5] = (byte)((gonetId >> 8) & 0xFF);
                spawnMessages[i][6] = (byte)((gonetId >> 16) & 0xFF);
                spawnMessages[i][7] = (byte)((gonetId >> 24) & 0xFF);
                // Fill rest with unique pattern
                for (int j = 8; j < SPAWN_SIZE; j++)
                {
                    spawnMessages[i][j] = (byte)(i * 10 + j);
                }
            }

            // Compress all messages rapidly (simulating same-frame batch)
            byte[][] compressedBuffers = new byte[SPAWN_COUNT][];
            ushort[] compressedSizes = new ushort[SPAWN_COUNT];

            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                LZ4CompressionSupport.Instance.Compress(
                    spawnMessages[i], SPAWN_SIZE,
                    out compressedBuffers[i], out compressedSizes[i]
                );

                // Verify size immediately
                Assert.AreEqual(SPAWN_SIZE + HEADER_SIZE, compressedSizes[i],
                    $"Spawn {i}: Size should be exactly {SPAWN_SIZE + HEADER_SIZE}");
            }

            // NOW verify that each compressed buffer has UNIQUE content
            // This is the critical test - if buffers are aliased, they'll all have the same content
            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                // Extract GONetId from compressed buffer (offset 4 after 4-byte header = offset 8)
                uint extractedId = (uint)(
                    compressedBuffers[i][HEADER_SIZE + 4] |
                    (compressedBuffers[i][HEADER_SIZE + 5] << 8) |
                    (compressedBuffers[i][HEADER_SIZE + 6] << 16) |
                    (compressedBuffers[i][HEADER_SIZE + 7] << 24)
                );

                uint expectedId = (uint)(27647 + (i * 1024));
                Assert.AreEqual(expectedId, extractedId,
                    $"BUFFER ALIASING DETECTED! Spawn {i}: Expected GONetId {expectedId}, got {extractedId}. " +
                    $"This indicates compressed buffers are being reused/overwritten before data is consumed.");
            }

            // Verify all messages decompress to original content
            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                LZ4CompressionSupport.Instance.Uncompress(
                    compressedBuffers[i], compressedSizes[i],
                    out byte[] uncompressed, out ushort uncompressedSize
                );

                Assert.AreEqual(SPAWN_SIZE, uncompressedSize);
                CollectionAssert.AreEqual(spawnMessages[i], uncompressed.Take(SPAWN_SIZE).ToArray(),
                    $"Spawn {i}: Decompressed content should match original");

                SerializationUtils.ReturnByteArray(uncompressed);
            }

            // Cleanup
            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                SerializationUtils.ReturnByteArray(compressedBuffers[i]);
            }
        }

        /// <summary>
        /// CRITICAL REGRESSION TEST: Simulates the exact send pattern that causes spawn loss.
        /// Compress, copy to reliable buffer, return original - repeat rapidly.
        /// Verifies data integrity after pool return.
        /// </summary>
        [Test]
        public void Compress_SimulateSendPath_CopyThenReturn_DataIntegrity()
        {
            const int SPAWN_COUNT = 10;
            const int SPAWN_SIZE = 80;
            const int HEADER_SIZE = 4;
            const int GONET_HEADER_SIZE = 5;  // channelId (1) + size (4)

            // Create unique spawn messages
            byte[][] spawnMessages = new byte[SPAWN_COUNT][];
            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                spawnMessages[i] = new byte[SPAWN_SIZE];
                spawnMessages[i][0] = 0x0D;
                spawnMessages[i][1] = 0x0D;
                spawnMessages[i][2] = 0x00;
                spawnMessages[i][3] = 0x00;
                uint gonetId = (uint)(27647 + (i * 1024));
                spawnMessages[i][4] = (byte)(gonetId & 0xFF);
                spawnMessages[i][5] = (byte)((gonetId >> 8) & 0xFF);
                spawnMessages[i][6] = (byte)((gonetId >> 16) & 0xFF);
                spawnMessages[i][7] = (byte)((gonetId >> 24) & 0xFF);
                for (int j = 8; j < SPAWN_SIZE; j++)
                {
                    spawnMessages[i][j] = (byte)(i * 10 + j);
                }
            }

            // Simulate the exact send path from GONetConnections.SendMessageOverChannel:
            // 1. Compress
            // 2. Copy to reliable buffer (simulated)
            // 3. Return compressed buffer to pool
            // 4. Repeat for next message

            // "Reliable buffers" that hold copies of the data
            byte[][] reliableBuffers = new byte[SPAWN_COUNT][];
            int[] reliableSizes = new int[SPAWN_COUNT];

            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                // Step 1: Compress
                LZ4CompressionSupport.Instance.Compress(
                    spawnMessages[i], SPAWN_SIZE,
                    out byte[] compressed, out ushort compressedSize
                );

                // Step 2: Simulate GONet header prepending + copy to "reliable buffer"
                int totalSize = GONET_HEADER_SIZE + compressedSize;
                reliableBuffers[i] = new byte[totalSize];
                reliableBuffers[i][0] = 6;  // Channel ID
                reliableBuffers[i][1] = (byte)(compressedSize & 0xFF);
                reliableBuffers[i][2] = (byte)((compressedSize >> 8) & 0xFF);
                reliableBuffers[i][3] = 0;
                reliableBuffers[i][4] = 0;
                Buffer.BlockCopy(compressed, 0, reliableBuffers[i], GONET_HEADER_SIZE, compressedSize);
                reliableSizes[i] = totalSize;

                // Step 3: Return compressed buffer to pool (THIS IS THE BUG TRIGGER)
                SerializationUtils.ReturnByteArray(compressed);
            }

            // NOW verify that each reliable buffer has UNIQUE content
            // If there's buffer aliasing, they'll all have the same GONetId
            for (int i = 0; i < SPAWN_COUNT; i++)
            {
                // Extract GONetId from reliable buffer
                // Layout: GONet header (5) + compression header (4) + spawn data (GONetId at offset 4)
                int gonetIdOffset = GONET_HEADER_SIZE + HEADER_SIZE + 4;
                uint extractedId = (uint)(
                    reliableBuffers[i][gonetIdOffset] |
                    (reliableBuffers[i][gonetIdOffset + 1] << 8) |
                    (reliableBuffers[i][gonetIdOffset + 2] << 16) |
                    (reliableBuffers[i][gonetIdOffset + 3] << 24)
                );

                uint expectedId = (uint)(27647 + (i * 1024));
                Assert.AreEqual(expectedId, extractedId,
                    $"SEND PATH BUFFER ALIASING! Message {i}: Expected GONetId {expectedId}, got {extractedId}. " +
                    $"The compressed buffer was returned to pool before reliable copy completed, " +
                    $"and was reused for subsequent messages.");
            }
        }

        /// <summary>
        /// CRITICAL REGRESSION TEST: Verify buffer content AFTER return to pool.
        /// This simulates what happens when a returned buffer is immediately reused.
        /// </summary>
        [Test]
        public void Compress_BufferReturnAndReuse_SubsequentDataIsIndependent()
        {
            const int SIZE = 80;
            const int HEADER_SIZE = 4;

            // First compression
            byte[] data1 = CreateTestData(SIZE, seed: 1);
            LZ4CompressionSupport.Instance.Compress(
                data1, SIZE,
                out byte[] compressed1, out ushort size1
            );

            // Copy the data immediately (as reliable channel would)
            byte[] copy1 = new byte[size1];
            Buffer.BlockCopy(compressed1, 0, copy1, 0, size1);

            // Return to pool
            SerializationUtils.ReturnByteArray(compressed1);

            // Second compression (may get same buffer from pool)
            byte[] data2 = CreateTestData(SIZE, seed: 2);
            LZ4CompressionSupport.Instance.Compress(
                data2, SIZE,
                out byte[] compressed2, out ushort size2
            );

            // If same buffer was returned, verify content is independent
            // The copy1 should still have data1's content, not data2's
            for (int i = HEADER_SIZE; i < size1; i++)
            {
                Assert.AreEqual(data1[i - HEADER_SIZE], copy1[i],
                    $"Copy of first compression at byte {i} should still have original data, " +
                    $"not be affected by subsequent compression");
            }

            // Verify compressed2 has data2's content
            for (int i = HEADER_SIZE; i < size2; i++)
            {
                Assert.AreEqual(data2[i - HEADER_SIZE], compressed2[i],
                    $"Second compression at byte {i} should have data2's content");
            }

            SerializationUtils.ReturnByteArray(compressed2);
        }
    }
}
