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

using System.Collections.Generic;
using GONetChannelId = System.Byte;

namespace GONet
{
    /// <summary>
    /// Tracks initialization message delivery to detect transport-level failures.
    ///
    /// PURPOSE: Some transports (e.g., Steamworks) report connection as "Connected" before send buffers are ready,
    /// causing reliable messages sent immediately after connection to be silently dropped.
    /// This tracking system detects when messages are lost despite reliable delivery flags.
    ///
    /// ARCHITECTURE:
    /// - Server tracks expected message count and channels sent to each client
    /// - Client sends acknowledgment message with received count and channels
    /// - Server validates match and logs CRITICAL warnings on mismatch
    /// - Retry mechanism (optional) resends missing messages
    ///
    /// See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md for complete design
    /// </summary>
    internal class GONetInitMessageTracker
    {
        /// <summary>
        /// Authority ID of the client being tracked
        /// </summary>
        public ushort AuthorityId { get; private set; }

        /// <summary>
        /// How many initialization messages the server sent to this client
        /// </summary>
        public int ExpectedMessageCount { get; private set; }

        /// <summary>
        /// Which channels were used for init messages (for retry purposes)
        /// </summary>
        public List<GONetChannelId> SentChannels { get; private set; }

        /// <summary>
        /// When the first init message was sent (for timeout detection)
        /// </summary>
        public long SentTimestampTicks { get; private set; }

        /// <summary>
        /// Whether client has acknowledged receipt of init messages
        /// </summary>
        public bool Acknowledged { get; private set; }

        /// <summary>
        /// How many messages client reported receiving (from ACK message)
        /// </summary>
        public int ClientReportedCount { get; private set; }

        /// <summary>
        /// Which channels client reported receiving (from ACK message)
        /// </summary>
        public List<GONetChannelId> ClientReportedChannels { get; private set; }

        /// <summary>
        /// Number of retry attempts made
        /// </summary>
        public int RetryCount { get; private set; }

        /// <summary>
        /// Timestamp of last retry attempt (for retry throttling)
        /// </summary>
        public long LastRetryTimestampTicks { get; private set; }

        /// <summary>
        /// Timestamp of first delivery failure (for cascading failure detection)
        /// </summary>
        public long FirstFailureTimestampTicks { get; private set; }

        /// <summary>
        /// Whether this client has experienced init message delivery failure
        /// </summary>
        public bool HasExperiencedFailure => !Acknowledged || ClientReportedCount != ExpectedMessageCount;

        /// <summary>
        /// Cached byte arrays for each channel (for retry purposes)
        /// Key: channel ID, Value: (byte array, length)
        /// </summary>
        private Dictionary<GONetChannelId, (byte[] data, int length)> cachedMessages;

        public GONetInitMessageTracker(ushort authorityId)
        {
            AuthorityId = authorityId;
            ExpectedMessageCount = 0;
            SentChannels = new List<GONetChannelId>();
            SentTimestampTicks = 0;
            Acknowledged = false;
            ClientReportedCount = 0;
            ClientReportedChannels = new List<GONetChannelId>();
            RetryCount = 0;
            LastRetryTimestampTicks = 0;
            FirstFailureTimestampTicks = 0;
            cachedMessages = new Dictionary<GONetChannelId, (byte[], int)>();
        }

        /// <summary>
        /// Record that an initialization message was sent on a specific channel.
        /// Called by server immediately after sending each init message.
        /// </summary>
        public void RecordMessageSent(GONetChannelId channelId, byte[] messageBytes, int messageLength, long timestampTicks)
        {
            ExpectedMessageCount++;
            SentChannels.Add(channelId);

            // Record first send timestamp
            if (SentTimestampTicks == 0)
            {
                SentTimestampTicks = timestampTicks;
            }

            // Cache message for potential retry
            // NOTE: Make a copy since the original byte array may be returned to pool
            byte[] cachedCopy = new byte[messageLength];
            System.Array.Copy(messageBytes, 0, cachedCopy, 0, messageLength);
            cachedMessages[channelId] = (cachedCopy, messageLength);

#if GONet_INIT_TRACE
            GONetLog.Debug($"[InitMsgTracker] Client {AuthorityId}: Recorded message {ExpectedMessageCount} on channel {channelId} ({messageLength} bytes)");
#endif
        }

        /// <summary>
        /// Process client acknowledgment message.
        /// Called by server when receiving InitAcknowledgment from client.
        /// </summary>
        public void ProcessAcknowledgment(int clientReportedCount, List<GONetChannelId> clientReportedChannels)
        {
            Acknowledged = true;
            ClientReportedCount = clientReportedCount;
            ClientReportedChannels = clientReportedChannels ?? new List<GONetChannelId>();

#if GONet_INIT_TRACE
            GONetLog.Debug($"[InitMsgTracker] Client {AuthorityId}: Received ACK - Reported {clientReportedCount} messages on channels [{string.Join(", ", ClientReportedChannels)}]");
#endif
        }

        /// <summary>
        /// Validate that client received all expected messages.
        /// Returns true if validation passed, false if mismatch detected.
        /// </summary>
        public bool ValidateDelivery()
        {
            if (!Acknowledged)
            {
                GONetLog.Warning($"[InitMsgTracker] Client {AuthorityId}: Validation called but not acknowledged yet");
                return false;
            }

            bool isValid = ClientReportedCount == ExpectedMessageCount;

            if (!isValid)
            {
                // Record first failure timestamp for cascading failure detection
                if (FirstFailureTimestampTicks == 0)
                {
                    FirstFailureTimestampTicks = System.DateTime.UtcNow.Ticks;
                }

                // Find missing channels
                List<GONetChannelId> missingChannels = new List<GONetChannelId>();
                foreach (GONetChannelId sentChannel in SentChannels)
                {
                    if (!ClientReportedChannels.Contains(sentChannel))
                    {
                        missingChannels.Add(sentChannel);
                    }
                }

                GONetLog.Fatal($"[INIT-MSG-FAILURE] Client {AuthorityId} reported receiving {ClientReportedCount} init messages, but server sent {ExpectedMessageCount}! " +
                    $"Expected channels: [{string.Join(", ", SentChannels)}], Client received: [{string.Join(", ", ClientReportedChannels)}]. " +
                    $"Missing channels: [{string.Join(", ", missingChannels)}]. " +
                    $"**RELIABLE MESSAGE DELIVERY FAILED!** Attempting retry...");
            }
            else
            {
                GONetLog.Info($"[INIT-MSG-SUCCESS] Client {AuthorityId} acknowledged receiving all {ExpectedMessageCount} init messages. Delivery validation PASSED.");
            }

            return isValid;
        }

        /// <summary>
        /// Get list of channels that client failed to receive.
        /// </summary>
        public List<GONetChannelId> GetMissingChannels()
        {
            List<GONetChannelId> missingChannels = new List<GONetChannelId>();
            foreach (GONetChannelId sentChannel in SentChannels)
            {
                if (!ClientReportedChannels.Contains(sentChannel))
                {
                    missingChannels.Add(sentChannel);
                }
            }
            return missingChannels;
        }

        /// <summary>
        /// Get cached message data for retry purposes.
        /// Returns null if channel not found in cache.
        /// </summary>
        public (byte[] data, int length)? GetCachedMessage(GONetChannelId channelId)
        {
            if (cachedMessages.TryGetValue(channelId, out var cached))
            {
                return cached;
            }
            return null;
        }

        /// <summary>
        /// Record retry attempt.
        /// </summary>
        public void RecordRetryAttempt(long timestampTicks)
        {
            RetryCount++;
            LastRetryTimestampTicks = timestampTicks;
            GONetLog.Warning($"[InitMsgTracker] Client {AuthorityId}: Retry attempt #{RetryCount} at {timestampTicks}");
        }

        /// <summary>
        /// Clean up cached message data (call when tracking is complete).
        /// </summary>
        public void Dispose()
        {
            cachedMessages.Clear();
#if GONet_INIT_TRACE
            GONetLog.Debug($"[InitMsgTracker] Client {AuthorityId}: Disposed tracker (sent: {ExpectedMessageCount}, received: {ClientReportedCount}, retries: {RetryCount})");
#endif
        }
    }
}
