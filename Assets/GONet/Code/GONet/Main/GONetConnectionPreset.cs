/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 * This file provides the GONetConnectionPreset ScriptableObject for saving and loading network connection configurations.
 */
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Represents the role/mode for this GONet instance.
    /// </summary>
    public enum GONetConnectionRole
    {
        /// <summary>Server + Client in same process (most common for single-player testing or player-hosted games)</summary>
        Host,

        /// <summary>Client only (connects to remote server)</summary>
        Client,

        /// <summary>Dedicated server only (no local client)</summary>
        DedicatedServer
    }

    /// <summary>
    /// ScriptableObject that stores network connection configuration for quick testing and saved presets.
    /// Create via: Assets → Create → GONet → Connection Preset
    /// </summary>
    [CreateAssetMenu(fileName = "GONetConnectionPreset", menuName = "GONet/Connection Preset", order = 1)]
    public class GONetConnectionPreset : ScriptableObject
    {
        /// <summary>
        /// Display name for this preset (e.g., "Local Test", "4-Player Test", "Production Server")
        /// </summary>
        [Tooltip("Display name for this preset")]
        public string presetName = "New Preset";

        /// <summary>
        /// Connection role for this preset
        /// </summary>
        [Tooltip("Connection role: Host (Server+Client), Client only, or Dedicated Server")]
        public GONetConnectionRole role = GONetConnectionRole.Host;

        /// <summary>
        /// IP address to connect to (only used when role = Client)
        /// </summary>
        [Tooltip("IP address to connect to (only used in Client mode)")]
        public string ipAddress = "127.0.0.1";

        /// <summary>
        /// Port number for server/client connection
        /// </summary>
        [Tooltip("Port number for network connection")]
        [Range(1024, 65535)]
        public ushort port = 7777;

        /// <summary>
        /// Player name (optional, for game-specific use)
        /// </summary>
        [Tooltip("Player name (optional, for display in UI)")]
        public string playerName = "";

        #region Advanced Settings

        /// <summary>
        /// Maximum number of simultaneous client connections (only used when role = Host or DedicatedServer)
        /// </summary>
        [Header("Advanced Settings")]
        [Tooltip("Maximum number of simultaneous clients (Server/Host only)")]
        [Range(1, 100)]
        public int maxConnections = 16;

        /// <summary>
        /// GONetId batch size for pre-allocated ID ranges (see GONetGlobal.client_GONetIdBatchSize)
        /// </summary>
        [Tooltip("Pre-allocated GONetId ranges for client spawning")]
        [Range(100, 1000)]
        public int gonetIdBatchSize = 200;

        /// <summary>
        /// Network send rate in Hz (updates per second)
        /// </summary>
        [Tooltip("Network send rate (updates per second)")]
        [Range(10, 120)]
        public int networkSendRate = 60;

        /// <summary>
        /// Enable pluggable transport layer (maps to GONetGlobal.usePluggableTransport)
        /// </summary>
        [Tooltip("Enable pluggable transport layer (false = legacy NetcodeIO direct, true = pluggable transport)")]
        public bool usePluggableTransport = true;

        /// <summary>
        /// Transport type to use when pluggable transport is enabled (maps to GONetGlobal.transportType)
        /// </summary>
        [Tooltip("Transport implementation (NetcodeIO or Steamworks)")]
        public GONetTransportType transportType = GONetTransportType.NetcodeIO;

        /// <summary>
        /// Auto-connect immediately on scene load (useful for command-line args)
        /// </summary>
        [Tooltip("Auto-connect immediately on scene load (useful for testing)")]
        public bool autoConnect = false;

        /// <summary>
        /// Additional key-value pairs for custom game-specific settings
        /// </summary>
        [Tooltip("Custom game-specific settings (extensible)")]
        public List<KeyValuePair> customSettings = new List<KeyValuePair>();

        #endregion

        /// <summary>
        /// Simple key-value pair for custom extensible settings
        /// </summary>
        [Serializable]
        public class KeyValuePair
        {
            public string key;
            public string value;
        }

        /// <summary>
        /// Creates a deep copy of this preset
        /// </summary>
        public GONetConnectionPreset Clone()
        {
            GONetConnectionPreset clone = CreateInstance<GONetConnectionPreset>();
            clone.presetName = presetName;
            clone.role = role;
            clone.ipAddress = ipAddress;
            clone.port = port;
            clone.playerName = playerName;
            clone.maxConnections = maxConnections;
            clone.gonetIdBatchSize = gonetIdBatchSize;
            clone.networkSendRate = networkSendRate;
            clone.usePluggableTransport = usePluggableTransport;
            clone.transportType = transportType;
            clone.autoConnect = autoConnect;
            clone.customSettings = new List<KeyValuePair>(customSettings);
            return clone;
        }

        /// <summary>
        /// Copies values from another preset into this one
        /// </summary>
        public void CopyFrom(GONetConnectionPreset other)
        {
            presetName = other.presetName;
            role = other.role;
            ipAddress = other.ipAddress;
            port = other.port;
            playerName = other.playerName;
            maxConnections = other.maxConnections;
            gonetIdBatchSize = other.gonetIdBatchSize;
            networkSendRate = other.networkSendRate;
            usePluggableTransport = other.usePluggableTransport;
            transportType = other.transportType;
            autoConnect = other.autoConnect;
            customSettings = new List<KeyValuePair>(other.customSettings);
        }

        /// <summary>
        /// Returns a user-friendly description of this preset
        /// </summary>
        public override string ToString()
        {
            return $"{presetName} ({role}, {ipAddress}:{port})";
        }
    }
}
