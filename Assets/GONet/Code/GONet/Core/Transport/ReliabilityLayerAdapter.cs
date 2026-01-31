/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original sources in binary form only (compiled code)
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified sources in binary form only (compiled code)
 */

using ReliableNetcode;

namespace GONet.Transport
{
    /// <summary>
    /// Wraps <see cref="IGONetTransport"/> with ReliableNetcode reliability layer.
    /// Used when transport doesn't have built-in reliability (e.g., raw UDP transports without <see cref="GONetTransportCapabilities.Reliability"/> flag).
    ///
    /// <para>
    /// This adapter bridges the gap between transports that only provide unreliable delivery (UDP-like)
    /// and GONet's need for reliable message delivery. It wraps the transport with ReliableNetcode's
    /// reliability protocol (packet sequencing, ACKs, retransmission, congestion control).
    /// </para>
    ///
    /// <para>
    /// WHEN TO USE:
    /// - Transport is raw UDP (NetcodeIOTransport without built-in reliability)
    /// - Custom transport that doesn't handle retransmission
    /// </para>
    ///
    /// <para>
    /// WHEN NOT TO USE:
    /// - Transport has <see cref="GONetTransportCapabilities.Reliability"/> flag (Steam P2P, TCP, EOS)
    /// - Transport already provides ordered, reliable delivery
    /// </para>
    /// </summary>
    internal class ReliabilityLayerAdapter : ReliableEndpoint
    {
        private readonly IGONetTransport transport;
        private readonly IGONetTransportConnection connection;

        /// <summary>
        /// Create reliability layer adapter wrapping a transport.
        /// </summary>
        /// <param name="transport">Underlying transport (must NOT have Reliability capability)</param>
        /// <param name="connection">Specific connection (server-side: client connection, client-side: null)</param>
        /// <param name="maxQueueSize">Maximum reliable message queue size (see <see cref="GONetTransportConfig.MaxReliableQueueSize"/>)</param>
        public ReliabilityLayerAdapter(IGONetTransport transport, IGONetTransportConnection connection, int maxQueueSize)
            : base(maxQueueSize)
        {
            this.transport = transport;
            this.connection = connection;

            // ReliableEndpoint → Transport: Set up callback for when ReliableEndpoint wants to send packet
            this.TransmitCallback = OnReliableEndpointTransmit;

            // Transport → ReliableEndpoint: Subscribe to incoming messages from transport
            // TIMESTAMP FIX: Subscribe to timestamp event first (fires before OnMessageReceived)
            transport.OnMessageReceivedWithTimestamp += OnTransportMessageReceivedWithTimestamp;
            transport.OnMessageReceived += OnTransportMessageReceived;
        }

        /// <summary>
        /// Callback when ReliableEndpoint wants to send a packet.
        /// Routes packet through transport layer.
        /// </summary>
        private void OnReliableEndpointTransmit(byte[] data, int length)
        {
            // ReliableNetcode has already added reliability headers (sequence, ACKs, etc.)
            // Just send through transport as-is

            // NOTE: ReliableNetcode manages QoS internally (reliable vs unreliable channels)
            // We always use Reliable here since ReliableEndpoint handles channel distinction
            GONetTransportQoS qos = GONetTransportQoS.Reliable;

            transport.Send(data, length, qos, connection);
        }

        /// <summary>
        /// Pending transport-level receive timestamp for the current receive operation.
        /// Set by OnTransportMessageReceivedWithTimestamp before OnTransportMessageReceived fires.
        /// </summary>
        private long _pendingTransportReceiveTicks = 0;

        /// <summary>
        /// Callback when transport receives a message WITH accurate transport-level timestamp.
        /// Called before OnTransportMessageReceived; stores timestamp for use in that handler.
        /// </summary>
        private void OnTransportMessageReceivedWithTimestamp(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection source, byte channel, long transportReceiveTicks)
        {
            // Store the transport-level timestamp for the upcoming OnTransportMessageReceived call
            _pendingTransportReceiveTicks = transportReceiveTicks;
        }

        /// <summary>
        /// Callback when transport receives a message.
        /// Feeds packet to ReliableEndpoint for reliability processing (ACK handling, sequencing, etc.).
        /// </summary>
        private void OnTransportMessageReceived(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection source, byte channel)
        {
            // Filter: Only process messages for OUR specific connection (server-side)
            // Client-side: connection is null OR source is null (NetcodeIO client receives), accept all messages
            // FIX (December 2025): NetcodeIOTransport passes source=null for client-side receives, so we must
            // check source != null before comparing. Otherwise ALL client messages are incorrectly filtered.
            if (connection != null && source != null && source != connection)
                return;

            // NOTE: ReliableNetcode manages channels internally (2 channels: reliable + unreliable)
            // The 'channel' parameter here is from IGONetTransport's channel multiplexing
            // ReliableEndpoint doesn't need to know about external channels - it just processes packets
            // Channel routing is handled at GONetConnection layer

            // TIMESTAMP FIX: Pass the transport-level timestamp through the reliability layer.
            // This ensures time sync gets accurate receive times even for out-of-order packets.
            long timestamp = _pendingTransportReceiveTicks;
            _pendingTransportReceiveTicks = 0; // Clear for next message

            // Feed packet to ReliableEndpoint for reliability processing
            // ReliableEndpoint will:
            // 1. Process ACKs (mark sent packets as received)
            // 2. Check sequence numbers (detect duplicates, out-of-order)
            // 3. Queue for retransmission if needed
            // 4. Invoke ReceiveCallback when message is ready for application layer
            base.ReceivePacket(data, length, timestamp);
        }
    }
}
