namespace NetcodeIO.NET.Internal
{
	/// <summary>
	/// Helper class for protecting against packet replay
	/// </summary>
	internal sealed class NetcodeReplayProtection
	{
		private const int NETCODE_REPLAY_PROTECTION_BUFFER_SIZE = 256;

		public ulong mostRecentSequence;
		public ulong[] receivedPackets;

		public NetcodeReplayProtection()
		{
			mostRecentSequence = 0;
			receivedPackets = new ulong[NETCODE_REPLAY_PROTECTION_BUFFER_SIZE];

			Reset();
		}

		/// <summary>
		/// Reset the packet replay buffer
		/// </summary>
		public void Reset()
		{
			mostRecentSequence = 0;
			for (int i = 0; i < receivedPackets.Length; i++)
				receivedPackets[i] = ulong.MaxValue;
		}

		/// <summary>
		/// Check if the given packet was already received. If not, store it in the replay buffer.
		///
		/// IMPORTANT SIDE EFFECTS:
		/// - Updates mostRecentSequence when a new highest sequence number is received
		/// - Stores the sequence number in the replay buffer for future duplicate detection
		///
		/// This method is NOT a pure check - it modifies internal state to track received packets.
		/// Calling this method twice with the same sequence will return true the second time.
		/// </summary>
		/// <param name="sequence">The sequence number to check. Must have bit 63 clear (not a special packet).</param>
		/// <returns>
		/// true if the packet was already received (duplicate) or too old (outside sliding window),
		/// false if this is a new packet that should be processed.
		/// </returns>
		/// <remarks>
		/// Sequence number handling:
		/// - Bit 63 set: Returns false immediately (special packets bypass replay protection)
		/// - Too old (sequence + BUFFER_SIZE &lt;= mostRecentSequence): Returns true (reject old packets)
		/// - New highest sequence: Updates mostRecentSequence and stores in buffer
		/// - Within window: Checks buffer slot, updates if newer than stored value
		///
		/// The mostRecentSequence update fixes a critical bug where packets arriving out-of-order
		/// could cause the sliding window to not advance properly, rejecting valid future packets.
		/// </remarks>
		public bool AlreadyReceived(ulong sequence)
		{
			if ((sequence & ((ulong)1 << 63)) != 0)
				return false;

			if (sequence + NETCODE_REPLAY_PROTECTION_BUFFER_SIZE <= mostRecentSequence)
				return true;

			if (sequence > mostRecentSequence)
				mostRecentSequence = sequence;

			int index = (int)(sequence % NETCODE_REPLAY_PROTECTION_BUFFER_SIZE);

			if (receivedPackets[index] == 0xFFFFFFFFFFFFFFFF)
			{
				receivedPackets[index] = sequence;
				return false;
			}

			if (receivedPackets[index] >= sequence)
				return true;

			receivedPackets[index] = sequence;
			return false;
		}
	}
}
