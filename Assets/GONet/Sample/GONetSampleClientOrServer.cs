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

using GONet;
using GONet.Transport;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using NetcodeIO.NET;

#if USING_AMAZON_GAMELIFT
using Aws.GameLift.Server;
#endif

public class GONetSampleClientOrServer : MonoBehaviour
{
    public bool isServer = true;

    /// <summary>
    /// When true, runs as both server and client (listen server / client-host).
    /// This is automatically set when GONetConnectionRole.Host is used.
    /// </summary>
    public bool isHost = false;

    public bool isUsingAWSGameLift =
#if USING_AMAZON_GAMELIFT
        true;
#else
        false;
#endif


#if USING_AMAZON_GAMELIFT
    volatile bool hasUnprocessedAWSGameLiftEvent_onStartGameSession = false;
#endif

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        // Only set framerate cap if not already configured by the game
        // Default is -1 (uncapped), which causes high CPU usage
        if (Application.targetFrameRate == -1)
        {
            Application.targetFrameRate = 128;
        }

        // Determine server/client/host role using same logic as GONetGlobal auto-detection
        DetermineRole(out bool shouldBeServer, out bool shouldBeHost);

        if (shouldBeHost)
        {
            // Host mode: Start both server and client (listen server)
            InitHost();
        }
        else if (shouldBeServer)
        {
            InitServer();
        }
        else
        {
            InitClient();
        }
    }

    /// <summary>
    /// Determines whether this instance should start as server, client, or host (listen server).
    /// Uses the same logic as GONetGlobal's auto-detection system.
    ///
    /// Priority order:
    /// 1. Command-line arguments (-server, -client, or -host) always override
    /// 2. Port availability check (if auto-detection enabled)
    /// 3. Inspector isServer/isHost fields (fallback)
    /// </summary>
    private void DetermineRole(out bool shouldBeServer, out bool shouldBeHost)
    {
        shouldBeServer = false;
        shouldBeHost = false;

        // Priority 1: Command-line arguments always win
        string[] args = System.Environment.GetCommandLineArgs();
        if (args.Contains("-host"))
        {
            GONetLog.Info("[GONetSampleClientOrServer] Command line: -host detected → Starting as HOST (Server + Client)");
            shouldBeHost = true;
            shouldBeServer = false; // Will be handled by host init
            return;
        }
        if (args.Contains("-server"))
        {
            GONetLog.Info("[GONetSampleClientOrServer] Command line: -server detected → Starting as DEDICATED SERVER");
            shouldBeServer = true;
            shouldBeHost = false;
            return;
        }
        if (args.Contains("-client"))
        {
            GONetLog.Info("[GONetSampleClientOrServer] Command line: -client detected → Starting as CLIENT");
            shouldBeServer = false;
            shouldBeHost = false;
            return;
        }

        // Priority 2: Auto-detection based on port availability (if enabled)
        if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableAutoRoleDetection)
        {
            string serverIP = GONetGlobal.ServerIPAddress_Actual;
            int serverPort = GONetGlobal.ServerPort_Actual;

            bool isServerRemote = !GONet.Utils.NetworkUtils.IsIPAddressOnLocalMachine(serverIP);
            bool isPortOccupied = GONet.Utils.NetworkUtils.IsLocalPortListening(serverPort);

            if (isServerRemote || isPortOccupied)
            {
                // Server is running remotely or port is occupied locally → start as client
                GONetLog.Info($"[GONetSampleClientOrServer] Auto-detection: Server at {serverIP}:{serverPort} is {(isServerRemote ? "remote" : "already running")} → Starting as CLIENT");
                shouldBeServer = false;
                shouldBeHost = false;
                return;
            }
            else
            {
                // Port is free → start as server (or host if configured)
                GONetLog.Info($"[GONetSampleClientOrServer] Auto-detection: Port {serverPort} is FREE → Starting as {(isHost ? "HOST" : "SERVER")}");
                shouldBeServer = !isHost;
                shouldBeHost = isHost;
                return;
            }
        }

        // Priority 3: Fallback to inspector settings
        shouldBeHost = isHost;
        shouldBeServer = !isHost && isServer;
        string roleString = shouldBeHost ? "HOST (Server + Client)" : (shouldBeServer ? "DEDICATED SERVER" : "CLIENT");
        GONetLog.Info($"[GONetSampleClientOrServer] Auto-detection disabled, using inspector setting: isHost={isHost}, isServer={isServer} → Starting as {roleString}");
    }

    private void InitClient()
    {
        if (isUsingAWSGameLift)
        {
            InitClient_AWSGameLift();
        }
        else
        {
            OnReadyToStartGONet();
        }
    }

    private void InitClient_AWSGameLift()
    {
#if USING_AMAZON_GAMELIFT
        GameLift gameLift = new GameLift();
        gameLift.Awake();
        gameLift.Start();

        string auth = null;
        gameLift.GetConnectionInfo(ref GONetGlobal.serverIPAddress_Actual, ref GONetGlobal.serverPort_Actual, ref auth);

        { // messy and looks duplicative, but ensures event gets fired in there:
            GONetGlobal.ServerIPAddress_Actual = GONetGlobal.serverIPAddress_Actual;
            GONetGlobal.ServerPort_Actual = GONetGlobal.serverPort_Actual;
        }

        OnReadyToStartGONet();
#endif
    }

    private void InitServer()
    {
        if (isUsingAWSGameLift)
        {
            InitServer_AWSGameLift();
        }
        else
        {
            OnReadyToStartGONet();
        }
    }

    private void InitServer_AWSGameLift()
    {
#if USING_AMAZON_GAMELIFT
        //Identify port number (hard coded here for simplicity) the game server is listening on for player connections
        var listeningPort = GONetGlobal.ServerPort_Default;

        //InitSDK will establish a local connection with GameLift's agent to enable further communication.
        var initSDKOutcome = GameLiftServerAPI.InitSDK();
        if (initSDKOutcome.Success)
        {
            ProcessParameters processParameters = new ProcessParameters(
                (gameSession) =>
                {
                    //When a game session is created, GameLift sends an activation request to the game server and passes along the game session object containing game properties and other settings.
                    //Here is where a game server should take action based on the game session object.
                    //Once the game server is ready to receive incoming player connections, it should invoke GameLiftServerAPI.ActivateGameSession()

                    GONetGlobal.ServerIPAddress_Actual = gameSession.IpAddress;
                    GONetGlobal.ServerPort_Actual = gameSession.Port;
                    hasUnprocessedAWSGameLiftEvent_onStartGameSession = true;
                },
                (updateGameSession) =>
                {
                    //When a game session is updated (e.g. by FlexMatch backfill), GameLiftsends a request to the game
                    //server containing the updated game session object.  The game server can then examine the provided
                    //matchmakerData and handle new incoming players appropriately.
                    //updateReason is the reason this update is being supplied.
                },
                () =>
                {
                    //OnProcessTerminate callback. GameLift will invoke this callback before shutting down an instance hosting this game server.
                    //It gives this game server a chance to save its state, communicate with services, etc., before being shut down.
                    //In this case, we simply tell GameLift we are indeed going to shutdown.
                    GameLiftServerAPI.ProcessEnding();
                },
                () =>
                {
                    //This is the HealthCheck callback.
                    //GameLift will invoke this callback every 60 seconds or so.
                    //Here, a game server might want to check the health of dependencies and such.
                    //Simply return true if healthy, false otherwise.
                    //The game server has 60 seconds to respond with its health status. GameLift will default to 'false' if the game server doesn't respond in time.
                    //In this case, we're always healthy!
                    return true;
                },
                listeningPort, //This game server tells GameLift that it will listen on port 7777 for incoming player connections.
                new LogParameters(new List<string>()
                    {
                        //Here, the game server tells GameLift what set of files to upload when the game session ends.
                        //GameLift will upload everything specified here for the developers to fetch later.
                        "/local/game/logs/myserver.log"
                    })
            );

            //Calling ProcessReady tells GameLift this game server is ready to receive incoming game sessions!
            var processReadyOutcome = GameLiftServerAPI.ProcessReady(processParameters);
            if (processReadyOutcome.Success)
            {
                GONetLog.Info("ProcessReady success.");
            }
            else
            {
                GONetLog.Error("ProcessReady failure : " + processReadyOutcome.Error.ToString());
            }
        }
        else
        {
            GONetLog.Error("InitSDK failure : " + initSDKOutcome.Error.ToString());
        }
#endif
    }

    /// <summary>
    /// Initializes host mode (listen server): starts both server and client in same process.
    /// This is the industry-standard approach for player-hosted games.
    ///
    /// Implementation:
    /// 1. Start server on configured port
    /// 2. Start client connecting to localhost
    /// 3. Mark client with ListenServer flag for optimization
    /// </summary>
    private void InitHost()
    {
        Application.targetFrameRate = 128; // Same as dedicated server

        GONetLog.Info("[GONetSampleClientOrServer] Initializing HOST mode (Server + Client)...");

        // NOTE: AWS GameLift not supported for host mode (would need additional setup)
        // For now, host mode only works with standard networking
        if (isUsingAWSGameLift)
        {
            GONetLog.Error("[GONetSampleClientOrServer] Host mode does not support AWS GameLift. Use dedicated server instead.");
            return;
        }

        // Call shared initialization (creates transport, starts server AND client)
        OnReadyToStartGONet_Host();
    }

    private void Update()
    {
#if USING_AMAZON_GAMELIFT
        if (hasUnprocessedAWSGameLiftEvent_onStartGameSession)
        {
            hasUnprocessedAWSGameLiftEvent_onStartGameSession = false;

            OnReadyToStartGONet(); // IMPORTANT: need to make sure this runs on main unity thread, which is why we are using hasUnprocessedAWSGameLiftEvent_onStartGameSession

            GameLiftServerAPI.ActivateGameSession();
        }
#endif
    }

    void OnApplicationQuit()
    {
#if USING_AMAZON_GAMELIFT
        if (isServer && isUsingAWSGameLift)
        {
            //Make sure to call GameLiftServerAPI.Destroy() when the application quits. This resets the local connection with GameLift's agent.
            GameLiftServerAPI.Destroy();
        }
#endif
    }

    private void OnReadyToStartGONet()
    {
        // Create transport based on GONetGlobal settings
        IGONetTransport transport = null;

        if (GONetGlobal.Instance.usePluggableTransport)
        {
            switch (GONetGlobal.Instance.transportType)
            {
                case GONetTransportType.NetcodeIO:
                    GONetLog.Info("[GONetSampleClientOrServer] Using NetcodeIO transport (IP/port based)");
                    transport = new NetcodeIOTransport();
                    break;

                case GONetTransportType.Steamworks:
                    GONetLog.Info("[GONetSampleClientOrServer] Using Steamworks transport (supports DirectIP and Steam P2P)");

                    // IMPORTANT: Steamworks requires GONetSteamManager to initialize SteamAPI
                    // Auto-create it if it doesn't exist
                    EnsureGONetSteamManagerExists();

                    // Create transport immediately - SteamworksTransport handles SDR fallback internally
                    // If CreateListenSocketP2P fails (SDR not ready), it automatically falls back to DirectIP
                    transport = new SteamworksTransport();
                    break;

                default:
                    GONetLog.Warning($"[GONetSampleClientOrServer] Unknown transport type: {GONetGlobal.Instance.transportType}, falling back to NetcodeIO");
                    transport = new NetcodeIOTransport();
                    break;
            }

            // Wrap transport with network condition simulation if enabled (Editor/Dev builds only)
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            transport = GONetGlobal.Instance.WrapTransportWithSimulation(transport);
#endif

            // Initialize transport with config using GONetMain's shared credentials
            // CRITICAL: Both client and server must use same PrivateKey and ProtocolID for NetcodeIO authentication
            var config = GONetTransportConfig.CreateDefault();
            config.PrivateKey = GONet.GONetMain._privateKey;  // Use shared static key (not random!)
            config.ProtocolID = GONet.GONetMain.noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest;  // Use shared protocol ID
            transport.Initialize(config);
            GONetLog.Info($"[GONetSampleClientOrServer] Transport initialized: {transport.GetType().Name} with shared credentials (ProtocolID: 0x{config.ProtocolID:X})");
        }
        else
        {
            GONetLog.Info("[GONetSampleClientOrServer] Pluggable transport disabled - using legacy NetcodeIO directly");
        }

        if (isServer)
        {
            int maxConnections = GONetGlobal.Instance != null ? GONetGlobal.Instance.maxConnections : 16;
            GONetLog.Info($"[GONetSampleClientOrServer] Creating GONetServer with maxConnections={maxConnections} (from GONetGlobal.Instance.maxConnections)");
            GONetMain.gonetServer = new GONetServer(maxConnections, GONetGlobal.ServerPort_Actual, transport);

            // When using pluggable transport, Start() requires port parameter
            bool started = transport != null
                ? GONetMain.gonetServer.Start(GONetGlobal.ServerPort_Actual)
                : GONetMain.gonetServer.Start();

            if (started)
            {
                GONetLog.Info($"[GONetSampleClientOrServer] Server started successfully on port {GONetGlobal.ServerPort_Actual}");
            }
            else
            {
                GONetLog.Error($"[GONetSampleClientOrServer] Failed to start server on port {GONetGlobal.ServerPort_Actual}");
            }
        }
        else
        {
            GONetMain.GONetClient = new GONetClient(transport);
            GONetMain.GONetClient.ConnectToServer(GONetGlobal.ServerIPAddress_Actual, GONetGlobal.ServerPort_Actual, 30);
            GONetLog.Info($"[GONetSampleClientOrServer] Client connecting to {GONetGlobal.ServerIPAddress_Actual}:{GONetGlobal.ServerPort_Actual}");
        }
    }

    /// <summary>
    /// Host-specific initialization: starts both server and client in same process (listen server).
    /// Industry-standard approach for player-hosted multiplayer games.
    ///
    /// Implementation:
    /// 1. Create shared transport
    /// 2. Start server on configured port
    /// 3. Start client connecting to localhost (127.0.0.1)
    /// 4. Mark client with ListenServer flag for optimization
    /// </summary>
    private void OnReadyToStartGONet_Host()
    {
        // Create transport based on GONetGlobal settings (same as dedicated server/client)
        IGONetTransport transport = null;

        if (GONetGlobal.Instance.usePluggableTransport)
        {
            switch (GONetGlobal.Instance.transportType)
            {
                case GONetTransportType.NetcodeIO:
                    GONetLog.Info("[GONetSampleClientOrServer] HOST: Using NetcodeIO transport (IP/port based)");
                    transport = new NetcodeIOTransport();
                    break;

                case GONetTransportType.Steamworks:
                    GONetLog.Info("[GONetSampleClientOrServer] HOST: Using Steamworks transport (supports DirectIP and Steam P2P)");
                    EnsureGONetSteamManagerExists();
                    transport = new SteamworksTransport();
                    break;

                default:
                    GONetLog.Warning($"[GONetSampleClientOrServer] HOST: Unknown transport type: {GONetGlobal.Instance.transportType}, falling back to NetcodeIO");
                    transport = new NetcodeIOTransport();
                    break;
            }

            // Initialize transport with shared credentials
            var config = GONetTransportConfig.CreateDefault();
            config.PrivateKey = GONet.GONetMain._privateKey;
            config.ProtocolID = GONet.GONetMain.noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest;
            transport.Initialize(config);
            GONetLog.Info($"[GONetSampleClientOrServer] HOST: Transport initialized: {transport.GetType().Name}");
        }
        else
        {
            GONetLog.Info("[GONetSampleClientOrServer] HOST: Pluggable transport disabled - using legacy NetcodeIO directly");
        }

        // PHASE I: Start server first
        int maxConnections = GONetGlobal.Instance != null ? GONetGlobal.Instance.maxConnections : 16;
        int serverPort = GONetGlobal.ServerPort_Actual;

        GONetLog.Info($"[GONetSampleClientOrServer] HOST: Creating GONetServer with maxConnections={maxConnections}, port={serverPort}");
        GONetMain.gonetServer = new GONetServer(maxConnections, serverPort, transport);

        bool serverStarted = transport != null
            ? GONetMain.gonetServer.Start(serverPort)
            : GONetMain.gonetServer.Start();

        if (!serverStarted)
        {
            GONetLog.Error($"[GONetSampleClientOrServer] HOST: Failed to start server on port {serverPort}. Aborting host initialization.");
            return;
        }

        GONetLog.Info($"[GONetSampleClientOrServer] HOST: Server started successfully on port {serverPort}");

        // PHASE I: Start client connecting to localhost
        // IMPORTANT: Client connects to 127.0.0.1 (loopback) since server is local
        const string LOCALHOST = "127.0.0.1";

        GONetLog.Info($"[GONetSampleClientOrServer] HOST: Creating GONetClient connecting to {LOCALHOST}:{serverPort}");
        GONetMain.GONetClient = new GONetClient(transport);

        // PHASE II PREP: Mark client as listen server (for loopback optimization in Phase II)
        // This flag will be used to detect host player and use GONetConnection_ClientHostLoopback
        GONetMain.GONetClient.ClientTypeFlags = ClientTypeFlags.ListenServer;
        GONetLog.Info($"[GONetSampleClientOrServer] HOST: Set ClientTypeFlags.ListenServer for loopback optimization");

        // Connect to localhost
        GONetMain.GONetClient.ConnectToServer(LOCALHOST, serverPort, timeoutSeconds: 30);
        GONetLog.Info($"[GONetSampleClientOrServer] HOST: Client connecting to localhost:{serverPort}");

        GONetLog.Info("[GONetSampleClientOrServer] HOST: Initialization complete - Server and Client both starting");
    }

    /// <summary>
    /// Ensures GONetSteamManager exists in the scene to handle Steamworks API initialization.
    /// If not found, creates it automatically on the GONetGlobal GameObject (DontDestroyOnLoad).
    /// </summary>
    private void EnsureGONetSteamManagerExists()
    {
        // Check if GONetSteamManager already exists
        var existingManager = FindObjectOfType<GONet.GONetSteamManager>();
        if (existingManager != null)
        {
            GONetLog.Info("[GONetSampleClientOrServer] GONetSteamManager already exists, using existing instance.");
            return;
        }

        // Create GONetSteamManager on GONetGlobal GameObject (DontDestroyOnLoad)
        var gonetGlobal = GONetGlobal.Instance;
        if (gonetGlobal != null)
        {
            var steamManager = gonetGlobal.gameObject.AddComponent<GONet.GONetSteamManager>();
            GONetLog.Info("[GONetSampleClientOrServer] GONetSteamManager created automatically on GONetGlobal GameObject.");
        }
        else
        {
            // Fallback: Create on this GameObject (less ideal, but works)
            var steamManager = gameObject.AddComponent<GONet.GONetSteamManager>();
            GONetLog.Warning("[GONetSampleClientOrServer] GONetSteamManager created on GONetSampleClientOrServer GameObject (GONetGlobal not found).");
        }
    }

    /// <summary>
    /// Coroutine that waits for Steam Datagram Relay to be ready, then initializes Steamworks transport.
    ///
    /// PROBLEM: CreateListenSocketP2P fails if called while SDR is in "Waiting" state.
    /// SOLUTION: Poll SDR status every frame until it reaches "Current" (ready) or times out.
    /// </summary>
    private System.Collections.IEnumerator WaitForSteamNetworkingThenStart()
    {
        GONetLog.Info("[GONetSampleClientOrServer] Waiting for Steam Datagram Relay to initialize...");

        const float TIMEOUT_SECONDS = 10f;
        float elapsedTime = 0f;

        while (elapsedTime < TIMEOUT_SECONDS)
        {
            // Check SDR availability status
            var availability = Steamworks.SteamNetworkingUtils.GetRelayNetworkStatus(out Steamworks.SteamRelayNetworkStatus_t status);

            if (availability == Steamworks.ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
            {
                // SDR is ready!
                GONetLog.Info($"[GONetSampleClientOrServer] Steam Datagram Relay ready after {elapsedTime:F2}s. Starting transport...");

                // Now it's safe to create the Steamworks transport
                IGONetTransport transport = new SteamworksTransport();

                // Initialize transport with shared credentials
                var config = GONetTransportConfig.CreateDefault();
                config.PrivateKey = GONet.GONetMain._privateKey;
                config.ProtocolID = GONet.GONetMain.noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest;
                transport.Initialize(config);
                GONetLog.Info($"[GONetSampleClientOrServer] Transport initialized: {transport.GetType().Name} with shared credentials (ProtocolID: 0x{config.ProtocolID:X})");

                // Start server or client
                if (isServer)
                {
                    int maxConnections = GONetGlobal.Instance != null ? GONetGlobal.Instance.maxConnections : 16;
                    GONetLog.Info($"[GONetSampleClientOrServer] Creating GONetServer with maxConnections={maxConnections} (from GONetGlobal.Instance.maxConnections)");
                    GONetMain.gonetServer = new GONetServer(maxConnections, GONetGlobal.ServerPort_Actual, transport);

                    bool started = GONetMain.gonetServer.Start(GONetGlobal.ServerPort_Actual);

                    if (started)
                    {
                        GONetLog.Info($"[GONetSampleClientOrServer] Server started successfully on port {GONetGlobal.ServerPort_Actual}");
                    }
                    else
                    {
                        GONetLog.Error($"[GONetSampleClientOrServer] Failed to start server on port {GONetGlobal.ServerPort_Actual}");
                    }
                }
                else
                {
                    GONetMain.GONetClient = new GONetClient(transport);
                    GONetMain.GONetClient.ConnectToServer(GONetGlobal.ServerIPAddress_Actual, GONetGlobal.ServerPort_Actual, 30);
                    GONetLog.Info($"[GONetSampleClientOrServer] Client connecting to {GONetGlobal.ServerIPAddress_Actual}:{GONetGlobal.ServerPort_Actual}");
                }

                yield break; // Done!
            }

            // Log status while waiting
            if (elapsedTime == 0f || (int)elapsedTime % 2 == 0) // Log every 2 seconds
            {
                GONetLog.Info($"[GONetSampleClientOrServer] SDR Status: {availability} (waiting... {elapsedTime:F1}s / {TIMEOUT_SECONDS}s)");
            }

            yield return null; // Wait one frame
            elapsedTime += UnityEngine.Time.deltaTime;
        }

        // Timeout - SDR never became ready
        GONetLog.Error($"[GONetSampleClientOrServer] TIMEOUT: Steam Datagram Relay failed to initialize after {TIMEOUT_SECONDS}s.");
        GONetLog.Error("[GONetSampleClientOrServer] Possible causes:");
        GONetLog.Error("  1. Steam client not running or not logged in");
        GONetLog.Error("  2. No internet connection (SDR requires internet)");
        GONetLog.Error("  3. Firewall blocking Steam networking");
        GONetLog.Error("  4. Spacewar App ID 480 SDR restrictions");
    }
}
