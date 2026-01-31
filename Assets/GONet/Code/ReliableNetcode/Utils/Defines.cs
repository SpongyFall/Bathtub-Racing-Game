using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReliableNetcode.Utils
{
	internal static class Defines
	{
		// Packet header now includes a 32-bit SessionId to isolate reliable sessions across resets/failovers.
		// Previous: 10 bytes max; New: 14 bytes max (adds 4 bytes session id).
		public const int MAX_PACKET_HEADER_BYTES = 14;

		// Fragment header includes: prefix(1) + channel(1) + sessionId(4) + sequence(2) + fragId(1) + fragCountMinus1(1)
		public const int FRAGMENT_HEADER_BYTES = 10;
	}
}
