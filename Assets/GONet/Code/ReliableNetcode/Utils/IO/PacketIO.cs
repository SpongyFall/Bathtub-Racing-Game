using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReliableNetcode.Utils
{
		internal static class PacketIO
		{
		public static bool SequenceGreaterThan(ushort s1, ushort s2)
		{
			return ((s1 > s2) && (s1 - s2 <= 32768)) ||
				((s1 < s2) && (s2 - s1 > 32768));
		}

		public static bool SequenceLessThan(ushort s1, ushort s2)
		{
			return SequenceGreaterThan(s2, s1);
		}

			public static int ReadPacketHeader(byte[] packetBuffer, int offset, int bufferLength, out byte channelID, out uint sessionId, out ushort sequence, out ushort ack, out uint ackBits)
			{
				if (packetBuffer == null) throw new ArgumentNullException(nameof(packetBuffer));
				if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
				if (bufferLength < 0) throw new ArgumentOutOfRangeException(nameof(bufferLength));
				if (offset + bufferLength > packetBuffer.Length) throw new ArgumentOutOfRangeException(nameof(bufferLength));

				// Minimum: prefix(1) + channel(1) + sessionId(4) + ack(2) = 8 bytes (ACK-only packet with all-1 ackBits omitted)
				if (bufferLength < 8)
					throw new FormatException("Buffer too small for packet header");

				long endPosition = (long)offset + bufferLength;

				using (var reader = ByteArrayReaderWriter.Get(packetBuffer))
				{
					reader.SeekRead(offset);

					void EnsureRemaining(int bytesNeeded)
					{
						if (reader.ReadPosition + bytesNeeded > endPosition)
							throw new FormatException("Buffer too small for packet header");
					}

					EnsureRemaining(1);
					byte prefixByte = reader.ReadByte();
					if ((prefixByte & 1) != 0)
					{
						throw new InvalidOperationException("Header does not indicate regular packet");
					}

					EnsureRemaining(1 + 4);
					channelID = reader.ReadByte();
					sessionId = reader.ReadUInt32();

					bool isAckOnly = (prefixByte & 0x80) != 0;

					if (!isAckOnly)
					{
						EnsureRemaining(2);
						sequence = reader.ReadUInt16();
					}
					else
					{
						sequence = 0;
					}

					// ack packets don't have sequence numbers, so diff-encoding is not valid/expected
					bool isAckDiffEncoded = !isAckOnly && (prefixByte & (1 << 5)) != 0;
					if (isAckDiffEncoded)
					{
						EnsureRemaining(1);
						byte sequenceDiff = reader.ReadByte();
						ack = (ushort)(sequence - sequenceDiff);
					}
					else
					{
						EnsureRemaining(2);
						ack = reader.ReadUInt16();
					}

					ackBits = 0xFFFFFFFF;

					if ((prefixByte & (1 << 1)) != 0)
					{
						EnsureRemaining(1);
						ackBits &= 0xFFFFFF00;
						ackBits |= reader.ReadByte();
					}

					if ((prefixByte & (1 << 2)) != 0)
					{
						EnsureRemaining(1);
						ackBits &= 0xFFFF00FF;
						ackBits |= (uint)(reader.ReadByte() << 8);
					}

					if ((prefixByte & (1 << 3)) != 0)
					{
						EnsureRemaining(1);
						ackBits &= 0xFF00FFFF;
						ackBits |= (uint)(reader.ReadByte() << 16);
					}

					if ((prefixByte & (1 << 4)) != 0)
					{
						EnsureRemaining(1);
						ackBits &= 0x00FFFFFF;
						ackBits |= (uint)(reader.ReadByte() << 24);
					}

					return (int)reader.ReadPosition - offset;
				}
			}

			public static int ReadFragmentHeader(byte[] packetBuffer, int offset, int bufferLength, int maxFragments, int fragmentSize, out int fragmentID, out int numFragments, out int fragmentBytes,
				out uint sessionId, out ushort sequence, out ushort ack, out uint ackBits, out byte channelID)
			{
				if (packetBuffer == null) throw new ArgumentNullException(nameof(packetBuffer));
				if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
				if (bufferLength < 0) throw new ArgumentOutOfRangeException(nameof(bufferLength));
				if (offset + bufferLength > packetBuffer.Length) throw new ArgumentOutOfRangeException(nameof(bufferLength));

				if (bufferLength < Defines.FRAGMENT_HEADER_BYTES)
					throw new FormatException(string.Concat("Buffer too small for packet header.  bufferLength: ", bufferLength));

				using (var reader = ByteArrayReaderWriter.Get(packetBuffer))
				{
					reader.SeekRead(offset);

					byte prefixByte = reader.ReadByte();

					if ((prefixByte & 1) == 0)
						throw new FormatException(string.Concat("Packet header indicates non-fragment packet.  prefixByte: ", prefixByte));

					channelID = reader.ReadByte();
					sessionId = reader.ReadUInt32();
					sequence = reader.ReadUInt16();

				fragmentID = reader.ReadByte();
				numFragments = reader.ReadByte() + 1;

				if (numFragments > maxFragments)
					throw new FormatException(string.Concat("Packet header indicates fragments outside of max range. numFragments: ", numFragments));

				if (fragmentID >= numFragments)
					throw new FormatException(string.Concat("Packet header indicates fragment ID outside of fragment count. fragmentID: ", fragmentID));

				fragmentBytes = bufferLength - Defines.FRAGMENT_HEADER_BYTES;

				ushort packetSequence = 0;
				ushort packetAck = 0;
				uint packetAckBits = 0;

					byte packetChannelID = 0;
					uint packetSessionId = 0;
	                
					if (fragmentID == 0)
					{
						int packetHeaderBytes = ReadPacketHeader(packetBuffer, offset + Defines.FRAGMENT_HEADER_BYTES, bufferLength - Defines.FRAGMENT_HEADER_BYTES, out packetChannelID, out packetSessionId, out packetSequence, out packetAck, out packetAckBits);
						if (packetSequence != sequence)
							throw new FormatException("Bad packet sequence in fragment");
						if (packetChannelID != channelID)
							throw new FormatException("Bad packet channel in fragment");
						if (packetSessionId != sessionId)
							throw new FormatException("Bad packet session id in fragment");

						fragmentBytes = bufferLength - Defines.FRAGMENT_HEADER_BYTES - packetHeaderBytes;
					}

				ack = packetAck;
				ackBits = packetAckBits;

				if (fragmentBytes > fragmentSize)
					throw new FormatException("Fragment bytes remaining > indicated fragment size");

				if (fragmentID != numFragments - 1 && fragmentBytes != fragmentSize)
					throw new FormatException("Fragment size invalid");

					return (int)reader.ReadPosition - offset;
				}
			}

		public static int WriteAckPacket(byte[] packetBuffer, byte channelID, uint sessionId, ushort ack, uint ackBits)
		{
			using (var writer = ByteArrayReaderWriter.Get(packetBuffer))
			{
				byte prefixByte = 0x80; // top bit set, indicates ack packet

				if ((ackBits & 0x000000FF) != 0x000000FF)
					prefixByte |= (1 << 1);

				if ((ackBits & 0x0000FF00) != 0x0000FF00)
					prefixByte |= (1 << 2);

				if ((ackBits & 0x00FF0000) != 0x00FF0000)
					prefixByte |= (1 << 3);

				if ((ackBits & 0xFF000000) != 0xFF000000)
					prefixByte |= (1 << 4);

				writer.Write(prefixByte);
				writer.Write(channelID);
				writer.Write(sessionId);

				writer.Write(ack);

				if ((ackBits & 0x000000FF) != 0x000000FF)
					writer.Write((byte)((ackBits & 0x000000FF)));

				if ((ackBits & 0x0000FF00) != 0x0000FF00)
					writer.Write((byte)((ackBits & 0x0000FF00) >> 8));

				if ((ackBits & 0x00FF0000) != 0x00FF0000)
					writer.Write((byte)((ackBits & 0x00FF0000) >> 16));

				if ((ackBits & 0xFF000000) != 0xFF000000)
					writer.Write((byte)((ackBits & 0xFF000000) >> 24));

				return (int)writer.WritePosition;
			}
		}

		public static int WritePacketHeader(byte[] packetBuffer, byte channelID, uint sessionId, ushort sequence, ushort ack, uint ackBits)
		{
			using (var writer = ByteArrayReaderWriter.Get(packetBuffer))
			{
                byte prefixByte = 0;

                if ((ackBits & 0x000000FF) != 0x000000FF)
                    prefixByte |= (1 << 1);

                if ((ackBits & 0x0000FF00) != 0x0000FF00)
                    prefixByte |= (1 << 2);

                if ((ackBits & 0x00FF0000) != 0x00FF0000)
                    prefixByte |= (1 << 3);

                if ((ackBits & 0xFF000000) != 0xFF000000)
                    prefixByte |= (1 << 4);

                int sequenceDiff = sequence - ack;
                if (sequenceDiff < 0)
                    sequenceDiff += 65536;
                if (sequenceDiff <= 255)
                    prefixByte |= (1 << 5);

                writer.Write(prefixByte);
                writer.Write(channelID);
				writer.Write(sessionId);
                writer.Write(sequence);

                if (sequenceDiff <= 255)
                    writer.Write((byte)sequenceDiff);
                else
                    writer.Write(ack);

                if ((ackBits & 0x000000FF) != 0x000000FF)
                    writer.Write((byte)((ackBits & 0x000000FF)));

                if ((ackBits & 0x0000FF00) != 0x0000FF00)
                    writer.Write((byte)((ackBits & 0x0000FF00) >> 8));

                if ((ackBits & 0x00FF0000) != 0x00FF0000)
                    writer.Write((byte)((ackBits & 0x00FF0000) >> 16));

                if ((ackBits & 0xFF000000) != 0xFF000000)
                    writer.Write((byte)((ackBits & 0xFF000000) >> 24));

                return (int)writer.WritePosition;
			}
		}
	}
}
