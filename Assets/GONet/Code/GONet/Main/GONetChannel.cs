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
using ReliableNetcode;
using GONetChannelId = System.Byte;

namespace GONet
{
    public class GONetChannel
    {
        private static readonly Dictionary<GONetChannelId, GONetChannel> byIdMap = new Dictionary<GONetChannelId, GONetChannel>(byte.MaxValue);
        private static GONetChannelId nextAvailableId = 0;

        public static readonly GONetChannel TimeSync_Unreliable;
        public static readonly GONetChannel AutoMagicalSync_Reliable;
        public static readonly GONetChannel AutoMagicalSync_Unreliable;
        public static readonly GONetChannel AutoMagicalSync_ValuesNowAtRest_Reliable;
        public static readonly GONetChannel CustomSerialization_Reliable;
        public static readonly GONetChannel CustomSerialization_Unreliable;
        public static readonly GONetChannel EventSingles_Reliable;
        /// <summary>
        /// <para>Using this probably only makes sense when the event implements <see cref="ITransientEvent"/>.</para>
        /// <para>If it implements <see cref="IPersistentEvent"/>, then it likely makes more sense to use <see cref="EventSingles_Reliable"/> instead.</para>
        /// </summary>
        public static readonly GONetChannel EventSingles_Unreliable;

        /// <summary>
        /// GONet internal use only!
        /// </summary>
        public static readonly GONetChannel ClientInitialization_EventSingles_Reliable;
        /// <summary>
        /// GONet internal use only!
        /// </summary>
        public static readonly GONetChannel ClientInitialization_CustomSerialization_Reliable;

        #region Distributed Host Authority Channels

        /// <summary>
        /// Unreliable channel for distributed host metrics gossip.
        /// Used for periodic node metrics broadcasts (RTT, CPU, stability scores).
        /// Low priority - dropped packets are acceptable as metrics are resent frequently.
        /// Target: 0.5 Hz update rate, &lt;5 KB/s per node at 32 players.
        /// </summary>
        public static readonly GONetChannel DistributedHost_Unreliable;

        /// <summary>
        /// Reliable channel for distributed host authority messages.
        /// Used for:
        /// - Election announcements (host designation, vice host selection)
        /// - Handoff protocol messages (Prepare, Snapshot, Commit, Complete)
        /// - Failover coordination (promotion announcements)
        /// - Epoch increments
        /// </summary>
        public static readonly GONetChannel DistributedHost_Reliable;

        #endregion

        public GONetChannelId Id { get; private set; }

        public QosType QualityOfService { get; private set; }

        static GONetChannel()
        {
            TimeSync_Unreliable = new GONetChannel(QosType.Unreliable);
            AutoMagicalSync_Reliable = new GONetChannel(QosType.Reliable);
            AutoMagicalSync_Unreliable = new GONetChannel(QosType.Unreliable);
            AutoMagicalSync_ValuesNowAtRest_Reliable = new GONetChannel(QosType.Reliable);
            CustomSerialization_Reliable = new GONetChannel(QosType.Reliable);
            CustomSerialization_Unreliable = new GONetChannel(QosType.Unreliable);
            EventSingles_Reliable = new GONetChannel(QosType.Reliable);
            EventSingles_Unreliable = new GONetChannel(QosType.Unreliable);

            ClientInitialization_EventSingles_Reliable = new GONetChannel(QosType.Reliable);
            ClientInitialization_CustomSerialization_Reliable = new GONetChannel(QosType.Reliable);

            // Distributed Host Authority channels
            DistributedHost_Unreliable = new GONetChannel(QosType.Unreliable);
            DistributedHost_Reliable = new GONetChannel(QosType.Reliable);
        }

        internal GONetChannel(QosType qualityOfService)
        {
            Id = nextAvailableId++;
            QualityOfService = qualityOfService;

            byIdMap[Id] = this;
        }

        public static GONetChannel ById(GONetChannelId id)
        {
            return byIdMap[id];
        }

        public static implicit operator GONetChannelId(GONetChannel channel)
        {
            return channel.Id;
        }

        public static bool IsGONetCoreChannel(GONetChannelId channelId)
        {
            // NOTE: DistributedHost channels are NOT core channels - they use OnCustomChannelPayloadReceived
            return
                channelId == TimeSync_Unreliable ||
                channelId == AutoMagicalSync_Reliable ||
                channelId == AutoMagicalSync_Unreliable ||
                channelId == AutoMagicalSync_ValuesNowAtRest_Reliable ||
                channelId == CustomSerialization_Reliable ||
                channelId == CustomSerialization_Unreliable ||
                channelId == EventSingles_Reliable ||
                channelId == EventSingles_Unreliable ||
                channelId == ClientInitialization_EventSingles_Reliable ||
                channelId == ClientInitialization_CustomSerialization_Reliable;
        }

        /// <summary>
        /// SLOT RESERVATION (December 2025): Determines message priority based on channel type.
        ///
        /// <para><b>System Priority Channels (bypass Gameplay slot limits):</b></para>
        /// <list type="bullet">
        ///   <item><description>TimeSync_Unreliable - Critical for clock synchronization</description></item>
        ///   <item><description>ClientInitialization_* - Scene loads, init data for late joiners</description></item>
        ///   <item><description>DistributedHost_Reliable - Authority/failover coordination</description></item>
        ///   <item><description>EventSingles_Reliable - Scene load events, critical state transitions</description></item>
        /// </list>
        ///
        /// <para><b>Gameplay Priority Channels (limited to GAMEPLAY_SLOT_LIMIT):</b></para>
        /// <list type="bullet">
        ///   <item><description>AutoMagicalSync_* - Gameplay object synchronization</description></item>
        ///   <item><description>CustomSerialization_* - User gameplay data</description></item>
        ///   <item><description>EventSingles_Unreliable - Transient gameplay events</description></item>
        ///   <item><description>DistributedHost_Unreliable - Metrics (not critical)</description></item>
        /// </list>
        /// </summary>
        public static ReliableNetcode.MessagePriority GetMessagePriority(GONetChannelId channelId)
        {
            // System priority: Critical infrastructure that must not be blocked by gameplay traffic
            if (channelId == TimeSync_Unreliable ||
                channelId == ClientInitialization_EventSingles_Reliable ||
                channelId == ClientInitialization_CustomSerialization_Reliable ||
                channelId == DistributedHost_Reliable ||
                channelId == EventSingles_Reliable)  // Scene load events go here
            {
                return ReliableNetcode.MessagePriority.System;
            }

            // Gameplay priority: Everything else
            return ReliableNetcode.MessagePriority.Gameplay;
        }

        /// <summary>
        /// Returns true if this channel should be tracked for init message validation.
        /// Only RELIABLE init channels are tracked - unreliable drops are expected.
        /// Used by both client (to count received messages) and server (to validate delivery).
        /// </summary>
        public static bool IsChannelTrackedForInitValidation(GONetChannelId channelId)
        {
            return
                channelId == ClientInitialization_EventSingles_Reliable ||
                channelId == ClientInitialization_CustomSerialization_Reliable;
            // NOTE: TimeSync_Unreliable intentionally excluded - unreliable drops are expected!
        }
    }
}
