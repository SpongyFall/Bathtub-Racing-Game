using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ReliableNetcode.Utils;

namespace ReliableNetcode
{
	/// <summary>
	/// Quality-of-service type for a message
	/// </summary>
	public enum QosType : byte
	{
		/// <summary>
		/// Message is guaranteed to arrive and in order
		/// </summary>
		Reliable = 0,

		/// <summary>
		/// Message is not guaranteed delivery nor order
		/// </summary>
		Unreliable = 1
	}

	/// <summary>
	/// Main class for routing messages through QoS channels
	/// </summary>
	public class ReliableEndpoint
	{
		private readonly object gate = new object();

		/// <summary>
		/// Method which will be called to transmit raw datagrams over the network
		/// </summary>
		public Action<byte[], int> TransmitCallback;

		/// <summary>
		/// Method which will be called when messages are received.
		/// Parameters: (buffer, length, receiveTimestamp).
		/// receiveTimestamp is the transport-level receive time in ticks (0 if unavailable).
		/// CRITICAL for accurate RTT calculations during high-load scenarios.
		/// </summary>
		public Action<byte[], int, long> ReceiveCallback;

		// Index, buffer, bufferLength
		public Action<uint, byte[], int> TransmitExtendedCallback;
		/// <summary>
		/// Extended callback with Index for multi-connection scenarios.
		/// Parameters: (index, buffer, length, receiveTimestamp).
		/// </summary>
		public Action<uint, byte[], int, long> ReceiveExtendedCallback;
		public uint Index = uint.MaxValue;

		/// <summary>
		/// Approximate round-trip-time in milliseconds
		/// </summary>
		public float RTTMilliseconds => _reliableChannel.RTTMilliseconds;

		/// <summary>
		/// Approximate packet loss
		/// </summary>
		public float PacketLoss => _reliableChannel.PacketLoss;

		/// <summary>
		/// Approximate send bandwidth
		/// </summary>
		public float SentBandwidthKBPS => _reliableChannel.SentBandwidthKBPS;

		/// <summary>
		/// Approximate received bandwidth
		/// </summary>
			public float ReceivedBandwidthKBPS => _reliableChannel.ReceivedBandwidthKBPS;

			private MessageChannel[] messageChannels;
			private double time = 0.0;

			// the reliable channel
			private ReliableMessageChannel _reliableChannel;

			/// <summary>
			/// When true, suppresses reliable packet processing (send/receive) for this endpoint while still allowing
			/// unreliable traffic. Used during failover/reconnect coordination to prevent sequence deadlocks when
			/// one side is resetting its reliability state.
			/// </summary>
			public bool SuppressReliableTraffic { get; set; }

			/// <summary>
			/// DEBUG: Connection identifier for distinguishing logs from multiple connections.
			/// Set this to a meaningful value (e.g., "S->C10" or "C10->S") for better log analysis.
			/// </summary>
			public string ConnectionId
			{
				get => _reliableChannel?.ConnectionId ?? "unset";
				set { if (_reliableChannel != null) _reliableChannel.ConnectionId = value; }
			}

			/// <summary>
			/// Session identifier embedded into reliable packet headers.
			/// Used to isolate reliability state across resets/failovers and reject in-flight packets from an old session.
			/// </summary>
			public uint ReliableSessionId
			{
				get
				{
					lock (gate)
					{
						return messageChannels[(int)QosType.Reliable].SessionId;
					}
				}
				set
				{
					lock (gate)
					{
						messageChannels[(int)QosType.Reliable].SessionId = value;
					}
				}
			}

			public ReliableEndpoint(int maxReliableQueueSize = 2000)
			{
				time = DateTime.UtcNow.GetTotalSeconds();

			_reliableChannel = new ReliableMessageChannel(maxReliableQueueSize) { TransmitCallback = this.transmitMessage, ReceiveCallback = this.receiveMessageWithTimestamp };

			messageChannels = new MessageChannel[]
			{
				_reliableChannel,
				new UnreliableMessageChannel() { TransmitCallback = this.transmitMessage, ReceiveCallback = this.receiveMessageWithTimestamp },
			};
		}

		public ReliableEndpoint(uint index, int maxReliableQueueSize = 2000) : this(maxReliableQueueSize)
		{
			Index = index;
		}

		/// <summary>
		/// Reset the endpoint
		/// </summary>
		public void Reset()
		{
			lock (gate)
			{
				for (int i = 0; i < messageChannels.Length; i++)
					messageChannels[i].Reset();
			}
		}

		/// <summary>
		/// Reset only the reliable channel state while preserving unreliable channel sequencing.
		/// This is useful when using the unreliable channel for reset coordination to avoid dropping
		/// those coordination messages due to an abrupt unreliable sequence rewind.
		/// </summary>
		public void ResetReliableChannel()
		{
			lock (gate)
			{
				messageChannels[(int)QosType.Reliable].Reset();
			}
		}

		/// <summary>
		/// PHASE 7 FIX (December 2025): Clear pre-authority state while preserving pending messages.
		///
		/// When a client transitions from pre-authority to post-authority, stale ackBuffer mappings
		/// can cause false ACKs to mark messages as delivered when they were never received by the server.
		/// This method clears only the ACK-related state while preserving pending messages that need
		/// to be retransmitted.
		///
		/// Call this when the client receives its authority ID from the server.
		/// </summary>
		public void ClearPreAuthorityState()
		{
			lock (gate)
			{
				for (int i = 0; i < messageChannels.Length; i++)
					messageChannels[i].ClearPreAuthorityState();
			}
		}

		/// <summary>
		/// Update the endpoint with the current time
		/// </summary>
		public void Update()
		{
			Update(DateTime.UtcNow.GetTotalSeconds());
		}

		/// <summary>
		/// Manually step the endpoint forward by increment in seconds
		/// </summary>
		public void UpdateFastForward(double increment)
		{
			this.time += increment;
			Update(this.time);
		}

			/// <summary>
			/// Update the endpoint with a specific time value
			/// </summary>
			public void Update(double time)
			{
				lock (gate)
				{
					this.time = time;

					for (int i = 0; i < messageChannels.Length; i++)
					{
						if (SuppressReliableTraffic && i == (int)QosType.Reliable)
						{
							continue;
						}

						messageChannels[i].Update(this.time);
					}
				}
			}

			public void ProcessSendBuffer_IfAppropriate()
			{
				lock (gate)
				{
					for (int i = 0; i < messageChannels.Length; i++)
					{
						if (SuppressReliableTraffic && i == (int)QosType.Reliable)
						{
							continue;
						}

						messageChannels[i].ProcessSendBuffer_IfAppropriate();
					}
				}
			}

		/// <summary>
		/// Call this when a datagram has been received over the network.
		/// </summary>
		/// <param name="buffer">Packet data</param>
		/// <param name="bufferLength">Packet length</param>
		/// <param name="receiveTimestamp">Transport-level receive timestamp in ticks (0 if unavailable).
		/// CRITICAL for accurate RTT calculations during high-load scenarios where processing
		/// delays can be 50-500ms between transport receive and message processing.</param>
		public void ReceivePacket(byte[] buffer, int bufferLength, long receiveTimestamp = 0)
		{
			//GONet.GONetLog.Debug("received length: " + bufferLength);

			lock (gate)
			{
				if (buffer == null)
				{
					GONet.GONetLog.Warning("[ReliableEndpoint] Received null packet buffer, ignoring.");
					return;
				}

				if (bufferLength <= 0)
				{
					GONet.GONetLog.Warning($"[ReliableEndpoint] Received non-positive packet length ({bufferLength}), ignoring.");
					return;
				}

				if (bufferLength > buffer.Length)
				{
					GONet.GONetLog.Warning($"[ReliableEndpoint] Received packet length {bufferLength} exceeds buffer size {buffer.Length}, ignoring.");
					return;
				}

				// CRITICAL: Validate packet format before processing
				if (bufferLength < 8)
				{
					GONet.GONetLog.Warning($"[ReliableEndpoint] Received packet too small ({bufferLength} bytes), ignoring. Minimum is 8 bytes.");
					return;
				}

				int channel = buffer[1];

				// CRITICAL: Validate channel index to prevent IndexOutOfRangeException
				if (channel < 0 || channel >= messageChannels.Length)
				{
					GONet.GONetLog.Warning($"[ReliableEndpoint] Received packet with invalid channel {channel} (valid range: 0-{messageChannels.Length - 1}), ignoring. Packet may not be in ReliableNetcode format. First 10 bytes: {BitConverter.ToString(buffer, 0, Math.Min(10, bufferLength))}");
					return;
				}

				// FAILOVER/RESET COORDINATION: Drop reliable packets while suppressed to prevent buffering a different reliability session.
				if (SuppressReliableTraffic && channel == (int)QosType.Reliable)
				{
					return;
				}

				try
				{
					// TIMESTAMP FIX: Pass the transport-level timestamp through the reliability layer.
					// This ensures time sync gets accurate receive times even for out-of-order packets.
					messageChannels[channel].ReceivePacket(buffer, bufferLength, receiveTimestamp);
				}
				catch (Exception ex)
				{
					GONet.GONetLog.Error($"[ReliableEndpoint] Dropped malformed packet on channel {channel} (bytes={bufferLength}). {ex}");
				}
			}
			}

			/// <summary>
			/// Send a message with the given QoS level
			/// </summary>
			public void SendMessage(byte[] buffer, int bufferLength, QosType qos)
			{
				lock (gate)
				{
					// NOTE: When SuppressReliableTraffic is true, reliable messages are still queued locally,
					// but transmission is halted by skipping reliable Update()/ProcessSendBuffer_IfAppropriate().
					messageChannels[(int)qos].SendMessage(buffer, bufferLength);
				}
			}

        /// <summary>
        /// SLOT RESERVATION (December 2025): Send a message with specified QoS and priority.
        /// System priority messages can bypass Gameplay slot limits in the reliable channel.
        /// </summary>
        /// <param name="buffer">Message data</param>
        /// <param name="bufferLength">Message length</param>
        /// <param name="qos">Quality of service (Reliable or Unreliable)</param>
        /// <param name="priority">Message priority (System bypasses slot limits, Gameplay is limited)</param>
        public void SendMessage(byte[] buffer, int bufferLength, QosType qos, MessagePriority priority)
        {
            lock (gate)
            {
                messageChannels[(int)qos].SendMessage(buffer, bufferLength, priority);
            }
        }

		/// <summary>
		/// Internal callback when a message is ready for delivery.
		/// Called by MessageChannel when messages are processed (possibly after buffering for ordering).
		/// </summary>
		/// <param name="buffer">Message data</param>
		/// <param name="length">Message length</param>
		/// <param name="receiveTimestamp">Transport-level receive timestamp in ticks (0 if unavailable)</param>
		protected void receiveMessageWithTimestamp(byte[] buffer, int length, long receiveTimestamp)
		{
			if (ReceiveCallback != null)
				ReceiveCallback(buffer, length, receiveTimestamp);

			if (ReceiveExtendedCallback != null)
				ReceiveExtendedCallback(Index, buffer, length, receiveTimestamp);
		}

		protected void transmitMessage(byte[] buffer, int length)
		{
			if (TransmitCallback != null)
				TransmitCallback(buffer, length);

			if (TransmitExtendedCallback != null)
				TransmitExtendedCallback(Index, buffer, length);
		}

        public string GetUsageStatistics()
        {
            return _reliableChannel.GetUsageStatistics(); // TODO other options...make API clear
        }
	}
}
