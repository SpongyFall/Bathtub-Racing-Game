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
using System;
using BitConverter = GONet.Utils.BitConverter;

namespace GONet.Editor.UnitTests.Utils
{
    /// <summary>
    /// Comprehensive test suite for GONet.Utils.BitConverter.
    /// Tests all overloads to ensure correct byte-level encoding.
    ///
    /// CRITICAL: This class had a bug where byte/sbyte overloads were missing,
    /// causing implicit conversion to short and writing 2 bytes instead of 1.
    /// This corrupted GONet channel IDs and broke client initialization.
    /// </summary>
    [TestFixture]
    public class BitConverterTests
    {
        private byte[] buffer;

        [SetUp]
        public void SetUp()
        {
            // Fresh buffer for each test
            buffer = new byte[16];
        }

        [TearDown]
        public void TearDown()
        {
            buffer = null;
        }

        #region Byte/SByte Tests (Critical - These Were Missing!)

        [Test]
        public void GetBytes_Byte_WritesExactlyOneByte()
        {
            // CRITICAL: This test validates the bug fix for missing byte overload
            byte value = 123;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(value, buffer[0], "Byte value should be at offset 0");
            Assert.AreEqual(0, buffer[1], "Second byte should be untouched (only 1 byte written)");
        }

        [Test]
        public void GetBytes_Byte_MinValue_Success()
        {
            byte value = byte.MinValue; // 0
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(0, buffer[0]);
        }

        [Test]
        public void GetBytes_Byte_MaxValue_Success()
        {
            byte value = byte.MaxValue; // 255
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(255, buffer[0]);
        }

        [Test]
        public void GetBytes_Byte_WithOffset_Success()
        {
            byte value = 42;
            BitConverter.GetBytes(value, buffer, 5);

            Assert.AreEqual(0, buffer[4], "Byte before offset should be untouched");
            Assert.AreEqual(42, buffer[5], "Byte at offset should contain value");
            Assert.AreEqual(0, buffer[6], "Byte after offset should be untouched");
        }

        [Test]
        public void GetBytes_SByte_WritesExactlyOneByte()
        {
            sbyte value = -123;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual((byte)value, buffer[0], "SByte value should be at offset 0");
            Assert.AreEqual(0, buffer[1], "Second byte should be untouched (only 1 byte written)");
        }

        [Test]
        public void GetBytes_SByte_MinValue_Success()
        {
            sbyte value = sbyte.MinValue; // -128
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(128, buffer[0]); // -128 as unsigned byte = 128
        }

        [Test]
        public void GetBytes_SByte_MaxValue_Success()
        {
            sbyte value = sbyte.MaxValue; // 127
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(127, buffer[0]);
        }

        [Test]
        public void GetBytes_Byte_ChannelIDScenario_Success()
        {
            // CRITICAL: This replicates the bug scenario - GONet channel IDs are bytes
            // Channel 6 (initialization completion), channel 8/9 (initialization channels)
            byte channel6 = 6;
            byte channel8 = 8;
            byte channel9 = 9;

            BitConverter.GetBytes(channel6, buffer, 0);
            BitConverter.GetBytes(channel8, buffer, 1);
            BitConverter.GetBytes(channel9, buffer, 2);

            Assert.AreEqual(6, buffer[0], "Channel 6 should be at byte 0");
            Assert.AreEqual(8, buffer[1], "Channel 8 should be at byte 1");
            Assert.AreEqual(9, buffer[2], "Channel 9 should be at byte 2");
            Assert.AreEqual(0, buffer[3], "No extra bytes should be written");
        }

        #endregion

        #region Bool Tests

        [Test]
        public void GetBytes_Bool_True_Success()
        {
            BitConverter.GetBytes(true, buffer, 0);
            Assert.AreEqual(1, buffer[0]);
        }

        [Test]
        public void GetBytes_Bool_False_Success()
        {
            BitConverter.GetBytes(false, buffer, 0);
            Assert.AreEqual(0, buffer[0]);
        }

        #endregion

        #region Char Tests

        [Test]
        public void GetBytes_Char_Success()
        {
            char value = 'A'; // 0x0041
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(2, sizeof(char));
            // Platform-dependent endianness
            bool isLittleEndian = System.BitConverter.IsLittleEndian;
            if (isLittleEndian)
            {
                Assert.AreEqual(0x41, buffer[0]);
                Assert.AreEqual(0x00, buffer[1]);
            }
        }

        #endregion

        #region Short Tests

        [Test]
        public void GetBytes_Short_PositiveValue_Success()
        {
            short value = 12345;
            BitConverter.GetBytes(value, buffer, 0);

            // Verify size
            Assert.AreEqual(2, sizeof(short));

            // Round-trip test
            short roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(short*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_Short_NegativeValue_Success()
        {
            short value = -12345;
            BitConverter.GetBytes(value, buffer, 0);

            short roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(short*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_Short_MinMaxValues_Success()
        {
            BitConverter.GetBytes(short.MinValue, buffer, 0);
            short min;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    min = *(short*)ptr;
                }
            }
            Assert.AreEqual(short.MinValue, min);

            Array.Clear(buffer, 0, buffer.Length);

            BitConverter.GetBytes(short.MaxValue, buffer, 0);
            short max;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    max = *(short*)ptr;
                }
            }
            Assert.AreEqual(short.MaxValue, max);
        }

        #endregion

        #region Int Tests

        [Test]
        public void GetBytes_Int_PositiveValue_Success()
        {
            int value = 123456789;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(4, sizeof(int));

            int roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(int*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_Int_NegativeValue_Success()
        {
            int value = -123456789;
            BitConverter.GetBytes(value, buffer, 0);

            int roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(int*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_Int_MinMaxValues_Success()
        {
            BitConverter.GetBytes(int.MinValue, buffer, 0);
            int min;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    min = *(int*)ptr;
                }
            }
            Assert.AreEqual(int.MinValue, min);

            Array.Clear(buffer, 0, buffer.Length);

            BitConverter.GetBytes(int.MaxValue, buffer, 0);
            int max;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    max = *(int*)ptr;
                }
            }
            Assert.AreEqual(int.MaxValue, max);
        }

        #endregion

        #region Long Tests

        [Test]
        public void GetBytes_Long_PositiveValue_Success()
        {
            long value = 123456789012345L;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(8, sizeof(long));

            long roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(long*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_Long_NegativeValue_Success()
        {
            long value = -123456789012345L;
            BitConverter.GetBytes(value, buffer, 0);

            long roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(long*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        #endregion

        #region UShort Tests

        [Test]
        public void GetBytes_UShort_Success()
        {
            ushort value = 54321;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(2, sizeof(ushort));

            ushort roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(ushort*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_UShort_MaxValue_Success()
        {
            ushort value = ushort.MaxValue;
            BitConverter.GetBytes(value, buffer, 0);

            ushort roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(ushort*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        #endregion

        #region UInt Tests

        [Test]
        public void GetBytes_UInt_Success()
        {
            uint value = 3141592653u;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(4, sizeof(uint));

            uint roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(uint*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_UInt_MaxValue_Success()
        {
            uint value = uint.MaxValue;
            BitConverter.GetBytes(value, buffer, 0);

            uint roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(uint*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        #endregion

        #region ULong Tests

        [Test]
        public void GetBytes_ULong_Success()
        {
            ulong value = 18446744073709551000UL;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(8, sizeof(ulong));

            ulong roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(ulong*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        [Test]
        public void GetBytes_ULong_MaxValue_Success()
        {
            ulong value = ulong.MaxValue;
            BitConverter.GetBytes(value, buffer, 0);

            ulong roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(ulong*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip);
        }

        #endregion

        #region Float Tests

        [Test]
        public void GetBytes_Float_Success()
        {
            float value = 3.14159f;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(4, sizeof(float));

            float roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(float*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip, 0.00001f);
        }

        [Test]
        public void GetBytes_Float_NegativeValue_Success()
        {
            float value = -123.456f;
            BitConverter.GetBytes(value, buffer, 0);

            float roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(float*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip, 0.00001f);
        }

        [Test]
        public void GetBytes_Float_SpecialValues_Success()
        {
            // Test NaN
            BitConverter.GetBytes(float.NaN, buffer, 0);
            float nan;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    nan = *(float*)ptr;
                }
            }
            Assert.IsTrue(float.IsNaN(nan));

            // Test Infinity
            Array.Clear(buffer, 0, buffer.Length);
            BitConverter.GetBytes(float.PositiveInfinity, buffer, 0);
            float inf;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    inf = *(float*)ptr;
                }
            }
            Assert.AreEqual(float.PositiveInfinity, inf);
        }

        #endregion

        #region Double Tests

        [Test]
        public void GetBytes_Double_Success()
        {
            double value = 3.141592653589793;
            BitConverter.GetBytes(value, buffer, 0);

            Assert.AreEqual(8, sizeof(double));

            double roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(double*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip, 0.0000000001);
        }

        [Test]
        public void GetBytes_Double_NegativeValue_Success()
        {
            double value = -123456.789012;
            BitConverter.GetBytes(value, buffer, 0);

            double roundTrip;
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    roundTrip = *(double*)ptr;
                }
            }
            Assert.AreEqual(value, roundTrip, 0.0000001);
        }

        #endregion

        #region Offset Tests

        [Test]
        public void GetBytes_WithOffset_DoesNotOverwritePreviousData()
        {
            // Write different types at different offsets
            BitConverter.GetBytes((byte)42, buffer, 0);
            BitConverter.GetBytes((short)12345, buffer, 1);
            BitConverter.GetBytes(987654321, buffer, 3);

            Assert.AreEqual(42, buffer[0], "Byte at offset 0");

            short shortVal;
            unsafe
            {
                fixed (byte* ptr = &buffer[1])
                {
                    shortVal = *(short*)ptr;
                }
            }
            Assert.AreEqual(12345, shortVal, "Short at offset 1");

            int intVal;
            unsafe
            {
                fixed (byte* ptr = &buffer[3])
                {
                    intVal = *(int*)ptr;
                }
            }
            Assert.AreEqual(987654321, intVal, "Int at offset 3");
        }

        #endregion

        #region Exception Tests

        [Test]
        public void GetBytes_NullDestination_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => BitConverter.GetBytes((byte)0, null, 0));
            Assert.Throws<ArgumentNullException>(() => BitConverter.GetBytes((short)0, null, 0));
            Assert.Throws<ArgumentNullException>(() => BitConverter.GetBytes(0, null, 0));
            Assert.Throws<ArgumentNullException>(() => BitConverter.GetBytes(0L, null, 0));
            Assert.Throws<ArgumentNullException>(() => BitConverter.GetBytes(0f, null, 0));
            Assert.Throws<ArgumentNullException>(() => BitConverter.GetBytes(0.0, null, 0));
        }

        [Test]
        public void GetBytes_NegativeOffset_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BitConverter.GetBytes((byte)0, buffer, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BitConverter.GetBytes((short)0, buffer, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BitConverter.GetBytes(0, buffer, -1));
        }

        [Test]
        public void GetBytes_OffsetPlusSizeExceedsBufferLength_ThrowsArgumentOutOfRangeException()
        {
            byte[] smallBuffer = new byte[4];

            // Byte at offset 3 is OK (writes 1 byte)
            Assert.DoesNotThrow(() => BitConverter.GetBytes((byte)0, smallBuffer, 3));

            // Byte at offset 4 exceeds buffer
            Assert.Throws<ArgumentOutOfRangeException>(() => BitConverter.GetBytes((byte)0, smallBuffer, 4));

            // Short at offset 3 exceeds buffer (needs 2 bytes)
            Assert.Throws<ArgumentOutOfRangeException>(() => BitConverter.GetBytes((short)0, smallBuffer, 3));

            // Int at offset 1 exceeds buffer (needs 4 bytes, only 3 available)
            Assert.Throws<ArgumentOutOfRangeException>(() => BitConverter.GetBytes(0, smallBuffer, 1));
        }

        #endregion

        #region Regression Tests (Bug Scenarios)

        [Test]
        public void GetBytes_GONetChannelIDEncoding_NoCorruption()
        {
            // CRITICAL: This test validates the actual bug that was found
            // GONetConnections encodes channel ID (byte) + size (uint) in message header
            // Before fix: channel ID was being implicitly converted to short, writing 2 bytes
            // After fix: channel ID writes exactly 1 byte

            byte channelId = 6; // Initialization completion channel
            uint bodySize = 26; // Body size after channel + size header

            // Encode header (matching GONetConnections.cs line 238-241)
            BitConverter.GetBytes(channelId, buffer, 0);
            BitConverter.GetBytes(bodySize, buffer, 1); // Should start at byte 1, NOT byte 2!

            // Verify channel ID
            Assert.AreEqual(6, buffer[0], "Channel ID should be at byte 0");

            // Verify size (starts at byte 1)
            uint readSize;
            unsafe
            {
                fixed (byte* ptr = &buffer[1])
                {
                    readSize = *(uint*)ptr;
                }
            }
            Assert.AreEqual(26, readSize, "Body size should be readable starting at byte 1");

            // Verify no corruption - if byte overload is missing, this would fail
            // because short would write 2 bytes, shifting size to bytes 2-5 instead of 1-4
        }

        [Test]
        public void GetBytes_AllChannelIDValues_NoCorruption()
        {
            // Test all possible GONet channel IDs (0-10 are defined)
            for (byte channelId = 0; channelId <= 10; channelId++)
            {
                Array.Clear(buffer, 0, buffer.Length);

                BitConverter.GetBytes(channelId, buffer, 0);
                BitConverter.GetBytes(100u, buffer, 1); // Dummy size

                Assert.AreEqual(channelId, buffer[0], $"Channel {channelId} should be at byte 0");

                // Verify size is still at correct offset
                uint readSize;
                unsafe
                {
                    fixed (byte* ptr = &buffer[1])
                    {
                        readSize = *(uint*)ptr;
                    }
                }
                Assert.AreEqual(100, readSize, $"Size should be readable at byte 1 for channel {channelId}");
            }
        }

        #endregion
    }
}
