/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 * This file provides enhanced command-line argument parsing for GONet builds.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Parses command-line arguments for GONet builds and applies them to connection configuration.
    ///
    /// Supported arguments:
    /// - Basic: -server, -client, -host, -ip [address], -port [number]
    /// - Advanced: -maxclients [number], -batchsize [number], -sendrate [number], -name [playerName]
    /// - Testing: -autoconnect, -preset [presetName]
    /// - Network Simulation (Dev/Editor builds only):
    ///   -netsim              Enable network simulation
    ///   -latency [ms]        One-way latency in milliseconds (0-500)
    ///   -jitter [ms]         Latency variance ± milliseconds (0-200)
    ///   -loss [percent]      Packet loss percentage (0-20)
    ///   -duplicate [percent] Packet duplication percentage (0-10)
    ///   -netsim-preset [name] Apply preset: lan, goodwifi, internet, poorwifi, badnetwork
    /// - CPU Throttling (Dev/Editor builds only):
    ///   -target-fps [fps]    Limit frame rate via thread sleep (1-120, simulates CPU load)
    ///   -cpu-throttle [ms]   Add fixed sleep per frame in milliseconds (0-500)
    ///
    /// Examples:
    ///   MyGame.exe -server -port 7777 -maxclients 32
    ///   MyGame.exe -client -ip 192.168.1.100 -port 7777 -autoconnect -name "Player1"
    ///   MyGame.exe -host -port 8888 -batchsize 500
    ///   MyGame.exe -preset "LocalTest"
    ///   MyGame.exe -client -ip 127.0.0.1 -netsim -latency 50 -jitter 10 -loss 2
    ///   MyGame.exe -server -netsim-preset poorwifi
    ///   MyGame.exe -host -target-fps 10                    (Limits to ~10 FPS via sleep)
    ///   MyGame.exe -host -netsim-preset poorwifi -target-fps 15 (Simulates poor WiFi + slow CPU)
    /// </summary>
    public static class GONetCommandLineParser
    {
        private static readonly string LOGCODE = $"[{nameof(GONetCommandLineParser)}] ";

        /// <summary>
        /// Parses command-line arguments and returns a GONetConnectionPreset with the parsed values.
        /// Returns null if no GONet-related arguments found.
        /// </summary>
        public static GONetConnectionPreset ParseCommandLineArgs()
        {
            string[] args = Environment.GetCommandLineArgs();

            if (args == null || args.Length == 0)
            {
                return null;
            }

            // Check if any GONet arguments exist
            if (!HasGONetArguments(args))
            {
                return null;
            }

            GONetLog.Debug($"{LOGCODE}Parsing command-line arguments: {string.Join(" ", args)}");

            GONetConnectionPreset preset = ScriptableObject.CreateInstance<GONetConnectionPreset>();
            preset.presetName = "Command Line";

            Dictionary<string, string> argDict = BuildArgumentDictionary(args);

            // Parse role (mutually exclusive: -server, -client, -host)
            if (argDict.ContainsKey("-server"))
            {
                preset.role = GONetConnectionRole.DedicatedServer;
                GONetLog.Info($"{LOGCODE}Role set to DedicatedServer");
            }
            else if (argDict.ContainsKey("-host"))
            {
                preset.role = GONetConnectionRole.Host;
                GONetLog.Info($"{LOGCODE}Role set to Host");
            }
            else if (argDict.ContainsKey("-client"))
            {
                preset.role = GONetConnectionRole.Client;
                GONetLog.Info($"{LOGCODE}Role set to Client");
            }

            // Parse IP address (client only)
            if (argDict.TryGetValue("-ip", out string ipAddress))
            {
                preset.ipAddress = ipAddress;
                GONetLog.Info($"{LOGCODE}IP address set to {ipAddress}");
            }

            // Parse port
            if (argDict.TryGetValue("-port", out string portStr) && ushort.TryParse(portStr, out ushort port))
            {
                preset.port = port;
                GONetLog.Info($"{LOGCODE}Port set to {port}");
            }

            // Parse player name
            if (argDict.TryGetValue("-name", out string playerName))
            {
                preset.playerName = playerName;
                GONetLog.Info($"{LOGCODE}Player name set to '{playerName}'");
            }

            // Parse max connections
            if (argDict.TryGetValue("-maxclients", out string maxClientsStr) && int.TryParse(maxClientsStr, out int maxClients))
            {
                preset.maxConnections = Mathf.Clamp(maxClients, 1, 100);
                GONetLog.Info($"{LOGCODE}Max connections set to {preset.maxConnections}");
            }

            // Parse GONetId batch size
            if (argDict.TryGetValue("-batchsize", out string batchSizeStr) && int.TryParse(batchSizeStr, out int batchSize))
            {
                preset.gonetIdBatchSize = Mathf.Clamp(batchSize, 100, 1000);
                GONetLog.Info($"{LOGCODE}GONetId batch size set to {preset.gonetIdBatchSize}");
            }

            // Parse network send rate
            if (argDict.TryGetValue("-sendrate", out string sendRateStr) && int.TryParse(sendRateStr, out int sendRate))
            {
                preset.networkSendRate = Mathf.Clamp(sendRate, 10, 120);
                GONetLog.Info($"{LOGCODE}Network send rate set to {preset.networkSendRate} Hz");
            }

            // Parse auto-connect flag
            if (argDict.ContainsKey("-autoconnect"))
            {
                preset.autoConnect = true;
                GONetLog.Info($"{LOGCODE}Auto-connect enabled");
            }

            // Parse preset name (for loading saved preset)
            if (argDict.TryGetValue("-preset", out string presetName))
            {
                // This will be handled by GONetConnectionManager to load actual preset
                preset.presetName = presetName;
                GONetLog.Info($"{LOGCODE}Preset name set to '{presetName}'");
            }

            return preset;
        }

        /// <summary>
        /// Checks if any GONet-related command-line arguments are present
        /// </summary>
        private static bool HasGONetArguments(string[] args)
        {
            string[] gonetArgs = new[] { "-server", "-client", "-host", "-ip", "-port", "-maxclients", "-batchsize", "-sendrate", "-autoconnect", "-preset", "-name",
                                         "-netsim", "-latency", "-jitter", "-loss", "-duplicate", "-netsim-preset",
                                         "-target-fps", "-cpu-throttle" };
            return args.Any(arg => gonetArgs.Contains(arg.ToLower()));
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Parses network simulation command-line arguments and applies them to GONetGlobal.
        /// Call this early in startup (e.g., in Awake or before GONetGlobal.Instance is used).
        ///
        /// Arguments:
        ///   -netsim              Enable network simulation
        ///   -latency [ms]        One-way latency (0-500ms)
        ///   -jitter [ms]         Latency variance ±ms (0-200ms)
        ///   -loss [percent]      Packet loss percentage (0-20%)
        ///   -duplicate [percent] Duplication percentage (0-10%)
        ///   -netsim-preset [name] Apply preset: lan, goodwifi, internet, poorwifi, badnetwork
        ///
        /// Returns true if any network simulation arguments were found and applied.
        /// </summary>
        public static bool ApplyNetworkSimulationArgs()
        {
            if (GONetGlobal.Instance == null)
            {
                GONetLog.Warning($"{LOGCODE}Cannot apply network simulation args - GONetGlobal.Instance is null");
                return false;
            }

            string[] args = Environment.GetCommandLineArgs();
            if (args == null || args.Length == 0) return false;

            Dictionary<string, string> argDict = BuildArgumentDictionary(args);
            bool anyApplied = false;

            // Check for preset first (can be overridden by individual args)
            if (argDict.TryGetValue("-netsim-preset", out string presetName))
            {
                GONet.Transport.NetworkConditionConfig preset = GetNetworkPresetByName(presetName);
                if (preset != null)
                {
                    GONetGlobal.Instance.ApplyNetworkSimulationPreset(preset);
                    GONetLog.Info($"{LOGCODE}Applied network simulation preset: {presetName}");
                    anyApplied = true;
                }
                else
                {
                    GONetLog.Warning($"{LOGCODE}Unknown network simulation preset: {presetName}. Valid presets: lan, goodwifi, internet, poorwifi, badnetwork");
                }
            }

            // Enable simulation if -netsim flag present
            if (argDict.ContainsKey("-netsim"))
            {
                GONetGlobal.Instance.networkSimulation_Enabled = true;
                GONetLog.Info($"{LOGCODE}Network simulation enabled via command line");
                anyApplied = true;
            }

            // Parse individual parameters (override preset values if specified)
            if (argDict.TryGetValue("-latency", out string latencyStr) && int.TryParse(latencyStr, out int latency))
            {
                GONetGlobal.Instance.networkSimulation_Enabled = true;
                GONetGlobal.Instance.networkSimulation_LatencyMs = Mathf.Clamp(latency, 0, 500);
                GONetLog.Info($"{LOGCODE}Network simulation latency set to {GONetGlobal.Instance.networkSimulation_LatencyMs}ms");
                anyApplied = true;
            }

            if (argDict.TryGetValue("-jitter", out string jitterStr) && int.TryParse(jitterStr, out int jitter))
            {
                GONetGlobal.Instance.networkSimulation_Enabled = true;
                GONetGlobal.Instance.networkSimulation_JitterMs = Mathf.Clamp(jitter, 0, 200);
                GONetLog.Info($"{LOGCODE}Network simulation jitter set to ±{GONetGlobal.Instance.networkSimulation_JitterMs}ms");
                anyApplied = true;
            }

            if (argDict.TryGetValue("-loss", out string lossStr) && float.TryParse(lossStr, out float loss))
            {
                GONetGlobal.Instance.networkSimulation_Enabled = true;
                GONetGlobal.Instance.networkSimulation_PacketLossPercent = Mathf.Clamp(loss, 0f, 20f);
                GONetLog.Info($"{LOGCODE}Network simulation packet loss set to {GONetGlobal.Instance.networkSimulation_PacketLossPercent}%");
                anyApplied = true;
            }

            if (argDict.TryGetValue("-duplicate", out string dupStr) && float.TryParse(dupStr, out float dup))
            {
                GONetGlobal.Instance.networkSimulation_Enabled = true;
                GONetGlobal.Instance.networkSimulation_DuplicatePercent = Mathf.Clamp(dup, 0f, 10f);
                GONetLog.Info($"{LOGCODE}Network simulation duplication set to {GONetGlobal.Instance.networkSimulation_DuplicatePercent}%");
                anyApplied = true;
            }

            if (anyApplied && GONetGlobal.Instance.networkSimulation_Enabled)
            {
                GONetLog.Info($"{LOGCODE}Network simulation config: Latency={GONetGlobal.Instance.networkSimulation_LatencyMs}ms, " +
                             $"Jitter=±{GONetGlobal.Instance.networkSimulation_JitterMs}ms, " +
                             $"Loss={GONetGlobal.Instance.networkSimulation_PacketLossPercent}%, " +
                             $"Dup={GONetGlobal.Instance.networkSimulation_DuplicatePercent}%");
            }

            return anyApplied;
        }

        /// <summary>
        /// Gets a network condition preset by name (case-insensitive).
        /// </summary>
        private static GONet.Transport.NetworkConditionConfig GetNetworkPresetByName(string name)
        {
            switch (name?.ToLower())
            {
                case "lan":
                    return GONet.Transport.NetworkConditionConfig.LAN;
                case "goodwifi":
                case "good-wifi":
                case "good_wifi":
                    return GONet.Transport.NetworkConditionConfig.GoodWiFi;
                case "internet":
                    return GONet.Transport.NetworkConditionConfig.Internet;
                case "poorwifi":
                case "poor-wifi":
                case "poor_wifi":
                    return GONet.Transport.NetworkConditionConfig.PoorWiFi;
                case "badnetwork":
                case "bad-network":
                case "bad_network":
                case "bad":
                    return GONet.Transport.NetworkConditionConfig.BadNetwork;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Checks if any network simulation command-line arguments are present.
        /// </summary>
        public static bool HasNetworkSimulationArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args == null)
            {
                GONetLog.Debug($"{LOGCODE}HasNetworkSimulationArgs: args is null");
                return false;
            }

            GONetLog.Debug($"{LOGCODE}HasNetworkSimulationArgs: Checking args: {string.Join(" ", args)}");

            string[] netsimArgs = new[] { "-netsim", "-latency", "-jitter", "-loss", "-duplicate", "-netsim-preset" };
            bool hasArgs = args.Any(arg => netsimArgs.Contains(arg.ToLower()));
            GONetLog.Debug($"{LOGCODE}HasNetworkSimulationArgs: result={hasArgs}");
            return hasArgs;
        }

        // ========================================
        // CPU THROTTLING (for stress testing)
        // ========================================

        /// <summary>
        /// Whether CPU throttling is enabled via command-line.
        /// </summary>
        public static bool IsCpuThrottlingEnabled { get; private set; }

        /// <summary>
        /// Target FPS when CPU throttling is enabled (0 = use fixed sleep instead).
        /// </summary>
        public static int CpuThrottleTargetFps { get; private set; }

        /// <summary>
        /// Fixed sleep time per frame in milliseconds (used when target FPS is not set).
        /// </summary>
        public static int CpuThrottleFixedSleepMs { get; private set; }

        /// <summary>
        /// The actual frame processing time (before any throttle sleep) in seconds.
        /// Used by metrics collector to calculate true CPU headroom when throttling is enabled.
        /// Without this, Time.deltaTime includes sleep time, making throttled clients look healthy.
        /// </summary>
        public static float LastActualFrameTimeSeconds { get; private set; }

        /// <summary>
        /// Parses CPU throttling command-line arguments.
        ///
        /// Arguments:
        ///   -target-fps [fps]    Limit frame rate via thread sleep (1-120)
        ///   -cpu-throttle [ms]   Add fixed sleep per frame (0-500ms)
        ///
        /// IMPORTANT: This provides a predictable, controlled way to simulate slow CPUs
        /// without relying on external tools like BES that may have unpredictable effects.
        ///
        /// Returns true if any CPU throttling arguments were found and applied.
        /// </summary>
        public static bool ApplyCpuThrottlingArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args == null || args.Length == 0) return false;

            Dictionary<string, string> argDict = BuildArgumentDictionary(args);
            bool anyApplied = false;

            // Parse target FPS
            if (argDict.TryGetValue("-target-fps", out string fpsStr) && int.TryParse(fpsStr, out int targetFps))
            {
                CpuThrottleTargetFps = Mathf.Clamp(targetFps, 1, 120);
                IsCpuThrottlingEnabled = true;
                anyApplied = true;
                GONetLog.Info($"{LOGCODE}CPU throttling enabled: target FPS = {CpuThrottleTargetFps} (frame budget = {1000f / CpuThrottleTargetFps:F1}ms)");
            }

            // Parse fixed sleep
            if (argDict.TryGetValue("-cpu-throttle", out string sleepStr) && int.TryParse(sleepStr, out int sleepMs))
            {
                CpuThrottleFixedSleepMs = Mathf.Clamp(sleepMs, 0, 500);
                IsCpuThrottlingEnabled = true;
                anyApplied = true;
                GONetLog.Info($"{LOGCODE}CPU throttling enabled: fixed sleep = {CpuThrottleFixedSleepMs}ms per frame");
            }

            return anyApplied;
        }

        /// <summary>
        /// Call this once per frame (at end of Update or LateUpdate) to apply CPU throttling.
        /// This uses Thread.Sleep to artificially limit frame rate, simulating high CPU load.
        ///
        /// IMPORTANT: This stores the actual processing time (before sleep) in LastActualFrameTimeSeconds
        /// so that the host migration scoring system can calculate true CPU headroom. Without this,
        /// Time.deltaTime includes sleep time, making throttled clients appear healthy when they're not.
        /// </summary>
        /// <param name="frameStartTime">The time when this frame started (from Time.realtimeSinceStartup)</param>
        public static void ApplyFrameThrottling(float frameStartTime)
        {
            if (!IsCpuThrottlingEnabled) return;

            // Calculate how long this frame ACTUALLY took (before sleep)
            // This is the TRUE processing time that reflects CPU capacity
            float actualFrameTime = UnityEngine.Time.realtimeSinceStartup - frameStartTime;
            LastActualFrameTimeSeconds = actualFrameTime;

            int sleepMs = 0;

            if (CpuThrottleTargetFps > 0)
            {
                float targetFrameTime = 1f / CpuThrottleTargetFps;
                float remainingTime = targetFrameTime - actualFrameTime;

                if (remainingTime > 0.001f) // At least 1ms to sleep
                {
                    sleepMs = (int)(remainingTime * 1000f);
                }
            }
            else if (CpuThrottleFixedSleepMs > 0)
            {
                sleepMs = CpuThrottleFixedSleepMs;
            }

            if (sleepMs > 0)
            {
                System.Threading.Thread.Sleep(sleepMs);
            }
        }

        /// <summary>
        /// Checks if any CPU throttling command-line arguments are present.
        /// </summary>
        public static bool HasCpuThrottlingArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args == null) return false;

            string[] cpuArgs = new[] { "-target-fps", "-cpu-throttle" };
            return args.Any(arg => cpuArgs.Contains(arg.ToLower()));
        }
#endif

        /// <summary>
        /// Builds a dictionary of command-line arguments.
        /// Format: "-key value" becomes { "-key": "value" }
        /// Flags without values (e.g., "-autoconnect") become { "-autoconnect": "" }
        /// </summary>
        private static Dictionary<string, string> BuildArgumentDictionary(string[] args)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLower();

                if (arg.StartsWith("-"))
                {
                    // Check if next arg is a value (doesn't start with -)
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        dict[arg] = args[i + 1];
                        i++; // Skip next arg since we consumed it
                    }
                    else
                    {
                        // Flag without value
                        dict[arg] = "";
                    }
                }
            }

            return dict;
        }

        /// <summary>
        /// Generates a batch file for launching multiple instances with different configurations.
        /// Useful for rapid testing.
        /// </summary>
        public static string GenerateLaunchScript(string executablePath, GONetConnectionPreset serverPreset, List<GONetConnectionPreset> clientPresets)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("REM GONet Multi-Instance Launch Script");
            sb.AppendLine("REM Generated by GONetCommandLineParser");
            sb.AppendLine();

            // Launch server
            if (serverPreset != null)
            {
                sb.Append($"start \"GONet Server\" \"{executablePath}\" ");
                sb.Append(GetCommandLineArgs(serverPreset));
                sb.AppendLine();
                sb.AppendLine("timeout /t 2 /nobreak >nul");
                sb.AppendLine();
            }

            // Launch clients
            if (clientPresets != null)
            {
                for (int i = 0; i < clientPresets.Count; i++)
                {
                    sb.Append($"start \"GONet Client {i + 1}\" \"{executablePath}\" ");
                    sb.Append(GetCommandLineArgs(clientPresets[i]));
                    sb.AppendLine();
                    sb.AppendLine("timeout /t 1 /nobreak >nul");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("echo All instances launched!");
            return sb.ToString();
        }

        /// <summary>
        /// Converts a GONetConnectionPreset back into command-line argument string
        /// </summary>
        public static string GetCommandLineArgs(GONetConnectionPreset preset)
        {
            if (preset == null)
            {
                return "";
            }

            List<string> args = new List<string>();

            // Role
            switch (preset.role)
            {
                case GONetConnectionRole.Host:
                    args.Add("-host");
                    break;
                case GONetConnectionRole.Client:
                    args.Add("-client");
                    break;
                case GONetConnectionRole.DedicatedServer:
                    args.Add("-server");
                    break;
            }

            // IP address (client only)
            if (preset.role == GONetConnectionRole.Client && !string.IsNullOrEmpty(preset.ipAddress))
            {
                args.Add($"-ip {preset.ipAddress}");
            }

            // Port
            args.Add($"-port {preset.port}");

            // Player name
            if (!string.IsNullOrEmpty(preset.playerName))
            {
                args.Add($"-name \"{preset.playerName}\"");
            }

            // Advanced settings
            if (preset.maxConnections != 16)
            {
                args.Add($"-maxclients {preset.maxConnections}");
            }

            if (preset.gonetIdBatchSize != 200)
            {
                args.Add($"-batchsize {preset.gonetIdBatchSize}");
            }

            if (preset.networkSendRate != 60)
            {
                args.Add($"-sendrate {preset.networkSendRate}");
            }

            if (preset.autoConnect)
            {
                args.Add("-autoconnect");
            }

            return string.Join(" ", args);
        }
    }
}
