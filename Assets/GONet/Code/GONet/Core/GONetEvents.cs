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

using GONet.Utils;
using MemoryPack;
using NetcodeIO.NET;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace GONet
{
    #region base stuffs

    /// <summary>
    /// This alone does not mean much.  Implement either <see cref="ITransientEvent"/> or <see cref="IPersistentEvent"/>.
    /// </summary>
    public partial interface IGONetEvent
    {
        long OccurredAtElapsedTicks { get; }
    }

    /// <summary>
    /// Implement this to indicate the information herein is only relevant while it is happening and while subscribers are notified and NOT to be passed along to newly connecting clients and can safely be skipped over during replay skip-ahead or fast-forward.
    ///
    /// POOLING COMPATIBILITY: Events implementing ITransientEvent are typically compatible with object pooling via ISelfReturnEvent,
    /// as they are processed immediately and not stored for future use. This allows for efficient memory reuse patterns.
    /// </summary>
    public partial interface ITransientEvent : IGONetEvent
    {
        /// <summary>
        /// GONet sends all events to all other machines in the simulation/game by default.
        /// This need to return true if this event (type) is supposed to only be sent to the
        /// singular/first recipient (and not subsequently relayed to the others connected to it) 
        /// when one of the following typical APIs is called:
        /// -<see cref="GONetMain.SendBytesToRemoteConnection(GONetConnection, byte[], int, byte)"/>
        /// -<see cref="GONetConnection.SendMessageOverChannel(byte[], int, byte)"/>
        /// </summary>
        [MemoryPackIgnore]
        bool IsSingularRecipientOnly { get => false; } // TODO consider moving this up to IGONetEvent if applicable to IPersistentEvent as well
    }

    /// <summary>
    /// Implement this to indicate the information herein should be stored and sent to newly connecting clients.
    /// These events are kept in GONet's persistentEventsThisSession collection for late-joining client delivery.
    ///
    /// ⚠️  CRITICAL ARCHITECTURAL CONSTRAINT: NO OBJECT POOLING ALLOWED
    ///
    /// Classes implementing IPersistentEvent MUST NOT implement ISelfReturnEvent or use object pooling.
    ///
    /// WHY NO POOLING:
    /// GONet stores persistent events BY REFERENCE in persistentEventsThisSession (GONet.cs:681) for
    /// the entire session duration (minutes to hours). When late-joining clients connect, these exact
    /// stored references are serialized and transmitted (see Server_SendClientPersistentEventsSinceStart
    /// in GONet.cs:4355).
    ///
    /// WHAT HAPPENS IF YOU ADD POOLING (DON'T!):
    ///   1. Event created: new PersistentEvent { Data = "ImportantState" }
    ///   2. Stored by reference in persistentEventsThisSession
    ///   3. Event.Return() called → data cleared: { Data = null }
    ///   4. Pool reuses object for different event → overwrites: { Data = "DifferentState" }
    ///   5. Late-joiner connects 30 minutes later
    ///   6. Server serializes persistentEventsThisSession (includes corrupted reference!)
    ///   7. Late-joiner receives WRONG/CORRUPTED data
    ///   8. RESULT: Invisible bugs, state desync, crashes, game-breaking issues
    ///
    /// MEMORY COST vs SAFETY:
    /// - Cost: ~48 bytes per event × 10-200 events = 1-10 KB per session
    /// - Benefit: 100% guarantee of data integrity for late-joining clients
    /// - Trade-off: Trivial memory overhead for critical correctness
    ///
    /// USAGE FREQUENCY:
    /// - Persistent events: 1-10 per minute (setup, config, state changes)
    /// - Transient events: 100-1000+ per second (movement, combat, frequent updates)
    /// - Memory allocation cost for persistent events is negligible compared to transient event pooling savings
    ///
    /// EXAMPLES OF CORRECT IMPLEMENTATION (no ISelfReturnEvent):
    /// - PersistentRpcEvent (see GONetRpcs.cs:912 for extensive rationale)
    /// - PersistentRoutedRpcEvent (TargetRpc variant)
    /// - InstantiateGONetParticipantEvent (spawn events)
    /// - DespawnGONetParticipantEvent (despawn with cancellation logic)
    /// - SceneLoadEvent (networked scene management)
    ///
    /// FOR END USERS:
    /// When creating custom persistent events, simply create with 'new' operator (never pool).
    /// The slight memory cost ensures your game state remains correct for all players.
    ///
    /// FOR FRAMEWORK DEVELOPERS:
    /// This design constraint is architecturally required by GONet's persistence mechanism.
    /// Do NOT attempt to "optimize" by adding pooling - the memory savings (~10 KB) are
    /// trivial compared to the catastrophic risk of data corruption. This pattern has been
    /// validated through production use and is fundamental to GONet's architecture.
    ///
    /// See also:
    /// - GONet.cs:681 - persistentEventsThisSession storage (events stored by reference)
    /// - GONet.cs:1595 - OnPersistentEvent_KeepTrack() (where events are added to storage)
    /// - GONet.cs:4355 - Server_SendClientPersistentEventsSinceStart() (transmission to late-joiners)
    /// - GONetRpcs.cs:912 - PersistentRpcEvent class (detailed pooling rationale with examples)
    /// </summary>
    public partial interface IPersistentEvent : IGONetEvent { }

    /// <summary>
    /// Tack this on to any event type to ensure calls to <see cref="GONetEventBus.Publish{T}(T, uint?)"/> only publish locally (i.e., not sent across the network to anyone else)
    /// </summary>
    public interface ILocalOnlyPublish { }

    /// <summary>
    /// This is something that would only apply to event class that implement <see cref="IPersistentEvent"/> that get queued up on server and sent to newly connecting clients.
    /// Instances that implement this tell GONet to look for instances of the other events of type <see cref="OtherEventTypesCancelledOut"/> and see if they cancel one another out
    /// so these messages can be removed from consideration in pairs as to not send these events anywhere.
    /// Example: <see cref="InstantiateGONetParticipantEvent"/> is cancelled out by <see cref="DespawnGONetParticipantEvent"/>.
    /// </summary>
    public interface ICancelOutOtherEvents
    {
        /// <summary>
        /// At time of writing, this should only be types that implement <see cref="IPersistentEvent"/>.
        /// </summary>
        Type[] OtherEventTypesCancelledOut { get; }

        /// <summary>
        /// This will only get called when <paramref name="otherEvent"/> is of the type <see cref="OtherEventTypesCancelledOut"/>.
        /// </summary>
        bool DoesCancelOutOtherEvent(IGONetEvent otherEvent);
    }

    #endregion

    [MemoryPackable]
    public partial class ServerSaysClientInitializationCompletion : ITransientEvent
    {
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Server marker indicating all init-tracked custom serialization messages have been sent.
    /// Client uses this to delay init acknowledgment until init message stream is complete.
    /// </summary>
    [MemoryPackable]
    public partial class ServerSaysInitMessageTrackingComplete : ITransientEvent
    {
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Client acknowledgment message sent after receiving all initialization messages.
    /// Used to detect Steamworks (or other transport) reliable message delivery failures.
    /// See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md
    /// </summary>
    [MemoryPackable]
    public partial class ClientInitializationAcknowledgment : ITransientEvent
    {
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// How many initialization messages the client received
        /// </summary>
        [MemoryPackOrder(0)]
        public int ReceivedMessageCount { get; set; }

        /// <summary>
        /// Which channels the client received init messages on (for mismatch diagnosis)
        /// </summary>
        [MemoryPackOrder(1)]
        public List<byte> ReceivedChannels { get; set; }
    }

    [MemoryPackable]
    public partial class AutoMagicalSync_AllCurrentValues_Message : ITransientEvent
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();
    }

    [MemoryPackable]
    public partial class AutoMagicalSync_ValueChanges_Message : ITransientEvent
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();
    }

    [MemoryPackable]
    public partial class AutoMagicalSync_ValuesNowAtRest_Message : ITransientEvent
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();
    }

    [MemoryPackable]
    public partial class OwnerAuthorityIdAssignmentEvent : IPersistentEvent
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();
    }

    [MemoryPackable]
    public partial class ClientRemotelyControlledGONetIdServerBatchAssignmentEvent : ITransientEvent
    {
        [MemoryPackIgnore]
        public bool IsSingularRecipientOnly => true;

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();

        public uint GONetIdRawBatchStart { get; set; }

        /// <summary>
        /// Server's lastAssignedGONetIdRaw after allocating this batch.
        /// Client should use this to set its own lastAssignedGONetIdRaw to prevent
        /// ID collisions between client-owned and future batch IDs.
        /// </summary>
        public uint ServerLastAssignedGONetIdRaw { get; set; }
    }

    [MemoryPackable]
    public partial class ClientRemotelyControlledGONetIdServerBatchRequestEvent : ITransientEvent
    {
        [MemoryPackIgnore]
        public bool IsSingularRecipientOnly => true;

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();
    }

    /// <summary>
    /// Fired locally-only when any <see cref="GONetParticipant"/> finished having its OnEnable() method called.
    /// IMPORTANT: This is not the proper time to indicate it is ready for use by other game logic, for that use <see cref="GONetParticipantStartedEvent"/> instead to be certain.
    /// </summary>
    [MemoryPackable]
    public partial class GONetParticipantEnabledEvent : ITransientEvent, ILocalOnlyPublish, IHaveRelatedGONetId
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();

        public uint GONetId { get; set; }

        public GONetParticipantEnabledEvent(uint gonetId)
        {
            GONetId = gonetId;
        }
    }

    /// <summary>
    /// Fired locally-only when any <see cref="GONetParticipant"/> finished having its Start() method called and it is ready to be used by other game logic.
    /// IMPORTANT: When this is fired/published, this is the first time it is certain that the <see cref="GONetParticipant.GONetId"/> value is fully assigned!
    /// </summary>
    [MemoryPackable]
    public partial class GONetParticipantStartedEvent : ITransientEvent, ILocalOnlyPublish, IHaveRelatedGONetId
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();

        public uint GONetId { get; set; }

        [MemoryPackConstructor] GONetParticipantStartedEvent() { }

        public GONetParticipantStartedEvent(GONetParticipant gonetParticipant)
        {
            GONetId = gonetParticipant.GONetId;
        }
    }

    /// <summary>
    /// Fired locally-only when any <see cref="GONetParticipant"/> finished having its OnDisable() method called and will no longer be active in the game.
    /// </summary>
    [MemoryPackable]
    public partial class GONetParticipantDisabledEvent : ITransientEvent, ILocalOnlyPublish, IHaveRelatedGONetId
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();

        public uint GONetId { get; set; }

        [MemoryPackConstructor] public GONetParticipantDisabledEvent() { }

        public GONetParticipantDisabledEvent(GONetParticipant gonetParticipant)
        {
            GONetId = gonetParticipant.GONetId;
        }
    }

    /// <summary>
    /// Fired locally-only when any <see cref="GONetParticipant"/> finished having its related 
    /// <see cref="GONet.Generation.GONetParticipant_AutoMagicalSyncCompanion_Generated.DeserializeInitAll(BitByBitByteArrayBuilder, long)"/> 
    /// method called.
    /// This is useful because individual SyncEvents will NOT be fired in those cases and there may be a need to do something once initial 
    /// values are known (from remote source/authority).
    /// </summary>
    [MemoryPackable]
    public partial class GONetParticipantDeserializeInitAllCompletedEvent : ITransientEvent, ILocalOnlyPublish, IHaveRelatedGONetId
    {
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();

        public uint GONetId { get; set; }

        [MemoryPackConstructor] public GONetParticipantDeserializeInitAllCompletedEvent() { }

        public GONetParticipantDeserializeInitAllCompletedEvent(GONetParticipant gonetParticipant)
        {
            GONetId = gonetParticipant.GONetId;
        }
    }

    [MemoryPackable]
    public partial class RequestMessage : ITransientEvent // TODO probably not always going to be considered transient
    {
        public long OccurredAtElapsedTicks { get; set; }

        public long UID;

        public RequestMessage(long occurredAtElapsedTicks)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;

            UID = GUID.Generate().AsInt64();
        }
    }

    [MemoryPackable]
    public partial class ResponseMessage : ITransientEvent // TODO probably not always going to be considered transient
    {
        public long OccurredAtElapsedTicks { get; set; }

        public long CorrelationRequestUID;

        public ResponseMessage(long occurredAtElapsedTicks, long correlationRequestUID)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            CorrelationRequestUID = correlationRequestUID;
        }
    }

    /// <summary>
    /// BANDWIDTH OPTIMIZATION: Encoding strategy for GameObject instance names.
    /// Reduces typical spawn event size by 10-30 bytes per spawn.
    /// </summary>
    [Flags]
    public enum InstanceNameEncoding : byte
    {
        /// <summary>
        /// Use prefab's default name (no suffix).
        /// Bandwidth: 0 bytes (just 1-byte flag).
        /// Example: "CannonBall" → "CannonBall"
        /// </summary>
        UseDefaultName = 0,

        /// <summary>
        /// Append Unity's standard "(Clone)" suffix to prefab name.
        /// Bandwidth: 0 bytes (just 1-byte flag).
        /// Example: "CannonBall" → "CannonBall(Clone)"
        /// Covers 80% of all spawns!
        /// </summary>
        UseClonePattern = 1,

        /// <summary>
        /// Append numeric suffix like "_1234" to name (encoded as ushort).
        /// Bandwidth: 2 bytes (ushort suffix).
        /// Example: "Player" → "Player_1234"
        /// Can combine with UseClonePattern: "Player(Clone)_1234"
        /// </summary>
        HasNumericSuffix = 2,

        /// <summary>
        /// Full custom name string (backwards compatibility fallback).
        /// Bandwidth: N bytes (full string length).
        /// Example: "MyCustomObjectName_ABC"
        /// Only used for truly custom names that don't fit other patterns.
        /// </summary>
        HasCustomName = 4
    }

    [MemoryPackable]
    public partial class InstantiateGONetParticipantEvent : IPersistentEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// BANDWIDTH OPTIMIZATION: 16-bit index into DesignTimeMetadata.json instead of full string location.
        /// Saves 38-78 bytes per spawn event (2 bytes vs 40-80 bytes).
        /// ushort.MaxValue (65535) indicates invalid/legacy value - use <see cref="DesignTimeLocation"/> fallback.
        /// </summary>
        public ushort DesignTimeLocationIndex;

        /// <summary>
        /// BACKWARDS COMPATIBILITY: Legacy full string location.
        /// Prefer using <see cref="DesignTimeLocationIndex"/> for bandwidth savings.
        /// Use this property for compatibility and debugging.
        /// When reading: If DesignTimeLocationIndex is valid (!= ushort.MaxValue), returns location from index lookup.
        /// When writing: Also updates DesignTimeLocationIndex from location string.
        /// </summary>
        [MemoryPackIgnore]
        public string DesignTimeLocation
        {
            get
            {
                // If index is valid, decode it to location string
                if (DesignTimeLocationIndex != ushort.MaxValue)
                {
                    string location = GONetSpawnSupport_Runtime.GetDesignTimeLocationFromIndex(DesignTimeLocationIndex);
                    if (!string.IsNullOrWhiteSpace(location))
                        return location;
                }

                // Fallback to stored string (backwards compatibility for old events)
                return _legacyDesignTimeLocation;
            }
            set
            {
                // Update both index and legacy string
                _legacyDesignTimeLocation = value;
                DesignTimeLocationIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(value);
            }
        }

        /// <summary>
        /// Internal backing field for backwards compatibility.
        /// NOT SERIALIZED - only used for in-memory compatibility layer.
        /// </summary>
        [MemoryPackIgnore]
        private string _legacyDesignTimeLocation;

        public uint GONetId;

        public ushort OwnerAuthorityId;

        /// <summary>
        /// Persistent ID of the machine that spawned this object.
        /// 0 = scene object (no specific spawner, immune to ProcessSpawnerDeath cleanup).
        /// This value never changes, even during authority transfers.
        /// Used in distributed host failover to determine object fate when spawner dies.
        /// </summary>
        public ulong SpawnerPersistentId;

        public Vector3 Position;

        public Quaternion Rotation;

        /// <summary>
        /// BANDWIDTH OPTIMIZATION: Encoding flags for InstanceName (1 byte vs 10-50 bytes).
        /// Determines how to reconstruct the GameObject name on remote machines.
        /// </summary>
        public InstanceNameEncoding InstanceNameEncodingFlags;

        /// <summary>
        /// BANDWIDTH OPTIMIZATION: Optional numeric suffix for name patterns like "Player_1234".
        /// Only used when HasNumericSuffix flag is set (2 bytes vs 10+ bytes for full string).
        /// </summary>
        public ushort InstanceNameNumericSuffix;

        /// <summary>
        /// BANDWIDTH OPTIMIZATION: Custom instance name (only serialized when HasCustomName flag is set).
        /// Most spawns (80%+) use UseClonePattern flag instead, saving 10-30 bytes per spawn.
        /// </summary>
        public string InstanceNameCustom;

        /// <summary>
        /// BACKWARDS COMPATIBILITY: Legacy full instance name string.
        /// Prefer using InstanceNameEncodingFlags + InstanceNameNumericSuffix for bandwidth savings.
        ///
        /// When reading: Reconstructs name from encoding flags.
        /// When writing: Analyzes name and sets appropriate encoding flags.
        /// </summary>
        [MemoryPackIgnore]
        public string InstanceName
        {
            get
            {
                // Decode based on flags
                if (InstanceNameEncodingFlags == InstanceNameEncoding.UseDefaultName)
                {
                    // Use prefab name from DesignTimeLocation
                    return GetPrefabNameFromDesignTimeLocation(DesignTimeLocation);
                }

                if ((InstanceNameEncodingFlags & InstanceNameEncoding.HasCustomName) != 0)
                {
                    // Full custom name stored (backwards compatibility)
                    return InstanceNameCustom ?? _legacyInstanceName;
                }

                // Start with prefab name
                string baseName = GetPrefabNameFromDesignTimeLocation(DesignTimeLocation);

                if ((InstanceNameEncodingFlags & InstanceNameEncoding.UseClonePattern) != 0)
                {
                    baseName += "(Clone)";
                }

                if ((InstanceNameEncodingFlags & InstanceNameEncoding.HasNumericSuffix) != 0)
                {
                    baseName += "_" + InstanceNameNumericSuffix;
                }

                return baseName;
            }
            set
            {
                _legacyInstanceName = value;

                // Analyze name and encode efficiently
                if (string.IsNullOrWhiteSpace(value))
                {
                    InstanceNameEncodingFlags = InstanceNameEncoding.UseDefaultName;
                    InstanceNameNumericSuffix = 0;
                    return;
                }

                string prefabName = GetPrefabNameFromDesignTimeLocation(DesignTimeLocation);

                // Check for "(Clone)" pattern
                bool hasClonePattern = value.EndsWith("(Clone)");
                string nameWithoutClone = hasClonePattern ? value.Substring(0, value.Length - 7) : value;

                // Check for numeric suffix pattern like "CannonBall_1234"
                int lastUnderscore = nameWithoutClone.LastIndexOf('_');
                if (lastUnderscore > 0 && lastUnderscore < nameWithoutClone.Length - 1)
                {
                    string basePart = nameWithoutClone.Substring(0, lastUnderscore);
                    string suffixPart = nameWithoutClone.Substring(lastUnderscore + 1);

                    if (ushort.TryParse(suffixPart, out ushort numericSuffix))
                    {
                        // Has numeric suffix pattern
                        if (basePart == prefabName)
                        {
                            // Efficient encoding: "PrefabName_1234"
                            InstanceNameEncodingFlags = InstanceNameEncoding.HasNumericSuffix;
                            if (hasClonePattern)
                                InstanceNameEncodingFlags |= InstanceNameEncoding.UseClonePattern;
                            InstanceNameNumericSuffix = numericSuffix;
                            return;
                        }
                    }
                }

                // Check if it's just "PrefabName(Clone)"
                if (hasClonePattern && nameWithoutClone == prefabName)
                {
                    InstanceNameEncodingFlags = InstanceNameEncoding.UseClonePattern;
                    InstanceNameNumericSuffix = 0;
                    return;
                }

                // Check if it's just the prefab name
                if (value == prefabName)
                {
                    InstanceNameEncodingFlags = InstanceNameEncoding.UseDefaultName;
                    InstanceNameNumericSuffix = 0;
                    return;
                }

                // Fall back to custom name (stores full string - backwards compatibility)
                InstanceNameEncodingFlags = InstanceNameEncoding.HasCustomName;
                InstanceNameNumericSuffix = 0;
                InstanceNameCustom = value;
                _legacyInstanceName = value;
            }
        }

        /// <summary>
        /// Internal backing field for custom instance names.
        /// Only used when HasCustomName flag is set.
        /// </summary>
        [MemoryPackIgnore]
        private string _legacyInstanceName;

        /// <summary>
        /// Helper to extract prefab name from DesignTimeLocation.
        /// Example: "addressables://Assets/Prefabs/CannonBall.prefab" → "CannonBall"
        /// </summary>
        private static string GetPrefabNameFromDesignTimeLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
                return string.Empty;

            // Handle different location formats
            // scene://SceneName/ObjectPath → extract last part
            // resources://Assets/Resources/PrefabName.prefab → extract PrefabName
            // addressables://Assets/Path/PrefabName.prefab → extract PrefabName

            int lastSlash = location.LastIndexOf('/');
            if (lastSlash < 0)
                return location; // No slashes, use as-is

            string fileName = location.Substring(lastSlash + 1);

            // Remove .prefab extension if present
            if (fileName.EndsWith(".prefab"))
                fileName = fileName.Substring(0, fileName.Length - 7);

            return fileName;
        }

        public string ParentFullUniquePath;

        public uint GONetIdAtInstantiation;

        public bool ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority;

        /// <summary>
        /// Identifies which scene this GONetParticipant was spawned in.
        /// <para>This is used for scene-based persistent event filtering to ensure late-joining clients
        /// only receive spawns relevant to their currently loaded scenes.</para>
        /// <para>Value is either the scene name from build settings or the addressable path for addressable scenes.</para>
        /// </summary>
        public string SceneIdentifier;

        /// <summary>
        /// Custom initialization data serialized from <see cref="IGONetSyncdBehaviourInitializer.Spawner_SerializeSpawnData"/>.
        /// <para>This data is sent ONCE at spawn time and deserialized on receivers before Awake() completes.</para>
        /// <para>Null if no <see cref="IGONetSyncdBehaviourInitializer"/> components are present on the spawned object.</para>
        /// </summary>
        public byte[] CustomSpawnData;

        internal static InstantiateGONetParticipantEvent Create(GONetParticipant gonetParticipant)
        {
            InstantiateGONetParticipantEvent @event = new InstantiateGONetParticipantEvent();

            @event.InstanceName = gonetParticipant.gameObject.name;

            // CRITICAL: Force metadata lookup to bypass caching check
            // This ensures we get the actual DesignTimeLocation even if metadata caching hasn't completed yet
            // Without force=true, early spawns (before caching completes) would get empty DesignTimeLocation
            @event.DesignTimeLocation = GONetSpawnSupport_Runtime.GetDesignTimeMetadata_Location(gonetParticipant, force: true);

            @event.ParentFullUniquePath = gonetParticipant.transform.parent == null ? string.Empty : HierarchyUtils.GetFullUniquePath(gonetParticipant.transform.parent.gameObject);

            @event.GONetId = gonetParticipant.GONetId;
            @event.GONetIdAtInstantiation = gonetParticipant.GONetIdAtInstantiation;
            @event.OwnerAuthorityId = gonetParticipant.OwnerAuthorityId;
            @event.SpawnerPersistentId = gonetParticipant.SpawnerPersistentId;
            @event.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority = false;

            @event.Position = gonetParticipant.transform.position;
            @event.Rotation = gonetParticipant.transform.rotation;

            // CRITICAL: Objects with GONetSessionContext (GONetGlobal, GONetLocal) persist via DontDestroyOnLoad
            // They must ALWAYS use "DontDestroyOnLoad" as SceneIdentifier, even if currently in a regular scene
            // Otherwise SceneUnloadEvent will incorrectly cancel their spawn events when original scene unloads
            @event.SceneIdentifier = gonetParticipant.GetComponent<GONetSessionContext>() != null
                ? HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE
                : GONetSceneManager.GetSceneIdentifier(gonetParticipant.gameObject);

            @event.OccurredAtElapsedTicks = default;

            // Serialize custom spawn data from IGONetSpawnDataProvider components
            @event.CustomSpawnData = SerializeCustomSpawnData(gonetParticipant);

            return @event;
        }

        internal static InstantiateGONetParticipantEvent Create_WithNonAuthorityInfo(GONetParticipant gonetParticipant, string nonAuthorityAlternate_designTimeLocation)
        {
            InstantiateGONetParticipantEvent @event = new InstantiateGONetParticipantEvent();

            @event.InstanceName = gonetParticipant.gameObject.name;
            @event.DesignTimeLocation = nonAuthorityAlternate_designTimeLocation;

            @event.GONetId = gonetParticipant.GONetId;
            @event.GONetIdAtInstantiation = gonetParticipant.GONetIdAtInstantiation;
            @event.OwnerAuthorityId = gonetParticipant.OwnerAuthorityId;
            @event.SpawnerPersistentId = gonetParticipant.SpawnerPersistentId;
            @event.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority = false;

            @event.Position = gonetParticipant.transform.position;
            @event.Rotation = gonetParticipant.transform.rotation;

            // CRITICAL: Objects with GONetSessionContext (GONetGlobal, GONetLocal) persist via DontDestroyOnLoad
            // They must ALWAYS use "DontDestroyOnLoad" as SceneIdentifier, even if currently in a regular scene
            // Otherwise SceneUnloadEvent will incorrectly cancel their spawn events when original scene unloads
            @event.SceneIdentifier = gonetParticipant.GetComponent<GONetSessionContext>() != null
                ? HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE
                : GONetSceneManager.GetSceneIdentifier(gonetParticipant.gameObject);

            @event.OccurredAtElapsedTicks = default;

            // Serialize custom spawn data from IGONetSpawnDataProvider components
            @event.CustomSpawnData = SerializeCustomSpawnData(gonetParticipant);

            return @event;
        }

        internal static InstantiateGONetParticipantEvent Create_WithRemotelyControlledByInfo(GONetParticipant gonetParticipant)
        {
            InstantiateGONetParticipantEvent @event = new InstantiateGONetParticipantEvent();

            @event.InstanceName = gonetParticipant.gameObject.name;

            // CRITICAL: Force metadata lookup to bypass caching check
            // This ensures we get the actual DesignTimeLocation even if metadata caching hasn't completed yet
            @event.DesignTimeLocation = GONetSpawnSupport_Runtime.GetDesignTimeMetadata_Location(gonetParticipant, force: true);

            @event.ParentFullUniquePath = gonetParticipant.transform.parent == null ? string.Empty : HierarchyUtils.GetFullUniquePath(gonetParticipant.transform.parent.gameObject);

            @event.GONetId = gonetParticipant.GONetId;
            @event.GONetIdAtInstantiation = gonetParticipant.GONetIdAtInstantiation;
            @event.OwnerAuthorityId = gonetParticipant.OwnerAuthorityId;
            @event.SpawnerPersistentId = gonetParticipant.SpawnerPersistentId;
            @event.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority = true;

            @event.Position = gonetParticipant.transform.position;
            @event.Rotation = gonetParticipant.transform.rotation;

            // CRITICAL: Objects with GONetSessionContext (GONetGlobal, GONetLocal) persist via DontDestroyOnLoad
            // They must ALWAYS use "DontDestroyOnLoad" as SceneIdentifier, even if currently in a regular scene
            // Otherwise SceneUnloadEvent will incorrectly cancel their spawn events when original scene unloads
            @event.SceneIdentifier = gonetParticipant.GetComponent<GONetSessionContext>() != null
                ? HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE
                : GONetSceneManager.GetSceneIdentifier(gonetParticipant.gameObject);

            @event.OccurredAtElapsedTicks = default;

            // Serialize custom spawn data from IGONetSpawnDataProvider components
            @event.CustomSpawnData = SerializeCustomSpawnData(gonetParticipant);

            return @event;
        }

        /// <summary>
        /// Serializes custom initialization data from all <see cref="IGONetSyncdBehaviourInitializer"/> components on the given GONetParticipant.
        /// </summary>
        /// <param name="gonetParticipant">The participant being initialized</param>
        /// <returns>Serialized initialization data byte array, or null if no initializers found</returns>
        private static byte[] SerializeCustomSpawnData(GONetParticipant gonetParticipant)
        {
            // Find all IGONetSyncdBehaviourInitializer components on the same GameObject
            IGONetSyncdBehaviourInitializer[] providers = gonetParticipant.GetComponents<IGONetSyncdBehaviourInitializer>();

            if (providers == null || providers.Length == 0)
            {
                return null; // No spawn data providers
            }

            // Create builder for serialization
            Utils.BitByBitByteArrayBuilder builder = Utils.BitByBitByteArrayBuilder.GetBuilder();

            // Write provider count (for deserialization validation)
            builder.WriteUInt((uint)providers.Length, 8); // Max 255 providers (overkill, but safe)

            // Call each provider's serialization method
            foreach (IGONetSyncdBehaviourInitializer provider in providers)
            {
                provider.Spawner_SerializeSpawnData(builder);
            }

            // Return serialized byte array (copy only the written bytes, not the full buffer)
            int bytesWritten = builder.Length_WrittenBytes;
            byte[] result = new byte[bytesWritten];
            Array.Copy(builder.GetBuffer(), 0, result, 0, bytesWritten);
            return result;
        }
    }

    /// <summary>
    /// Commands all machines to despawn a <see cref="GONetParticipant"/> and its <see cref="GameObject"/>.
    /// <para>This event represents an **intentional gameplay despawn** (not scene lifecycle destruction).</para>
    ///
    /// <para><b>Networking Behavior:</b></para>
    /// <list type="bullet">
    /// <item><b>Network Propagation:</b> YES - Sent to all remote connections</item>
    /// <item><b>Persistent Event:</b> YES - Added to persistent event history for late-joining clients</item>
    /// <item><b>Cancels Spawn:</b> YES - Cancels corresponding <see cref="InstantiateGONetParticipantEvent"/> in persistent history</item>
    /// </list>
    ///
    /// <para><b>When This Event is Published:</b></para>
    /// <list type="bullet">
    /// <item>Player/AI destroys an object through gameplay logic</item>
    /// <item>Projectile hits target and is removed</item>
    /// <item>Pickup item is collected and removed</item>
    /// <item>Any intentional, non-scene-related object removal</item>
    /// </list>
    ///
    /// <para><b>When This Event is NOT Published:</b></para>
    /// <list type="bullet">
    /// <item>Scene is unloading (objects destroyed as part of scene lifecycle)</item>
    /// <item>Application is quitting</item>
    /// <item>Object is in a DontDestroyOnLoad scene during scene transition</item>
    /// </list>
    ///
    /// <para><b>Usage Example:</b></para>
    /// <code>
    /// // Subscribe to gameplay despawns only (not scene unloads)
    /// GONetMain.EventBus.Subscribe&lt;DespawnGONetParticipantEvent&gt;(evt => {
    ///     GONetLog.Info($"Object despawned through gameplay: {evt.GONetId}");
    ///     // Handle gameplay-specific cleanup, scoring, etc.
    /// });
    /// </code>
    ///
    /// <para>See GONet scene management documentation for complete scene lifecycle details.</para>
    /// </summary>
    [MemoryPackable]
    public partial class DespawnGONetParticipantEvent : IPersistentEvent, ICancelOutOtherEvents
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The GONetId of the GONetParticipant being despawned.
        /// </summary>
        public uint GONetId;

        // REPARENTING FIX (Jan 2026): Added ReparentGONetParticipantEvent to the list.
        // Previously, despawned objects left stale reparent events in persistentEventsThisSession,
        // causing late joiners to receive reparent events for objects that no longer exist.
        static readonly Type[] otherEventsTypeCancelledOut = new[] {
            typeof(InstantiateGONetParticipantEvent),
            typeof(ValueMonitoringSupport_NewBaselineEvent),
            typeof(ValueMonitoringSupport_BaselineExpiredEvent),
            typeof(ReparentGONetParticipantEvent)
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is InstantiateGONetParticipantEvent)
            {
                InstantiateGONetParticipantEvent instantiationEvent = (InstantiateGONetParticipantEvent)otherEvent;
                return instantiationEvent.GONetId != GONetParticipant.GONetId_Unset &&
                    (instantiationEvent.GONetId == GONetId || instantiationEvent.GONetId == GONetMain.GetGONetIdAtInstantiation(GONetId));
            }
            else if (otherEvent is ValueMonitoringSupport_NewBaselineEvent)
            {
                ValueMonitoringSupport_NewBaselineEvent newBaselineEvent = (ValueMonitoringSupport_NewBaselineEvent)otherEvent;
                return newBaselineEvent.GONetId != GONetParticipant.GONetId_Unset && newBaselineEvent.GONetId == GONetId;
            }
            else if (otherEvent is ValueMonitoringSupport_BaselineExpiredEvent)
            {
                ValueMonitoringSupport_BaselineExpiredEvent expiredBaselineEvent = (ValueMonitoringSupport_BaselineExpiredEvent)otherEvent;
                return expiredBaselineEvent.GONetId != GONetParticipant.GONetId_Unset && expiredBaselineEvent.GONetId == GONetId;
            }
            // REPARENTING FIX (Jan 2026): Cancel reparent events when object is despawned.
            // This prevents late joiners from receiving reparent events for non-existent objects.
            else if (otherEvent is ReparentGONetParticipantEvent)
            {
                ReparentGONetParticipantEvent reparentEvent = (ReparentGONetParticipantEvent)otherEvent;
                return reparentEvent.GONetId != GONetParticipant.GONetId_Unset && reparentEvent.GONetId == GONetId;
            }

            return false;
        }
    }

    [MemoryPackable]
    public partial class PoolIdRangeEntry
    {
        public ushort DesignTimeLocationIndex;
        public uint GONetIdRawStart;
        public ushort Count;
        public string SceneIdentifier;
        public bool PersistAcrossScenes;
    }

    [MemoryPackable]
    public partial class PoolInitializationEvent : IPersistentEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        [MemoryPackOrder(0)]
        public List<PoolIdRangeEntry> Ranges { get; set; } = new List<PoolIdRangeEntry>(4);
    }

    [MemoryPackable]
    public partial class PoolGrowthEvent : IPersistentEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        [MemoryPackOrder(0)]
        public List<PoolIdRangeEntry> Ranges { get; set; } = new List<PoolIdRangeEntry>(4);
    }

    [MemoryPackable]
    public partial class PoolObjectBorrowEvent : IPersistentEvent, ICancelOutOtherEvents, IHaveRelatedGONetId
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        public uint GONetId { get; set; }
        public ushort BorrowerAuthorityId;
        public Vector3 Position;
        public Quaternion Rotation;
        public uint RequestId;

        static readonly Type[] otherEventsTypeCancelledOut = new[] {
            typeof(PoolObjectBorrowEvent)
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is PoolObjectBorrowEvent borrowEvent)
            {
                return borrowEvent.GONetId != GONetParticipant.GONetId_Unset && borrowEvent.GONetId == GONetId;
            }

            return false;
        }
    }

    [MemoryPackable]
    public partial class PoolObjectReturnEvent : IPersistentEvent, ICancelOutOtherEvents, IHaveRelatedGONetId
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        public uint GONetId { get; set; }

        static readonly Type[] otherEventsTypeCancelledOut = new[] {
            typeof(PoolObjectBorrowEvent)
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is PoolObjectBorrowEvent borrowEvent)
            {
                return borrowEvent.GONetId != GONetParticipant.GONetId_Unset && borrowEvent.GONetId == GONetId;
            }

            return false;
        }
    }

    public enum PoolObjectDestroyedReason : byte
    {
        Unknown = 0,
        DestroyCalled = 1,
        SceneUnloaded = 2,
        Corrupted = 3,
    }

    [MemoryPackable]
    public partial class PoolObjectDestroyedEvent : IPersistentEvent, ICancelOutOtherEvents, IHaveRelatedGONetId
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        public uint GONetId { get; set; }
        public PoolObjectDestroyedReason ReasonCode;

        static readonly Type[] otherEventsTypeCancelledOut = new[] {
            typeof(PoolObjectBorrowEvent)
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is PoolObjectBorrowEvent borrowEvent)
            {
                return borrowEvent.GONetId != GONetParticipant.GONetId_Unset && borrowEvent.GONetId == GONetId;
            }

            return false;
        }
    }

    [MemoryPack.MemoryPackUnion(0, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_Single))]
    [MemoryPack.MemoryPackUnion(1, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector2))]
    [MemoryPack.MemoryPackUnion(2, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector3))]
    [MemoryPack.MemoryPackUnion(3, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector4))]
    [MemoryPack.MemoryPackUnion(4, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Quaternion))]
    [MemoryPack.MemoryPackUnion(5, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_Boolean))]
    [MemoryPack.MemoryPackUnion(6, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_Byte))]
    [MemoryPack.MemoryPackUnion(7, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_SByte))]
    [MemoryPack.MemoryPackUnion(8, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_Int16))]
    [MemoryPack.MemoryPackUnion(9, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_UInt16))]
    [MemoryPack.MemoryPackUnion(10, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_Int32))]
    [MemoryPack.MemoryPackUnion(11, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_UInt32))]
    [MemoryPack.MemoryPackUnion(12, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_Int64))]
    [MemoryPack.MemoryPackUnion(13, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_UInt64))]
    [MemoryPack.MemoryPackUnion(14, typeof(GONet.ValueMonitoringSupport_NewBaselineEvent_System_Double))]
    [MemoryPackable]
    public abstract partial class ValueMonitoringSupport_NewBaselineEvent : IPersistentEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        public uint GONetId { get; set; }

        public byte ValueIndex { get; set; }
    }

    #region ValueMonitoringSupport_NewBaselineEvent child classes for each supported type
    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_Single : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.Single NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector2 : ValueMonitoringSupport_NewBaselineEvent
    {
        public UnityEngine.Vector2 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector3 : ValueMonitoringSupport_NewBaselineEvent
    {
        public UnityEngine.Vector3 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector4 : ValueMonitoringSupport_NewBaselineEvent
    {
        public UnityEngine.Vector4 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Quaternion : ValueMonitoringSupport_NewBaselineEvent
    {
        public UnityEngine.Quaternion NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_Boolean : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.Boolean NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_Byte : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.Byte NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_SByte : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.SByte NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_Int16 : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.Int16 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_UInt16 : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.UInt16 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_Int32 : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.Int32 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_UInt32 : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.UInt32 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_Int64 : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.Int64 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_UInt64 : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.UInt64 NewBaselineValue { get; set; }
    }

    [MemoryPackable]
    public partial class ValueMonitoringSupport_NewBaselineEvent_System_Double : ValueMonitoringSupport_NewBaselineEvent
    {
        public System.Double NewBaselineValue { get; set; }
    }
    #endregion

    /// <summary>
    /// <para>
    /// This class uses a feature of the <see cref="ICancelOutOtherEvents"/> processing to allow us to only send newly connecting clients just
    /// the most recent <see cref="ValueMonitoringSupport_NewBaselineEvent"/> instead of the entire history along the way of the game.
    /// </para>
    /// <para>
    /// IMPORTANT: The semantics of this class and how GONet promises to use it is: for every instance of this class/event published, it is 
    /// immediately followed by publishing a corresponding instance of <see cref="ValueMonitoringSupport_NewBaselineEvent"/>.
    /// </para>
    /// </summary>
    [MemoryPackable]
    public partial class ValueMonitoringSupport_BaselineExpiredEvent : IPersistentEvent, ICancelOutOtherEvents
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        public uint GONetId { get; set; }

        public byte ValueIndex { get; set; }

        // CRITICAL FIX (Dec 2025): Also cancel out previous BaselineExpiredEvent for same GONetId+ValueIndex.
        // Without this, baseline events accumulate indefinitely:
        // 1. BaselineExpiredEvent(X,Y) added 2. NewBaselineEvent(X,Y) added
        // 3. Next frame: New BaselineExpiredEvent(X,Y) cancels NewBaselineEvent, adds itself
        // 4. New NewBaselineEvent(X,Y) added
        // 5. Result: TWO BaselineExpiredEvent entries - accumulation!
        // By cancelling previous BaselineExpiredEvent, we maintain exactly one baseline event pair per value.
        static readonly Type[] otherEventTypesCancelledOut = new[] { typeof(ValueMonitoringSupport_NewBaselineEvent), typeof(ValueMonitoringSupport_BaselineExpiredEvent) };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventTypesCancelledOut;

        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is ValueMonitoringSupport_NewBaselineEvent newBaselineEvent)
            {
                return newBaselineEvent.GONetId != GONetParticipant.GONetId_Unset && newBaselineEvent.GONetId == GONetId
                    && newBaselineEvent.ValueIndex == ValueIndex;
            }
            else if (otherEvent is ValueMonitoringSupport_BaselineExpiredEvent prevExpiredEvent)
            {
                // Cancel out previous expired event for same value
                return prevExpiredEvent.GONetId != GONetParticipant.GONetId_Unset && prevExpiredEvent.GONetId == GONetId
                    && prevExpiredEvent.ValueIndex == ValueIndex;
            }
            return false;
        }
    }

    [MemoryPackable]
    public partial class PersistentEvents_Bundle : ITransientEvent
    {
        public long OccurredAtElapsedTicks { get; set; }

        public LinkedList<IPersistentEvent> PersistentEvents;

        public PersistentEvents_Bundle() { }

        [MemoryPackConstructor]
        public PersistentEvents_Bundle(long occurredAtElapsedTicks, LinkedList<IPersistentEvent> persistentEvents) : this()
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            PersistentEvents = persistentEvents;
        }
    }

    /// <summary>
    /// Represents a single chunk of a large PersistentEvents_Bundle that has been split for transmission.
    /// Used when persistent events exceed safe message size limits (> 12 KB).
    /// The client reassembles all chunks before deserializing the complete bundle.
    /// NOTE: Implements ITransientEvent since chunks are transport-layer constructs (not business logic)
    /// that should only be sent to the specific recipient and not relayed/persisted.
    /// </summary>
    [MemoryPackable]
    public partial class PersistentEvents_BundleChunk : ITransientEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// Unique identifier for this multi-chunk message. All chunks with the same ChunkId belong together.
        /// </summary>
        public uint ChunkId { get; set; }

        /// <summary>
        /// Zero-based index of this chunk (0, 1, 2, ..., TotalChunks-1)
        /// </summary>
        public ushort ChunkIndex { get; set; }

        /// <summary>
        /// Total number of chunks in the complete message
        /// </summary>
        public ushort TotalChunks { get; set; }

        /// <summary>
        /// Raw serialized data for this chunk (max ~12.2 KB of data per chunk, resulting in ~12 KB total after wrapper overhead)
        /// </summary>
        public byte[] ChunkData { get; set; }

        /// <summary>
        /// Total size of the original uncompressed bundle (for validation and diagnostics)
        /// </summary>
        public int OriginalBundleSize { get; set; }

        public PersistentEvents_BundleChunk() { }

        [MemoryPackConstructor]
        public PersistentEvents_BundleChunk(uint chunkId, ushort chunkIndex, ushort totalChunks, byte[] chunkData, int originalBundleSize)
        {
            ChunkId = chunkId;
            ChunkIndex = chunkIndex;
            TotalChunks = totalChunks;
            ChunkData = chunkData;
            OriginalBundleSize = originalBundleSize;
        }
    }

    [MemoryPackable]
    public partial class ClientTypeFlagsChangedEvent : ITransientEvent
    {
        public long OccurredAtElapsedTicks { get; set; }

        public ushort ClientAuthorityId { get; set; }

        public ClientTypeFlags FlagsPrevious { get; set; }

        public ClientTypeFlags FlagsNow { get; set; }

        public ClientTypeFlagsChangedEvent(long occurredAtElapsedTicks, ushort clientAuthorityId, ClientTypeFlags flagsPrevious, ClientTypeFlags flagsNow)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            ClientAuthorityId = clientAuthorityId;
            FlagsPrevious = flagsPrevious;
            FlagsNow = flagsNow;
        }
    }

    /// <summary>
    /// IMPORTANT: This event is initiated (and first published) from a client once the state changes locally on that client, which is slightly different than <see cref="RemoteClientStateChangedEvent"/>
    /// </summary>
    [MemoryPackable]
    public partial class ClientStateChangedEvent : ITransientEvent
    {
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// NOTE: When processing this event on the server, this value can be used to lookup the corresponding <see cref="GONetRemoteClient"/> instance by 
        ///       calling <see cref="GONetServer.TryGetClientByConnectionUID(ulong, out GONetRemoteClient)"/>.
        /// </summary>
        public ulong InitiatingClientConnectionUID { get; set; }

        public ClientState StatePrevious { get; set; }

        public ClientState StateNow { get; set; }

        public ClientStateChangedEvent(long occurredAtElapsedTicks, ulong initiatingClientConnectionUID, ClientState statePrevious, ClientState stateNow)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            InitiatingClientConnectionUID = initiatingClientConnectionUID;
            StatePrevious = statePrevious;
            StateNow = stateNow;
        }
    }

    /// <summary>
    /// IMPORTANT: This event is initiated (and first published) from the server once the state changes locally on the server for a client, which is slightly different than <see cref="ClientStateChangedEvent"/>
    ///            When this event is fired and is received/processed on a client, the client's local data representing the client state may likely NOT be updated to reflect the state change
    ///            and if it is important that the client IS updated to reflect the state change, subscribe to <see cref="ClientStateChangedEvent"/> instead.
    /// </summary>
    [MemoryPackable]
    public partial class RemoteClientStateChangedEvent : ITransientEvent
    {
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// NOTE: When processing this event on the server, this value can be used to lookup the corresponding <see cref="GONetRemoteClient"/> instance by 
        ///       calling <see cref="GONetServer.TryGetClientByConnectionUID(ulong, out GONetRemoteClient)"/>.
        /// </summary>
        public ulong InitiatingClientConnectionUID { get; set; }

        /// <summary>
        /// Since this event initiates server side and the server will not have as many possible states for a client, the only values this might be are:
        /// <see cref="ClientState.Connected"/> and <see cref="ClientState.Disconnected"/> TODO: see about getting all other values working as well!
        /// </summary>
        public ClientState StatePrevious { get; set; }

        public ClientState StateNow { get; set; }

        public RemoteClientStateChangedEvent(long occurredAtElapsedTicks, ulong initiatingClientConnectionUID, ClientState statePrevious, ClientState stateNow)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            InitiatingClientConnectionUID = initiatingClientConnectionUID;
            StatePrevious = statePrevious;
            StateNow = stateNow;
        }
    }

    public enum SyncEvent_ValueChangeProcessedExplanation : byte
    {
        OutboundToOthers = 1,

        InboundFromOther,

        BlendingBetweenInboundValuesFromOther,
    }

    /// <summary>
    /// Once this event is sent through <see cref="GONetEventBus.Publish{T}(T, uint?)"/>, it will automatically have <see cref="Return"/> called on it.
    /// At time of writing, this is to support (automatic) object pool usage for better memory/garbage/GC performance.
    ///
    /// IMPORTANT COMPATIBILITY CONSTRAINT: Events implementing ISelfReturnEvent should NOT also implement IPersistentEvent.
    ///
    /// REASON: GONet's persistence system stores direct references to persistent events for late-joining clients.
    /// If a persistent event also implemented ISelfReturnEvent, its Return() method would clear the event data
    /// after processing, corrupting the data when it's later sent to new clients.
    ///
    /// DESIGN PATTERN:
    /// - Transient events (ITransientEvent) + ISelfReturnEvent = SAFE (immediate processing, pooling enabled)
    /// - Persistent events (IPersistentEvent) + NO pooling = SAFE (stored references, data preserved)
    /// - Persistent events + ISelfReturnEvent = DANGEROUS (data corruption for late-joining clients)
    ///
    /// This constraint ensures data integrity in GONet's event persistence mechanism.
    /// </summary>
    public interface ISelfReturnEvent
    {
        void Return();
    }

    public interface IHaveRelatedGONetId
    {
        uint GONetId { get; set; }
    }

    [MemoryPackable]
    public partial class InternalOnlyMemoryPackComilationAssistanceForGenerated : SyncEvent_ValueChangeProcessed
    {
        public override GONetSyncableValue ValuePrevious => throw new NotImplementedException();

        public override GONetSyncableValue ValueNew => throw new NotImplementedException();

        public override SyncEvent_GeneratedTypes SyncEvent_GeneratedType => throw new NotImplementedException();

        public override void Return()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// This represents that a sync value change has been processed locally.  Two major occassions:
    /// 1) For an outbound change being sent to others (in which case, this event is published AFTER the change has been sent to remote sources)
    /// 2) For an inbound change received from other (in which case, this event is published AFTER the change has been applied)
    /// </summary>
    [MemoryPack.MemoryPackUnion(ushort.MaxValue, typeof(InternalOnlyMemoryPackComilationAssistanceForGenerated))]
    [MemoryPackable]
    public abstract partial class SyncEvent_ValueChangeProcessed : ITransientEvent, ILocalOnlyPublish, ISelfReturnEvent
    {
        public double OccurredAtElapsedSeconds { get => TimeSpan.FromTicks(OccurredAtElapsedTicks).TotalSeconds; set { OccurredAtElapsedTicks = TimeSpan.FromSeconds(value).Ticks; } }
        [MemoryPackIgnore] public long OccurredAtElapsedTicks { get; set; }

        public double ProcessedAtElapsedSeconds { get => TimeSpan.FromTicks(ProcessedAtElapsedTicks).TotalSeconds; set { ProcessedAtElapsedTicks = TimeSpan.FromSeconds(value).Ticks; } }
        [MemoryPackIgnore] public long ProcessedAtElapsedTicks;

        public ushort RelatedOwnerAuthorityId;
        public uint GONetId;

        [MemoryPackIgnore] public byte CodeGenerationId;

        public byte SyncMemberIndex;
        public SyncEvent_ValueChangeProcessedExplanation Explanation;

        [MemoryPackIgnore] public abstract GONetSyncableValue ValuePrevious { get; }
        [MemoryPackIgnore] public abstract GONetSyncableValue ValueNew { get; }
        [MemoryPackIgnore] public abstract SyncEvent_GeneratedTypes SyncEvent_GeneratedType { get; }

        /// <summary>
        /// Do NOT use!  This is for object pooling and MessagePack only.
        /// </summary>
        public SyncEvent_ValueChangeProcessed() { }

        public abstract void Return();
    }

    [MemoryPackable]
    public partial class SyncEvent_PersistenceBundle
    {
        public Queue<SyncEvent_ValueChangeProcessed> bundle;

        public static readonly SyncEvent_PersistenceBundle Instance = new SyncEvent_PersistenceBundle();
    }

    /// <summary>
    /// This represents that a sync value change has been processed.  Two major occassions:
    /// 1) For an outbound change being sent to others (in which case, this event is published AFTER the change has been sent to remote sources)
    /// 2) For an inbound change received from other (in which case, this event is published AFTER the change has been applied)
    /// </summary>
    [MemoryPackable]
    public sealed partial class SyncEvent_Time_ElapsedTicks_SetFromAuthority : SyncEvent_ValueChangeProcessed
    {
        public double ElapsedSeconds_Previous { get => TimeSpan.FromTicks(ElapsedTicks_Previous).TotalSeconds; set { ElapsedTicks_Previous = TimeSpan.FromSeconds(value).Ticks; } }
        [MemoryPackIgnore] public long ElapsedTicks_Previous { get; private set; }

        public double ElapsedSeconds_New { get => TimeSpan.FromTicks(ElapsedTicks_New).TotalSeconds; set { ElapsedTicks_New = TimeSpan.FromSeconds(value).Ticks; } }
        [MemoryPackIgnore] public long ElapsedTicks_New { get; private set; }

        public double RoundTripSeconds_Latest { get; set; }
        public double RoundTripSeconds_RecentAverage { get; set; }
        public float RoundTripMilliseconds_LowLevelTransportProtocol { get; set; }

        public override GONetSyncableValue ValuePrevious => ElapsedTicks_Previous;
        public override GONetSyncableValue ValueNew => ElapsedTicks_New;
        public override SyncEvent_GeneratedTypes SyncEvent_GeneratedType => throw new NotImplementedException();

        static readonly ObjectPool<SyncEvent_Time_ElapsedTicks_SetFromAuthority> pool = new ObjectPool<SyncEvent_Time_ElapsedTicks_SetFromAuthority>(5, 1);
        static readonly ConcurrentQueue<SyncEvent_Time_ElapsedTicks_SetFromAuthority> returnQueue_onceOnBorrowThread = new ConcurrentQueue<SyncEvent_Time_ElapsedTicks_SetFromAuthority>();
        static System.Threading.Thread borrowThread;

        /// <summary>
        /// Do NOT use!  This is for object pooling and MessagePack only.
        /// Instead, call <see cref="Borrow(SyncEvent_ValueChangeProcessedExplanation, long, uint, uint, byte, long, long)"/>.
        /// </summary>
        public SyncEvent_Time_ElapsedTicks_SetFromAuthority() { }

        /// <summary>
        /// IMPORTANT: It is the caller's responsibility to ensure the instance returned from this method is also returned back
        ///            here (i.e., to private object pool) via <see cref="Return(SyncEvent_Time_ElapsedTicks_SetFromAuthority)"/> when no longer needed!
        /// </summary>
        public static SyncEvent_Time_ElapsedTicks_SetFromAuthority Borrow(long elapsedTicks_previous, long elapsedTicks_new, float roundTripSeconds_latest, float roundTripSeconds_recentAverage, float roundTripMilliseconds_LowLevelTransportProtocol)
        {
            if (borrowThread == null)
            {
                borrowThread = System.Threading.Thread.CurrentThread;
            }
            else if (borrowThread != System.Threading.Thread.CurrentThread)
            {
                const string REQUIRED_CALL_SAME_BORROW_THREAD = "Not allowed to call this from more than one thread.  So, ensure Borrow() is called from the same exact thread for this specific event type.  NOTE: Each event type can have its' Borrow() called from a different thread from one another.";
                throw new InvalidOperationException(REQUIRED_CALL_SAME_BORROW_THREAD);
            }

            int autoReturnCount = returnQueue_onceOnBorrowThread.Count;
            SyncEvent_Time_ElapsedTicks_SetFromAuthority autoReturn;
            while (returnQueue_onceOnBorrowThread.TryDequeue(out autoReturn) && autoReturnCount > 0)
            {
                Return(autoReturn);
                --autoReturnCount; // Fixed: was ++ which caused infinite loop potential
            }

            var @event = pool.Borrow();

            @event.RoundTripSeconds_Latest = roundTripSeconds_latest;
            @event.RoundTripSeconds_RecentAverage = roundTripSeconds_recentAverage;
            @event.RoundTripMilliseconds_LowLevelTransportProtocol = roundTripMilliseconds_LowLevelTransportProtocol;

            @event.Explanation = SyncEvent_ValueChangeProcessedExplanation.InboundFromOther;
            @event.OccurredAtElapsedTicks = elapsedTicks_previous;
            @event.RelatedOwnerAuthorityId = GONetMain.OwnerAuthorityId_Server;

            { // meaningless for this event:
                @event.GONetId = GONetParticipant.GONetId_Unset;
                @event.CodeGenerationId = 0;
                @event.SyncMemberIndex = 0;
            }

            @event.ElapsedTicks_Previous = elapsedTicks_previous;
            @event.ElapsedTicks_New = elapsedTicks_new;

            return @event;
        }

        public override void Return()
        {
            Return(this);
        }

        public static void Return(SyncEvent_Time_ElapsedTicks_SetFromAuthority borrowed)
        {
            if (borrowThread == System.Threading.Thread.CurrentThread)
            {
                pool.Return(borrowed);
            }
            else
            {
                returnQueue_onceOnBorrowThread.Enqueue(borrowed);
            }
        }
    }

    #region Scene Management Events

    /// <summary>
    /// Indicates which loading system to use for the scene.
    /// </summary>
    public enum SceneLoadType : byte
    {
        /// <summary>
        /// Traditional: Scene in Build Settings, loaded by name/build index
        /// </summary>
        BuildSettings = 0,

        /// <summary>
        /// Modern: Scene loaded via Unity Addressables system
        /// </summary>
        Addressables = 1
    }

    /// <summary>
    /// Persistent event for scene loading.
    /// Server publishes this when loading a scene, clients receive and load accordingly.
    /// Late-joining clients receive this event to sync scene state.
    ///
    /// <para><b>Cancellation Behavior (LoadSceneMode.Single only):</b></para>
    /// <para>When loading a scene with LoadSceneMode.Single, this event cancels ALL previous SceneLoadEvent instances
    /// from the persistent event history. This prevents late-joining clients from experiencing sequential scene loads
    /// that already-connected clients never saw.</para>
    ///
    /// <para><b>Example Problem Without Cancellation:</b></para>
    /// <list type="bullet">
    /// <item>Server loads Scene A (Single mode) → SceneLoadEvent #1 persists</item>
    /// <item>Server loads Scene B (Single mode) → SceneLoadEvent #2 persists</item>
    /// <item>Server loads Scene C (Single mode) → SceneLoadEvent #3 persists</item>
    /// <item>Late-joiner connects → Receives all 3 events → Loads A, then B, then C (confusion!)</item>
    /// </list>
    ///
    /// <para><b>Solution With Cancellation:</b></para>
    /// <list type="bullet">
    /// <item>Server loads Scene A (Single mode) → SceneLoadEvent #1 persists</item>
    /// <item>Server loads Scene B (Single mode) → SceneLoadEvent #2 persists, cancels #1</item>
    /// <item>Server loads Scene C (Single mode) → SceneLoadEvent #3 persists, cancels #2</item>
    /// <item>Late-joiner connects → Receives only event #3 → Loads C directly (correct!)</item>
    /// </list>
    ///
    /// <para><b>Additive Mode Behavior:</b></para>
    /// <para>LoadSceneMode.Additive events do NOT cancel previous loads, as additive scenes
    /// are meant to stack on top of existing scenes.</para>
    /// </summary>
    [MemoryPackable]
    public partial class SceneLoadEvent : IPersistentEvent, ICancelOutOtherEvents
    {
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// Scene name (for Build Settings) or addressable key (for Addressables)
        /// </summary>
        public string SceneName;

        /// <summary>
        /// Build index for scenes in Build Settings (fallback identifier)
        /// </summary>
        public int SceneBuildIndex = -1;

        /// <summary>
        /// Which loading system to use
        /// </summary>
        public SceneLoadType LoadType;

        /// <summary>
        /// Single or Additive loading mode
        /// </summary>
        public UnityEngine.SceneManagement.LoadSceneMode Mode;

        /// <summary>
        /// For Addressables: Whether to activate scene immediately after loading
        /// </summary>
        public bool ActivateOnLoad = true;

        /// <summary>
        /// For Addressables: Loading priority
        /// </summary>
        public int Priority = 100;

        static readonly Type[] otherEventsTypeCancelledOut = new[] {
            typeof(SceneLoadEvent)
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            // Only LoadSceneMode.Single cancels previous scene loads
            // Additive scenes should stack, not replace
            if (Mode != UnityEngine.SceneManagement.LoadSceneMode.Single)
            {
                return false;
            }

            if (otherEvent is SceneLoadEvent previousLoadEvent)
            {
                // Cancel ALL previous SceneLoadEvent instances when loading in Single mode
                // This ensures late-joiners only see the most recent scene state
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Persistent event for scene unloading.
    /// Cancels out corresponding SceneLoadEvent for late-joining clients.
    /// </summary>
    [MemoryPackable]
    public partial class SceneUnloadEvent : IPersistentEvent, ICancelOutOtherEvents
    {
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// Scene name or addressable key to unload
        /// </summary>
        public string SceneName;

        /// <summary>
        /// Build index for fallback identification
        /// </summary>
        public int SceneBuildIndex = -1;

        /// <summary>
        /// Which loading system was used
        /// </summary>
        public SceneLoadType LoadType;

        static readonly Type[] otherEventsTypeCancelledOut = new[] {
            typeof(SceneLoadEvent),
            typeof(InstantiateGONetParticipantEvent),  // CRITICAL: Also cancel spawns from unloaded scenes
            typeof(ValueMonitoringSupport_NewBaselineEvent),  // CRITICAL: Also cancel value events for destroyed objects
            typeof(ValueMonitoringSupport_BaselineExpiredEvent)  // CRITICAL: Also cancel expired baseline events
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is SceneLoadEvent loadEvent)
            {
                // Cancel if same scene name
                if (!string.IsNullOrEmpty(SceneName) && SceneName == loadEvent.SceneName)
                    return true;

                // Fallback: cancel if same build index (and both are build settings scenes)
                if (SceneBuildIndex >= 0 &&
                    SceneBuildIndex == loadEvent.SceneBuildIndex &&
                    LoadType == SceneLoadType.BuildSettings &&
                    loadEvent.LoadType == SceneLoadType.BuildSettings)
                    return true;
            }
            else if (otherEvent is InstantiateGONetParticipantEvent spawnEvent)
            {
                // CRITICAL FIX: When scene unloads, remove ALL spawn events for objects that were in that scene
                // WITHOUT removing DontDestroyOnLoad objects (they persist across scene changes!)
                // Without this, late-joiners receive spawn events for non-existent objects from unloaded scenes

                // CRITICAL: Never cancel spawns in DontDestroyOnLoad scene - these objects persist across ALL scene changes
                // Examples: GONet_GlobalContext, GONet_LocalContext, player objects with AutoDontDestroyOnLoad=true
                if (spawnEvent.SceneIdentifier == HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE)
                {
                    return false; // DontDestroyOnLoad objects are NEVER cancelled by scene unloads
                }

                // Cancel if spawn's scene matches the unloaded scene
                if (!string.IsNullOrEmpty(SceneName) && SceneName == spawnEvent.SceneIdentifier)
                    return true;

                // Fallback: check by build index for build settings scenes
                // Note: SceneIdentifier may be addressable path, so this only works for build settings scenes
                if (SceneBuildIndex >= 0 && LoadType == SceneLoadType.BuildSettings)
                {
                    // Try to parse build index from SceneIdentifier if it's a build settings scene
                    // SceneIdentifier format for build settings: scene name (or may match exactly)
                    if (spawnEvent.SceneIdentifier == SceneName)
                        return true;
                }
            }
            else if (otherEvent is ValueMonitoringSupport_NewBaselineEvent baselineEvent)
            {
                // CRITICAL FIX: When scene unloads, also cancel value baseline events for objects in that scene
                // Value events reference GONetIds - if the spawn for that GONetId is in the unloaded scene, cancel the value event
                // This prevents "Unable to find GONetParticipant" errors for late-joiners

                // We need to check if the GONetId belongs to an object in the unloaded scene
                // Since we don't have direct scene info in baseline events, we rely on the spawn cancellation happening first
                // The persistent event system will remove both spawn AND value events for the same GONetId
                // For now, we can't directly cancel value events by scene - they get cancelled when the spawn is cancelled
                // This is handled by the persistent event cancellation mechanism in GONet.cs OnPersistentEvent_KeepTrack
                return false;  // Let the spawn cancellation handle it indirectly
            }
            else if (otherEvent is ValueMonitoringSupport_BaselineExpiredEvent expiredEvent)
            {
                // Same logic as NewBaselineEvent - rely on spawn cancellation
                return false;  // Let the spawn cancellation handle it indirectly
            }

            return false;
        }
    }

    /// <summary>
    /// Transient event published by CLIENT when a scene finishes loading.
    /// Server uses this to know when to send scene-defined object GONetId assignments.
    /// This ensures late-joining clients have fully loaded the scene before receiving GONetIds.
    /// </summary>
    [MemoryPackable]
    public partial class SceneLoadCompleteEvent : ITransientEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks => throw new System.NotImplementedException();

        /// <summary>
        /// Name of the scene that finished loading
        /// </summary>
        public string SceneName;

        /// <summary>
        /// Load mode that was used (Single or Additive)
        /// </summary>
        public UnityEngine.SceneManagement.LoadSceneMode Mode;
    }

    /// <summary>
    /// Transient, local-only event published when the client switches to a new host during hot standby failover.
    /// Game code can subscribe to this to handle any necessary state transitions.
    /// </summary>
    [MemoryPackable]
    public partial class HostSwitchoverEvent : ITransientEvent, ILocalOnlyPublish
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; }

        /// <summary>Authority ID of this client.</summary>
        public ushort MyAuthorityId { get; }

        /// <summary>Authority ID of the old host we were connected to (now dead).</summary>
        public ushort OldHostAuthorityId { get; }

        /// <summary>Authority ID of the new host we're switching to.</summary>
        public ushort NewHostAuthorityId { get; }

        [MemoryPackConstructor]
        public HostSwitchoverEvent() { }

        public HostSwitchoverEvent(long occurredAtElapsedTicks, ushort myAuthorityId, ushort oldHostAuthorityId, ushort newHostAuthorityId)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            MyAuthorityId = myAuthorityId;
            OldHostAuthorityId = oldHostAuthorityId;
            NewHostAuthorityId = newHostAuthorityId;
        }
    }

    /// <summary>
    /// Transient, local-only event published when host failover completes on this machine.
    /// Fired on both the new host (isSelf=true) and clients that accepted the new host (isSelf=false).
    /// Game code can subscribe to this via GONetMain.EventBus to react to host changes.
    /// </summary>
    [MemoryPackable]
    public partial class HostFailoverCompletedEvent : ITransientEvent, ILocalOnlyPublish
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>The new host's authority ID (1023 after promotion).</summary>
        public ushort NewHostAuthorityId { get; set; }

        /// <summary>
        /// The promoting peer's original authority ID before becoming 1023.
        /// Useful for identifying which peer promoted when both hosts have authority 1023.
        /// </summary>
        public ushort PromotingPeerOriginalAuthorityId { get; set; }

        /// <summary>True if this machine is the new host, false if we're a client accepting the new host.</summary>
        public bool IsSelf { get; set; }

        /// <summary>Number of GONetParticipants that had ownership migrated (only non-zero on new host).</summary>
        public int MigratedGNPCount { get; set; }

        [MemoryPackConstructor]
        public HostFailoverCompletedEvent() { }

        public HostFailoverCompletedEvent(long occurredAtElapsedTicks, ushort newHostAuthorityId,
            ushort promotingPeerOriginalAuthorityId, bool isSelf, int migratedGNPCount)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            NewHostAuthorityId = newHostAuthorityId;
            PromotingPeerOriginalAuthorityId = promotingPeerOriginalAuthorityId;
            IsSelf = isSelf;
            MigratedGNPCount = migratedGNPCount;
        }
    }

    /// <summary>
    /// Transient, local-only event published when a host demotes itself to a client.
    /// Fired only on the demoted host's machine.
    /// </summary>
    [MemoryPackable]
    public partial class HostDemotedEvent : ITransientEvent, ILocalOnlyPublish
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>Authority ID of the host we were (typically 1023).</summary>
        public ushort PreviousHostAuthorityId { get; set; }

        /// <summary>Our original authority ID before promotion, or 0 if original server.</summary>
        public ushort PreviousHostOriginalAuthorityId { get; set; }

        /// <summary>Our new authority ID after demotion (may be 0 until reassigned).</summary>
        public ushort DemotedHostNewAuthorityId { get; set; }

        /// <summary>Authority ID of the new host (typically 1023).</summary>
        public ushort NewHostAuthorityId { get; set; }

        /// <summary>The new host's original authority ID before promotion.</summary>
        public ushort NewHostOriginalAuthorityId { get; set; }

        /// <summary>Epoch for the new host.</summary>
        public uint NewHostEpoch { get; set; }

        /// <summary>True if demotion was part of a graceful handoff.</summary>
        public bool WasVoluntary { get; set; }

        [MemoryPackConstructor]
        public HostDemotedEvent() { }

        public HostDemotedEvent(
            long occurredAtElapsedTicks,
            ushort previousHostAuthorityId,
            ushort previousHostOriginalAuthorityId,
            ushort demotedHostNewAuthorityId,
            ushort newHostAuthorityId,
            ushort newHostOriginalAuthorityId,
            uint newHostEpoch,
            bool wasVoluntary)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            PreviousHostAuthorityId = previousHostAuthorityId;
            PreviousHostOriginalAuthorityId = previousHostOriginalAuthorityId;
            DemotedHostNewAuthorityId = demotedHostNewAuthorityId;
            NewHostAuthorityId = newHostAuthorityId;
            NewHostOriginalAuthorityId = newHostOriginalAuthorityId;
            NewHostEpoch = newHostEpoch;
            WasVoluntary = wasVoluntary;
        }
    }

    /// <summary>
    /// Transient, local-only event fired on the current host when a better vice host candidate
    /// has been consistently detected. Use this to prompt for voluntary migration.
    /// </summary>
    [MemoryPackable]
    public partial class BetterHostAvailableEvent : ITransientEvent, ILocalOnlyPublish
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>Current host's authority ID.</summary>
        public ushort CurrentHostAuthorityId { get; set; }

        /// <summary>Current host's calculated score.</summary>
        public float CurrentHostScore { get; set; }

        /// <summary>Better candidate's authority ID.</summary>
        public ushort BetterHostAuthorityId { get; set; }

        /// <summary>Better candidate's calculated score.</summary>
        public float BetterHostScore { get; set; }

        /// <summary>How much better the candidate is (ratio). Example: 0.30 = 30% better.</summary>
        public float ScoreDifferencePercent { get; set; }

        /// <summary>How long this candidate has been consistently better.</summary>
        public float SustainedDurationSeconds { get; set; }

        [MemoryPackConstructor]
        public BetterHostAvailableEvent() { }

        public BetterHostAvailableEvent(long occurredAtElapsedTicks, ushort currentHostAuthorityId, float currentHostScore,
            ushort betterHostAuthorityId, float betterHostScore, float scoreDifferencePercent, float sustainedDurationSeconds)
        {
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
            CurrentHostAuthorityId = currentHostAuthorityId;
            CurrentHostScore = currentHostScore;
            BetterHostAuthorityId = betterHostAuthorityId;
            BetterHostScore = betterHostScore;
            ScoreDifferencePercent = scoreDifferencePercent;
            SustainedDurationSeconds = sustainedDurationSeconds;
        }
    }

    #endregion

    #region Post-Failover Reconciliation

    /// <summary>
    /// Server sends this snapshot after failover to allow clients to reconcile their local state.
    /// Similar to late-joiner sync but subtractive: clients destroy local objects NOT in this list.
    /// This provides a self-healing mechanism for missed despawns during failover transitions.
    /// </summary>
    [MemoryPackable]
    public partial class PostFailoverReconciliationSnapshotEvent : ITransientEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The failover epoch when this snapshot was taken.
        /// Clients should ignore snapshots from older epochs.
        /// </summary>
        public uint FailoverEpoch { get; set; }

        /// <summary>
        /// Authoritative list of all alive GONetIds on the server at snapshot time.
        /// Clients destroy any local runtime-spawned objects NOT in this list.
        /// </summary>
        public uint[] AliveGONetIds { get; set; }

        /// <summary>
        /// Server's current time when snapshot was taken (for diagnostics/ordering).
        /// </summary>
        public double ServerElapsedSeconds { get; set; }

        /// <summary>
        /// Number of connected clients when snapshot was sent (for diagnostics).
        /// </summary>
        public int ConnectedClientCount { get; set; }

        public PostFailoverReconciliationSnapshotEvent() { }

        [MemoryPackConstructor]
        public PostFailoverReconciliationSnapshotEvent(uint failoverEpoch,
            uint[] aliveGONetIds, double serverElapsedSeconds, int connectedClientCount)
        {
            FailoverEpoch = failoverEpoch;
            AliveGONetIds = aliveGONetIds;
            ServerElapsedSeconds = serverElapsedSeconds;
            ConnectedClientCount = connectedClientCount;
        }
    }

    /// <summary>
    /// Client acknowledgment of reconciliation snapshot processing.
    /// Server can use this to track which clients have reconciled.
    /// </summary>
    [MemoryPackable]
    public partial class PostFailoverReconciliationAckEvent : ITransientEvent
    {
        [MemoryPackIgnore]
        public bool IsSingularRecipientOnly => true;

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The epoch that was reconciled.
        /// </summary>
        public uint FailoverEpoch { get; set; }

        /// <summary>
        /// Number of ghost objects destroyed during reconciliation.
        /// </summary>
        public int GhostsDestroyed { get; set; }

        /// <summary>
        /// Number of local objects that matched the server's list (already correct).
        /// </summary>
        public int ObjectsMatched { get; set; }

        public PostFailoverReconciliationAckEvent() { }

        [MemoryPackConstructor]
        public PostFailoverReconciliationAckEvent(uint failoverEpoch,
            int ghostsDestroyed, int objectsMatched)
        {
            FailoverEpoch = failoverEpoch;
            GhostsDestroyed = ghostsDestroyed;
            ObjectsMatched = objectsMatched;
        }
    }

    /// <summary>
    /// Client requests reconciliation from server after completing late-joiner sync.
    /// Only sent when mesh is enabled and a failover has occurred (epoch > 1).
    /// Server responds with <see cref="PostFailoverReconciliationSnapshotEvent"/>.
    /// </summary>
    [MemoryPackable]
    public partial class ReconciliationRequestEvent : ITransientEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The client's current epoch (for server to verify/log).
        /// </summary>
        public uint ClientEpoch { get; set; }

        public ReconciliationRequestEvent() { }

        [MemoryPackConstructor]
        public ReconciliationRequestEvent(uint clientEpoch)
        {
            ClientEpoch = clientEpoch;
        }
    }

    /// <summary>
    /// Client requests a full auto-sync state refresh after voluntary handoff.
    /// Server responds by sending AllCurrentValues bundles to the requesting client.
    /// </summary>
    [MemoryPackable]
    public partial class PostHandoffFullStateSyncRequestEvent : ITransientEvent
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The client's current epoch (for server to verify/log).
        /// </summary>
        public uint ClientEpoch { get; set; }

        public PostHandoffFullStateSyncRequestEvent() { }

        [MemoryPackConstructor]
        public PostHandoffFullStateSyncRequestEvent(uint clientEpoch)
        {
            ClientEpoch = clientEpoch;
        }
    }

    #endregion

    #region Reparenting Events

    /// <summary>
    /// Persistent event that synchronizes GONetParticipant reparenting across the network.
    /// When a GONetParticipant is reparented (e.g., item picked up by player), this event
    /// is published and persisted for late-joining clients.
    ///
    /// Key behaviors:
    /// - New ReparentEvent for same GONetId REPLACES previous (no accumulation)
    /// - ReparentEvent where NewParent == OriginalParent SELF-CANCELS (not stored)
    /// - DespawnEvent cancels both SpawnEvent and ReparentEvent
    ///
    /// Parent Reference Strategy (Hybrid Representation):
    /// - Use GONetId when parent is a GONetParticipant (fast + robust)
    /// - Use anchor GONetId + relative path when parent is non-GNP under a GNP
    /// - Sentinel: 0 means world root (null parent) or non-GNP parent (check path)
    /// - Fallback to full path for non-GNP containers
    /// </summary>
    [MemoryPackable]
    public partial class ReparentGONetParticipantEvent : IPersistentEvent, ICancelOutOtherEvents, IHaveRelatedGONetId
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The GONetId of the object being reparented.
        /// </summary>
        public uint GONetId { get; set; }

        /// <summary>
        /// The authority ID of the machine that initiated this reparent.
        /// Used for server-side validation.
        /// </summary>
        public ushort SourceAuthorityId { get; set; }

        #region Original Parent (for self-cancel detection)

        /// <summary>
        /// Original parent's GONetId (preferred when parent is a GNP).
        /// 0 = world root OR non-GNP parent (check OriginalParentFullUniquePath).
        /// </summary>
        public uint OriginalParentGONetId { get; set; }

        /// <summary>
        /// Original parent's full unique path (fallback for non-GNP parents).
        /// Empty string = world root (when OriginalParentGONetId is also 0).
        /// Non-empty = path to non-GNP container.
        /// </summary>
        public string OriginalParentFullUniquePath { get; set; }

        /// <summary>
        /// Original parent's unique path relative to the anchor GNP identified by OriginalParentGONetId.
        /// Empty string means OriginalParentGONetId is the direct parent GNP or world root.
        /// </summary>
        public string OriginalParentRelativePath { get; set; }

        #endregion

        #region New Parent

        /// <summary>
        /// New parent's GONetId (preferred when parent is a GNP).
        /// 0 = world root OR non-GNP parent (check NewParentFullUniquePath).
        /// When NewParentRelativePath is set, this is the anchor GNP (not the direct parent).
        /// </summary>
        public uint NewParentGONetId { get; set; }

        /// <summary>
        /// New parent's full unique path (fallback for non-GNP parents).
        /// Empty string = world root (when NewParentGONetId is also 0).
        /// Non-empty = path to non-GNP container.
        /// </summary>
        public string NewParentFullUniquePath { get; set; }

        /// <summary>
        /// New parent's unique path relative to the anchor GNP identified by NewParentGONetId.
        /// Empty string means NewParentGONetId is the direct parent GNP or world root.
        /// </summary>
        public string NewParentRelativePath { get; set; }

        #endregion

        #region Local Transform Offsets

        /// <summary>
        /// Local position offset to maintain relative to new parent.
        /// Captured at reparent time on authority, applied on non-authority.
        /// </summary>
        public UnityEngine.Vector3 LocalPositionOffset { get; set; }

        /// <summary>
        /// Local rotation offset to maintain relative to new parent.
        /// Captured at reparent time on authority, applied on non-authority.
        /// </summary>
        public UnityEngine.Quaternion LocalRotationOffset { get; set; }

        #endregion

        #region ICancelOutOtherEvents Implementation

        static readonly Type[] otherEventsTypeCancelledOut = new[]
        {
            typeof(ReparentGONetParticipantEvent)
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        /// <summary>
        /// Determines if this event cancels out another event.
        /// - A new ReparentEvent replaces the previous one for the same GONetId.
        /// </summary>
        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is ReparentGONetParticipantEvent otherReparent)
            {
                // Same object - new reparent replaces old reparent
                return otherReparent.GONetId == GONetId;
            }
            return false;
        }

        #endregion

        #region Self-Cancel Detection

        /// <summary>
        /// Determines if this event should self-cancel (not be persisted).
        /// Returns true if the new parent is the same as the original parent,
        /// meaning the object has returned to its original position.
        /// </summary>
        [MemoryPackIgnore]
        public bool ShouldSelfCancel
        {
            get
            {
                // Relative path comparison (anchor GNP + relative path)
                if (!string.IsNullOrEmpty(NewParentRelativePath) || !string.IsNullOrEmpty(OriginalParentRelativePath))
                {
                    return NewParentGONetId != 0 &&
                        NewParentGONetId == OriginalParentGONetId &&
                        NewParentRelativePath == OriginalParentRelativePath;
                }

                // Both using GONetId (common case for GNP parents)
                if (NewParentGONetId != 0 && OriginalParentGONetId != 0)
                {
                    return NewParentGONetId == OriginalParentGONetId;
                }

                // Both world root
                if (NewParentGONetId == 0 && OriginalParentGONetId == 0 &&
                    string.IsNullOrEmpty(NewParentFullUniquePath) &&
                    string.IsNullOrEmpty(OriginalParentFullUniquePath))
                {
                    return true; // Both are world root
                }

                // Fallback to path comparison for non-GNP parents
                if (NewParentGONetId == 0 && OriginalParentGONetId == 0)
                {
                    return NewParentFullUniquePath == OriginalParentFullUniquePath;
                }

                return false;
            }
        }

        #endregion

        #region Constructors

        [MemoryPackConstructor]
        public ReparentGONetParticipantEvent() { }

        /// <summary>
        /// Creates a ReparentGONetParticipantEvent with the specified parameters.
        /// </summary>
        public ReparentGONetParticipantEvent(
            uint gonetId,
            ushort sourceAuthorityId,
            uint originalParentGONetId,
            string originalParentPath,
            string originalParentRelativePath,
            uint newParentGONetId,
            string newParentPath,
            string newParentRelativePath,
            UnityEngine.Vector3 localPosition,
            UnityEngine.Quaternion localRotation,
            long occurredAtElapsedTicks)
        {
            GONetId = gonetId;
            SourceAuthorityId = sourceAuthorityId;
            OriginalParentGONetId = originalParentGONetId;
            OriginalParentFullUniquePath = originalParentPath ?? string.Empty;
            OriginalParentRelativePath = originalParentRelativePath ?? string.Empty;
            NewParentGONetId = newParentGONetId;
            NewParentFullUniquePath = newParentPath ?? string.Empty;
            NewParentRelativePath = newParentRelativePath ?? string.Empty;
            LocalPositionOffset = localPosition;
            LocalRotationOffset = localRotation;
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
        }

        #endregion

        public override string ToString()
        {
            string origParent = OriginalParentGONetId != 0
                ? (string.IsNullOrEmpty(OriginalParentRelativePath)
                    ? $"GNP:{OriginalParentGONetId}"
                    : $"GNP:{OriginalParentGONetId} RelPath:{OriginalParentRelativePath}")
                : (string.IsNullOrEmpty(OriginalParentFullUniquePath) ? "WorldRoot" : $"Path:{OriginalParentFullUniquePath}");

            string newParent = NewParentGONetId != 0
                ? (string.IsNullOrEmpty(NewParentRelativePath)
                    ? $"GNP:{NewParentGONetId}"
                    : $"GNP:{NewParentGONetId} RelPath:{NewParentRelativePath}")
                : (string.IsNullOrEmpty(NewParentFullUniquePath) ? "WorldRoot" : $"Path:{NewParentFullUniquePath}");

            return $"[ReparentEvent] GONetId:{GONetId} from:{origParent} to:{newParent} localPos:{LocalPositionOffset}";
        }
    }

    #endregion

    #region Animator Trigger Sync Events

    /// <summary>
    /// <para>
    /// Persistent event published when an Animator Trigger parameter is fired via <see cref="GONetParticipant.SetAnimatorTrigger"/>.
    /// </para>
    /// <para>
    /// <b>Why Event-Based (Not Value Monitoring):</b><br/>
    /// Unity has no Animator.GetTrigger() method - trigger state cannot be read.<br/>
    /// Triggers auto-reset after being consumed by the animator.<br/>
    /// GONet's value-based sync requires reading values to detect changes.<br/>
    /// Solution: Use IPersistentEvent pattern (proven by DespawnGONetParticipantEvent, ReparentGONetParticipantEvent).
    /// </para>
    /// <para>
    /// <b>Late-Joiner Behavior:</b><br/>
    /// 1. Authority fires SetAnimatorTrigger("Attack") → AnimatorTriggerFiredEvent published &amp; stored<br/>
    /// 2. At end of frame: AnimatorTriggerResetEvent published → cancels fired event from persistent history<br/>
    /// 3. Late-joiner connecting before reset: Receives trigger, animation plays<br/>
    /// 4. Late-joiner connecting after reset: No stale trigger (correct behavior)
    /// </para>
    /// <para>
    /// <b>Usage:</b>
    /// </para>
    /// <code>
    /// // Instead of: animator.SetTrigger("Jump");
    /// gonetParticipant.SetAnimatorTrigger("Jump");
    ///
    /// // Or with pre-computed hash for performance:
    /// static readonly int JumpHash = Animator.StringToHash("Jump");
    /// gonetParticipant.SetAnimatorTrigger(JumpHash);
    /// </code>
    /// </summary>
    [MemoryPackable]
    public partial class AnimatorTriggerFiredEvent : IPersistentEvent, IHaveRelatedGONetId
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The GONetId of the GONetParticipant whose Animator trigger was fired.
        /// </summary>
        public uint GONetId { get; set; }

        /// <summary>
        /// The hash of the trigger parameter name (from Animator.StringToHash).
        /// </summary>
        public int TriggerNameHash { get; set; }

        /// <summary>
        /// The authority ID of the machine that fired this trigger.
        /// </summary>
        public ushort SourceAuthorityId { get; set; }

        [MemoryPackConstructor]
        public AnimatorTriggerFiredEvent() { }

        public AnimatorTriggerFiredEvent(uint gonetId, int triggerNameHash, ushort sourceAuthorityId, long occurredAtElapsedTicks)
        {
            GONetId = gonetId;
            TriggerNameHash = triggerNameHash;
            SourceAuthorityId = sourceAuthorityId;
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
        }

        public override string ToString()
        {
            return $"[AnimatorTriggerFired] GONetId:{GONetId} TriggerHash:{TriggerNameHash} SourceAuth:{SourceAuthorityId}";
        }
    }

    /// <summary>
    /// <para>
    /// Persistent event published to cancel out a corresponding <see cref="AnimatorTriggerFiredEvent"/>.
    /// This ensures late-joiners do not receive stale trigger events.
    /// </para>
    /// <para>
    /// Automatically published at the end of the frame after <see cref="AnimatorTriggerFiredEvent"/> is fired.
    /// Uses <see cref="ICancelOutOtherEvents"/> to remove the fired event from persistent history.
    /// </para>
    /// </summary>
    [MemoryPackable]
    public partial class AnimatorTriggerResetEvent : IPersistentEvent, ICancelOutOtherEvents, IHaveRelatedGONetId
    {
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// The GONetId of the GONetParticipant whose Animator trigger was reset.
        /// </summary>
        public uint GONetId { get; set; }

        /// <summary>
        /// The hash of the trigger parameter name (from Animator.StringToHash).
        /// </summary>
        public int TriggerNameHash { get; set; }

        /// <summary>
        /// The authority ID of the machine that reset this trigger.
        /// </summary>
        public ushort SourceAuthorityId { get; set; }

        #region ICancelOutOtherEvents Implementation

        static readonly Type[] otherEventsTypeCancelledOut = new[]
        {
            typeof(AnimatorTriggerFiredEvent),
            typeof(AnimatorTriggerResetEvent)
        };

        [MemoryPackIgnore]
        public Type[] OtherEventTypesCancelledOut => otherEventsTypeCancelledOut;

        /// <summary>
        /// Determines if this reset event cancels out another event.
        /// Cancels AnimatorTriggerFiredEvent or previous AnimatorTriggerResetEvent with matching GONetId + TriggerNameHash.
        /// </summary>
        public bool DoesCancelOutOtherEvent(IGONetEvent otherEvent)
        {
            if (otherEvent is AnimatorTriggerFiredEvent firedEvent)
            {
                return firedEvent.GONetId == GONetId && firedEvent.TriggerNameHash == TriggerNameHash;
            }
            else if (otherEvent is AnimatorTriggerResetEvent prevResetEvent)
            {
                // Cancel previous reset event for same trigger (deduplication)
                return prevResetEvent.GONetId == GONetId && prevResetEvent.TriggerNameHash == TriggerNameHash;
            }
            return false;
        }

        #endregion

        [MemoryPackConstructor]
        public AnimatorTriggerResetEvent() { }

        public AnimatorTriggerResetEvent(uint gonetId, int triggerNameHash, ushort sourceAuthorityId, long occurredAtElapsedTicks)
        {
            GONetId = gonetId;
            TriggerNameHash = triggerNameHash;
            SourceAuthorityId = sourceAuthorityId;
            OccurredAtElapsedTicks = occurredAtElapsedTicks;
        }

        public override string ToString()
        {
            return $"[AnimatorTriggerReset] GONetId:{GONetId} TriggerHash:{TriggerNameHash} SourceAuth:{SourceAuthorityId}";
        }
    }

    #endregion
}
