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

namespace GONet.Transport
{
    /// <summary>
    /// Factory for creating transport instances based on the configured transport type.
    ///
    /// <para>
    /// USAGE: Centralizes transport instantiation to avoid hardcoded transport types scattered
    /// throughout the codebase. All code that needs to create a new transport instance should
    /// use this factory instead of directly instantiating concrete transport classes.
    /// </para>
    ///
    /// <para>
    /// EXTENSIBILITY: To add a new transport type:
    /// 1. Add new value to <see cref="GONetTransportType"/> enum
    /// 2. Add case to <see cref="Create"/> method
    /// </para>
    /// </summary>
    public static class GONetTransportFactory
    {
        /// <summary>
        /// Creates a new transport instance based on the configured transport type in GONetGlobal.
        ///
        /// <para>
        /// This is the primary entry point for transport creation. Uses GONetGlobal.Instance.transportType
        /// to determine which transport to instantiate.
        /// </para>
        /// </summary>
        /// <returns>New transport instance of the configured type, or null if GONetGlobal not available</returns>
        public static IGONetTransport Create()
        {
            if (GONetGlobal.Instance == null)
            {
                GONetLog.Warning("[GONetTransportFactory] Cannot create transport - GONetGlobal.Instance is null");
                return null;
            }

            return Create(GONetGlobal.Instance.transportType);
        }

        /// <summary>
        /// Creates a new transport instance of the specified type.
        ///
        /// <para>
        /// Use this overload when you need to create a transport of a specific type
        /// regardless of GONetGlobal configuration (e.g., for testing or special cases).
        /// </para>
        /// </summary>
        /// <param name="transportType">The type of transport to create</param>
        /// <returns>New transport instance</returns>
        public static IGONetTransport Create(GONetTransportType transportType)
        {
            switch (transportType)
            {
                case GONetTransportType.Steamworks:
                    return new SteamworksTransport();

                case GONetTransportType.NetcodeIO:
                default:
                    return new NetcodeIOTransport();
            }
        }

        /// <summary>
        /// Creates and initializes a transport with default GONet credentials.
        ///
        /// <para>
        /// This convenience method combines transport creation with standard GONet initialization,
        /// including private key and protocol ID from GONetMain. Use this for mesh clients,
        /// dormant servers, and other internally-managed connections.
        /// </para>
        /// </summary>
        /// <returns>Initialized transport instance, or null if creation/initialization failed</returns>
        public static IGONetTransport CreateAndInitialize()
        {
            var transport = Create();
            if (transport == null)
            {
                return null;
            }

            var config = GONetTransportConfig.CreateDefault();
            config.PrivateKey = GONetMain._privateKey;
            config.ProtocolID = GONetMain.noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest;
            transport.Initialize(config);

            return transport;
        }

        /// <summary>
        /// Creates and initializes a transport of the specified type with default GONet credentials.
        /// </summary>
        /// <param name="transportType">The type of transport to create</param>
        /// <returns>Initialized transport instance</returns>
        public static IGONetTransport CreateAndInitialize(GONetTransportType transportType)
        {
            var transport = Create(transportType);

            var config = GONetTransportConfig.CreateDefault();
            config.PrivateKey = GONetMain._privateKey;
            config.ProtocolID = GONetMain.noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest;
            transport.Initialize(config);

            return transport;
        }
    }
}
