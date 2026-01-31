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

using GONet.Core;
using GONet.Generation;
using GONet.Utils;
using GONet.Transport;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GONet
{
    /// <summary>
    /// Transport implementation to use for network communication.
    /// </summary>
    public enum GONetTransportType
    {
        /// <summary>
        /// NetcodeIO transport - IP address/port based, suitable for LAN and dedicated servers.
        /// </summary>
        NetcodeIO = 0,

        /// <summary>
        /// Steamworks transport - Steam ID based with NAT traversal via Steam Datagram Relay.
        /// </summary>
        Steamworks = 1
    }

    /// <summary>
    /// Very important, in fact required, that this get added to one and only one <see cref="GameObject"/> in the first scene loaded in your game.
    /// This is where all the links into Unity life cycle stuffs start for GONet at large.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    [RequireComponent(typeof(GONetParticipant))]
    [RequireComponent(typeof(GONetSessionContext))] // NOTE: requiring GONetSessionContext will thereby get the DontDestroyOnLoad behavior
    public sealed class GONetGlobal : GONetParticipantCompanionBehaviour
    {
        /// <summary>
        /// Singleton instance to prevent duplicate GONetGlobal instances across scenes.
        /// </summary>
        private static GONetGlobal instance;

        /// <summary>
        /// Public accessor for the singleton instance.
        /// Use this instead of FindObjectOfType to ensure you get the persistent instance, not a duplicate that's about to be destroyed.
        /// </summary>
        public static GONetGlobal Instance => instance;

        /// <summary>
        /// High-resolution timestamp when GONetGlobal.Awake() was called.
        /// This represents the START of scene load (first object to Awake in the scene).
        /// All scene-defined objects will have awakeTimeTicks close to this value.
        /// Runtime-spawned objects will have awakeTimeTicks significantly AFTER this.
        /// </summary>
        private static long gonetGlobalAwakeTicks = -1;

        /// <summary>
        /// Tracks when each scene started loading (high-resolution ticks).
        /// Used to distinguish scene-defined objects (created during scene load) vs
        /// runtime-spawned objects (created after scene finished loading).
        /// Key: Scene.name, Value: HighResolutionTimeUtils.UtcNowTicks when scene started loading (GONetGlobal.Awake time)
        /// </summary>
        private static readonly Dictionary<string, long> sceneLoadTimesTicks = new Dictionary<string, long>();

        /// <summary>
        /// Clears all static session state for session reset (Fast Iteration Mode or lobby flow).
        /// Called by GONetMain.ResetForNewSession().
        /// </summary>
        internal static void ClearSessionState()
        {
            sceneLoadTimesTicks.Clear();
            gonetGlobalAwakeTicks = -1;
            GONetLog.Debug("[GONetGlobal] Session state cleared for reset.");
        }

        #region TODO this should be configurable/set elsewhere potentially AFTER loading up and depending on other factors like match making etc...

        //public string serverIP;

        //public int serverPort;

        [Tooltip("***IMPORTANT: When Awake() is called, this value will be locked in place, whereas any adjustments at runtime will yield nothing.\nWhen a Sync Settings Profile or [GONetAutoMagicalSync] setting for " + nameof(GONetAutoMagicalSyncSettings_ProfileTemplate.ShouldBlendBetweenValuesReceived) + " is set to true, this value is used throughout GONet for the length of time in milliseconds to buffer up received sync values from other machines in the network before applying the data locally.\n*When 0, everything will have to be predicted client-side (e.g., extrapolation) since the data received is always old.\n*Non-zero positive values will yield much more accurate (although out of date) data assuming the buffer lead time is large enough to account for lag (network/processing).")]
        [Range(0, 1000)]
        public int valueBlendingBufferLeadTimeMilliseconds = (int)TimeSpan.FromSeconds(GONetMain.BLENDING_BUFFER_LEAD_SECONDS_DEFAULT).TotalMilliseconds;

        [Header("Adaptive Value Blending Buffer")]
        [Tooltip("Enable adaptive buffer lead time based on actual incoming update rate.\n\n" +
                "CRITICAL for variable-rate scenarios (backpressure trickle mode).\n" +
                "When enabled, buffer = max(minBuffer, incomingInterval * 1.5)\n\n" +
                "Without this, when trickle slows to 2Hz (500ms), client plays through fixed 100ms buffer\n" +
                "in 0.1s, then has 0.4s silence → stutter/teleport.\n\n" +
                "When disabled, uses fixed 'valueBlendingBufferLeadTimeMilliseconds'.\n" +
                "Default: TRUE (smart-capped to actual queue contents)")]
        public bool enableAdaptiveBlendingBuffer = true;

        [Tooltip("Minimum buffer lead time (ms). Used during high update rates.\n\n" +
                "This is the floor for adaptive buffer sizing.\n" +
                "Typical 24Hz sync = ~42ms intervals → buffer stays near minimum.\n\n" +
                "Default: 100ms\n" +
                "Range: 50-300ms")]
        [Range(50, 300)]
        public int adaptiveBufferMinLeadTimeMs = 100;

        [Tooltip("Maximum buffer lead time (ms). Caps adaptation during extreme throttling.\n\n" +
                "This is the ceiling for adaptive buffer sizing.\n" +
                "CRITICAL: Must accommodate worst-case trickle rate (500ms at maxTrickleIntervalMs).\n" +
                "Formula: maxBuffer = maxTrickleInterval * 1.5 = 750ms ensures smooth interpolation.\n\n" +
                "WHY 750ms (December 2025 Production Default):\n" +
                "• During backpressure, trickle can slow to 500ms (2Hz)\n" +
                "• Buffer needs 1.5x incoming interval for smooth interpolation\n" +
                "• 500ms * 1.5 = 750ms prevents stutter at extreme throttle\n\n" +
                "QUEUE CAPACITY:\n" +
                "• Default queue is 10 entries (GONetAutoMagicalSyncSettings)\n" +
                "• At 2Hz trickle (500ms), queue holds 5 seconds of history\n" +
                "• 750ms buffer is well within queue capacity at any rate\n\n" +
                "Default: 750ms (matches 2Hz trickle worst case)\n" +
                "Range: 200-1500ms")]
        [Range(200, 1500)]
        public int adaptiveBufferMaxLeadTimeMs = 750;

        [Tooltip("How quickly buffer EXPANDS when conditions worsen (update rate slows).\n\n" +
                "EMA alpha for expansion. Higher = faster reaction to throttling.\n" +
                "When backpressure kicks in, you want fast expansion to prevent stutter.\n\n" +
                "Default: 0.4 (fairly aggressive)\n" +
                "Range: 0.1-0.8")]
        [Range(0.1f, 0.8f)]
        public float adaptiveBufferExpandSpeed = 0.4f;

        [Tooltip("How quickly buffer SHRINKS when conditions improve (update rate increases).\n\n" +
                "EMA alpha for shrinking. Lower = conservative (prevents oscillation).\n" +
                "No rush to reduce buffer when conditions improve.\n\n" +
                "Default: 0.1 (conservative)\n" +
                "Range: 0.05-0.3")]
        [Range(0.05f, 0.3f)]
        public float adaptiveBufferShrinkSpeed = 0.1f;

        #endregion

        [Tooltip("GONet requires GONetGlobal to have a prefab for GONetLocal set here.  Each machine in the network game will instantiate one instance of this prefab.")]
        [SerializeField]
        internal GONetLocal gonetLocalPrefab;

        [Tooltip("Enable automatic client/server role detection based on port availability.\n\n" +
                "When enabled:\n" +
                "• First instance (port free) → Starts as SERVER\n" +
                "• Additional instances (port occupied) → Start as CLIENTS\n" +
                "• Command line args (-server/-client) always override auto-detection\n\n" +
                "This is ideal for local development and testing, eliminating manual role selection.\n\n" +
                "When disabled:\n" +
                "• You must explicitly specify -server or -client via command line\n" +
                "• Or use keyboard shortcuts (Ctrl+Alt+S for server, Ctrl+Alt+C for client)\n\n" +
                "Default: Enabled (recommended for development)")]
        public bool enableAutoRoleDetection = true;

        [Tooltip("Number of GONetIds allocated per batch for client-spawned objects.\n\n" +
                "IMPORTANT: Limbo mode only triggers when client exhausts ALL batch IDs (RARE edge case).\n\n" +
                "• Higher values (500-1000): Better for rapid spawning scenarios (100+ spawns/sec)\n" +
                "• Lower values (100-200): Better for typical gameplay (reduces server memory overhead)\n\n" +
                "Default: 200 IDs per batch (suitable for most games)\n" +
                "Range: 100-1000 IDs per batch\n\n" +
                "Client automatically requests new batch when 50% remaining.")]
        [Range(100, 1000)]
        public int client_GONetIdBatchSize = 200;

        [Tooltip("Maximum number of clients that can connect to the server simultaneously.\n\n" +
                "Default: 16 clients (typical for small-medium multiplayer games)\n" +
                "Range: 1-100 clients\n\n" +
                "Set via connection wizard or GONetConnectionPreset.maxConnections.")]
        [Range(1, 100)]
        public int maxConnections = 16;

        [Header("Congestion Management - Adaptive Scaling")]
        [Tooltip("⭐ ADAPTIVE POOL SIZING (Recommended)\n\n" +
                "When TRUE (default): Pool size automatically scales based on network demand.\n" +
                "• Scales UP when utilization exceeds 75% (prevents drops)\n" +
                "• Scales DOWN when utilization stays below 25% (conserves memory)\n" +
                "• Warns aggressively when memory/bandwidth limits approached\n" +
                "• Respects maxPacketsPerTick as absolute ceiling (safety cap)\n\n" +
                "When FALSE: Manual control - pool fixed at maxPacketsPerTick.\n" +
                "• For experts who need precise bandwidth control\n" +
                "• For bandwidth-constrained scenarios (mobile, low-end servers)\n\n" +
                "GONet Philosophy: \"Do what the user wants, warn when risky\"\n" +
                "Default: TRUE (auto-scale for best user experience)")]
        public bool enableAdaptivePoolScaling = true;

        [Tooltip("Starting pool size for adaptive scaling (when enableAdaptivePoolScaling=true).\n\n" +
                "Adaptive scaling will grow/shrink from this baseline:\n" +
                "• Grows when utilization >75% (up to maxPacketsPerTick ceiling)\n" +
                "• Shrinks when utilization <25% (down to this minimum)\n\n" +
                "CONFIGURATION GUIDELINES:\n" +
                "• Small Co-op (2-8 players): 500\n" +
                "• Battle Royale (50-100 players): 1500\n" +
                "• MMO (100+ players): 3000\n\n" +
                "NOTE: When enableAdaptivePoolScaling=false, this value is ignored and\n" +
                "maxPacketsPerTick is used as a fixed pool size.\n\n" +
                "Default: 1000 (suitable for most games)\n" +
                "Range: 100-10000")]
        [Range(100, 10000)]
        public int adaptivePoolBaselineSize = 1000;

        [Tooltip("ABSOLUTE MAXIMUM pool size (safety ceiling for adaptive scaling).\n\n" +
                "When enableAdaptivePoolScaling=TRUE:\n" +
                "• Pool can never grow beyond this limit (prevents runaway memory)\n" +
                "• Aggressive warnings logged when approaching this ceiling\n" +
                "• Recommended: 10x your baseline (e.g., baseline=1000 → max=10000)\n\n" +
                "When enableAdaptivePoolScaling=FALSE:\n" +
                "• This IS the fixed pool size (no scaling occurs)\n\n" +
                "EXPERT OVERRIDE: Lower this to cap bandwidth in constrained scenarios:\n" +
                "• Mobile clients with limited bandwidth\n" +
                "• Low-end servers with strict memory budgets\n" +
                "• Development/testing with artificial constraints\n\n" +
                "⚠️ WARNING: Setting this TOO LOW will cause packet drops!\n" +
                "SYMPTOMS: Objects stuck at spawn, high drop rates, 'Pool exhausted' errors\n\n" +
                "Default: 20000 (generous ceiling for auto-scaling)\n" +
                "Range: 100-100000")]
        [Range(100, 100000)]
        public int maxPacketsPerTick = 20000;

        [Tooltip("Start dropping unreliable packets when pool utilization exceeds this percentage.\n\n" +
                "Flow control threshold to prevent packet pool exhaustion.\n" +
                "When borrowed packet count exceeds (maxPacketsPerTick × unreliableDropThreshold),\n" +
                "new unreliable packets are dropped to preserve pool capacity for reliable packets.\n\n" +
                "TUNING GUIDANCE:\n" +
                "• Higher (0.95-0.99): More aggressive - allows pool to fill nearly completely\n" +
                "  Use when: Reliable packets are rare, mostly unreliable traffic\n" +
                "• Lower (0.80-0.90): More conservative - drops unreliable earlier\n" +
                "  Use when: Mix of reliable/unreliable, want buffer for reliable messages\n\n" +
                "TRADE-OFFS:\n" +
                "• Too high: Reliable packets may fail if pool suddenly fills\n" +
                "• Too low: Unnecessary unreliable packet drops under normal load\n\n" +
                "Default: 0.90 (drop unreliable when 90% pool utilization)\n" +
                "Range: 0.50-0.99 (50%-99%)")]
        [Range(0.5f, 0.99f)]
        public float unreliableDropThreshold = 0.90f;

        [Tooltip("Enable detailed congestion logging for debugging network bottlenecks.\n\n" +
                "When enabled, logs packet drop events with actionable diagnostics:\n" +
                "• Drop rate (packets dropped / total packets)\n" +
                "• Pool utilization percentage\n" +
                "• Channel causing drops (AutoMagicalSync, TimeSync, etc.)\n" +
                "• Recommended solutions (increase pool size, reduce sync frequency, etc.)\n\n" +
                "WHEN TO ENABLE:\n" +
                "• Investigating objects stuck at spawn position\n" +
                "• Tuning maxPacketsPerTick for your game\n" +
                "• Debugging high packet drop rates\n\n" +
                "PERFORMANCE IMPACT:\n" +
                "• Minimal - only logs when drops occur\n" +
                "• Throttled logging (batches drops to avoid spam)\n\n" +
                "Default: DISABLED (logging itself causes excessive overhead during congestion)")]
        public bool enableCongestionLogging = false;

        [Header("Temporal Thinning (Smart Congestion Management)")]
        [Tooltip("Enable intelligent temporal thinning for send/receive queues (RECOMMENDED - keep enabled).\n\n" +
                "WHAT IT DOES:\n" +
                "Instead of randomly dropping packets when queues fill (90% cutoff),\n" +
                "intelligently thins queues by keeping every Nth unreliable message.\n" +
                "ALL reliable messages are ALWAYS preserved (spawns, RPCs, etc.).\n\n" +
                "WHY IT'S SMARTER:\n" +
                "• Random drops → Choppy/unpredictable packet loss\n" +
                "• Temporal thinning → Smooth 50% fidelity timeline (evenly spaced)\n\n" +
                "BENEFITS:\n" +
                "• Send-side: Prevents network flooding BEFORE it happens\n" +
                "• Receive-side: Prevents 24-second freezes when client falls behind\n" +
                "• Preserves continuous timeline vs random gaps\n" +
                "• Zero configuration - auto-scales based on queue depth\n\n" +
                "WHEN TO DISABLE:\n" +
                "• You want raw packet drops for testing\n" +
                "• Custom congestion control is implemented\n\n" +
                "YOU CAN PROBABLY LEAVE THIS ALONE - smart defaults handle 800+ objects automatically.\n\n" +
                "Default: Enabled (recommended for production)")]
        public bool enableTemporalThinning = true;

        [Tooltip("Trigger temporal thinning when send queue exceeds this count (YOU CAN PROBABLY LEAVE THIS ALONE).\n\n" +
                "Send-side thinning runs BEFORE messages hit the network,\n" +
                "preventing congestion proactively.\n\n" +
                "SMART DEFAULTS:\n" +
                "• 200 messages = ~0.2-0.5ms thinning time (negligible)\n" +
                "• Triggers before 90% hard cutoff (smoother degradation)\n" +
                "• Auto-scales with maxPacketsPerTick\n\n" +
                "TUNING (only if needed):\n" +
                "• Lower (100-150): More aggressive - thins earlier, smoother but more CPU\n" +
                "• Higher (300-500): Less aggressive - allows larger bursts before thinning\n\n" +
                "Default: 200 messages\n" +
                "Range: 50-1000")]
        [Range(50, 1000)]
        public int sendQueueThinningTriggerCount = 200;

        [Tooltip("Trigger temporal thinning when receive queue exceeds this count (YOU CAN PROBABLY LEAVE THIS ALONE).\n\n" +
                "Receive-side thinning runs when client processing falls behind,\n" +
                "preventing main thread freezes (24-second hang with 800 objects).\n\n" +
                "SMART DEFAULTS:\n" +
                "• 200 messages = ~0.2-0.5ms thinning time (negligible)\n" +
                "• Prevents queue from exploding (1449 → 6380 messages during freeze)\n" +
                "• Defense-in-depth: Catches bursts that bypassed send-side thinning\n\n" +
                "TUNING (only if needed):\n" +
                "• Lower (100-150): More aggressive - prevents large backlogs\n" +
                "• Higher (300-500): Less aggressive - tolerates larger processing delays\n\n" +
                "Default: 200 messages\n" +
                "Range: 50-1000")]
        [Range(50, 1000)]
        public int receiveQueueThinningTriggerCount = 200;

        [Tooltip("⭐ ADAPTIVE THINNING: Enable intelligent thinning based on congestion severity (RECOMMENDED).\n\n" +
                "When enabled, thinning becomes MORE AGGRESSIVE the FURTHER over threshold you are:\n" +
                "• Light congestion (1-2x threshold): Drop 50% (keep every 2nd)\n" +
                "• Medium congestion (2-3x threshold): Drop 66% (keep every 3rd)\n" +
                "• Heavy congestion (3x+ threshold): Drop 75% (keep every 4th)\n\n" +
                "BENEFITS:\n" +
                "• Self-correcting: Recovers faster from severe congestion\n" +
                "• Minimal impact during light load (only 50% drop)\n" +
                "• Aggressive protection during scene loads (75% drop when needed)\n\n" +
                "When disabled, uses fixed 'temporalThinningKeepEveryNth' setting.\n\n" +
                "Default: Enabled (smart adaptive behavior)")]
        public bool enableAdaptiveThinning = true;

        [Tooltip("Baseline thinning rate (keep every Nth) for light congestion or when adaptive disabled.\n\n" +
                "Controls temporal sampling fidelity:\n" +
                "• 2 (default) = Keep every 2nd message = 50% fidelity\n" +
                "• 3 = Keep every 3rd message = 33% fidelity\n" +
                "• 5 = Keep every 5th message = 20% fidelity\n\n" +
                "ALWAYS KEEPS:\n" +
                "• ALL reliable messages (spawns, RPCs, ownership changes)\n" +
                "• Only thins unreliable position/rotation updates\n\n" +
                "WHY 50% WORKS:\n" +
                "• Authority re-sends state 30-60 times/sec\n" +
                "• Client value blending smooths over 1-2 dropped frames\n" +
                "• 50% fidelity = 15-30 Hz effective (still smooth)\n\n" +
                "ADAPTIVE THINNING OVERRIDE:\n" +
                "When enableAdaptiveThinning=true, this is the MINIMUM level.\n" +
                "System automatically increases to 3 or 4 under heavy congestion.\n\n" +
                "Default: 2 (keep every 2nd unreliable message = 50% fidelity)\n" +
                "Range: 2-10")]
        [Range(2, 10)]
        public int temporalThinningKeepEveryNth = 2;

        [Tooltip("CPU time budget for processing queues per frame (YOU CAN PROBABLY LEAVE THIS ALONE).\n\n" +
                "DUAL TRIGGER SYSTEM:\n" +
                "Thinning activates when EITHER condition is met:\n" +
                "1. Queue count exceeds threshold (200 messages), OR\n" +
                "2. Processing time exceeds this CPU budget\n\n" +
                "SMART TIME-BOXING:\n" +
                "• Measures actual time spent processing messages per frame\n" +
                "• Triggers thinning if budget exceeded (prevents frame stutter)\n" +
                "• 0 = disabled (only use queue count trigger)\n\n" +
                "RECOMMENDED VALUES:\n" +
                "• Desktop (60 FPS): 2.0-3.0ms (generous, allows normal burst traffic)\n" +
                "• VR (90 FPS): 1.5-2.0ms (moderate, allows scene init bursts)\n" +
                "• Mobile: 1.0-1.5ms (battery-conscious but not too aggressive)\n\n" +
                "WHY DUAL TRIGGERS WORK:\n" +
                "• Queue count: Catches sustained high traffic (200+ message backlog)\n" +
                "• CPU time: Catches GC pauses or extreme bursts (>2.5ms processing)\n" +
                "• Together: Comprehensive congestion protection without false positives\n\n" +
                "NOTE: 1.0ms is TOO AGGRESSIVE - scene init with 800 objects takes 1-3ms (normal!)\n" +
                "Default: 2.5ms (allows normal burst traffic during scene init/spawning)\n" +
                "Range: 0-5ms (0 = disabled)")]
        [Range(0f, 5f)]
        public float queueProcessingCpuBudgetMs = 2.5f;

        /// <summary>
        /// Configuration for reliable message frame spreading (main thread protection).
        /// Groups all frame spreading settings into a single struct for better Inspector UX.
        /// </summary>
        [Serializable]
        public struct ReliableFrameSpreadingSettings
        {
            [Tooltip("⭐ MAIN THREAD PROTECTION: Enable adaptive frame spreading for reliable messages (RECOMMENDED).\n\n" +
                    "PROBLEM SOLVED:\n" +
                    "• Reliable message bursts (RPC floods, mass spawns) overwhelm main Unity thread\n" +
                    "• Processing 600+ reliable messages in one frame → 10-20ms frame time → stutter/jank\n" +
                    "• Temporal thinning CANNOT drop reliable messages (they're reliable!)\n\n" +
                    "HOW IT WORKS:\n" +
                    "• When main thread queue backs up OR CPU budget exceeded → defer messages to next frame\n" +
                    "• LOSSLESS (not dropping like unreliable thinning, just deferring)\n" +
                    "• Adaptive escalation: Light (100/frame) → Medium (50) → Heavy (25)\n" +
                    "• PANIC VALVE: If queue > 2000, disable spreading (better to lag than lose sync)\n\n" +
                    "BENEFITS:\n" +
                    "• Prevents Unity main thread stutter (frame time stays <16ms at 60 FPS)\n" +
                    "• Preserves reliable delivery (no message loss)\n" +
                    "• Self-correcting (queue drains → processing limit increases)\n\n" +
                    "WHEN TO DISABLE:\n" +
                    "• You want raw main thread behavior for profiling\n" +
                    "• Latency is more important than frame time (competitive shooters)\n\n" +
                    "YOU CAN PROBABLY LEAVE THIS ALONE - smart defaults handle RPC bursts automatically.\n\n" +
                    "Default: Enabled (recommended for production)")]
            public bool enableReliableFrameSpreading;

            [Tooltip("Reliable queue count that triggers frame spreading on main thread.\n\n" +
                    "When main thread receive queue exceeds this count:\n" +
                    "• Frame spreading activates (limits processing per frame)\n" +
                    "• Prevents Unity main thread from stuttering\n\n" +
                    "SMART DEFAULTS:\n" +
                    "• 200 messages matches unreliable thinning threshold (consistency)\n" +
                    "• Triggers before queue explodes (proactive protection)\n\n" +
                    "TUNING (only if needed):\n" +
                    "• Lower (100-150): More aggressive - spreads earlier, smoother frames\n" +
                    "• Higher (300-500): Less aggressive - allows larger bursts before spreading\n\n" +
                    "Default: 200 messages\n" +
                    "Range: 50-500")]
            [Range(50, 500)]
            public int reliableProcessingThreshold;

            [Tooltip("Baseline messages to process per frame during light congestion.\n\n" +
                    "Controls max reliable messages processed per Unity frame:\n" +
                    "• 100 (default) = 6000 msg/sec at 60 FPS (high throughput)\n" +
                    "• 50 = 3000 msg/sec (moderate throughput, smoother frames)\n" +
                    "• 200 = 12000 msg/sec (very high throughput, potential stutter)\n\n" +
                    "ADAPTIVE ESCALATION (when enabled):\n" +
                    "System automatically reduces this limit based on congestion:\n" +
                    "• Light (1-2x overage): Use baseline (e.g., 100 msg/frame)\n" +
                    "• Medium (2-3x overage): Use baseline/2 (e.g., 50 msg/frame)\n" +
                    "• Heavy (3x+ overage): Use baseline/4 (e.g., 25 msg/frame)\n\n" +
                    "TRADE-OFFS:\n" +
                    "• Higher (150-200): More throughput but potential frame stutter under load\n" +
                    "• Lower (50-75): Smoother frames but slower reliable message delivery\n\n" +
                    "Default: 100 messages/frame (balances throughput with frame time)\n" +
                    "Range: 25-200")]
            [Range(25, 200)]
            public int reliableProcessingBaselineLimit;

            [Tooltip("⭐ ADAPTIVE ESCALATION: Enable intelligent spreading based on congestion severity (RECOMMENDED).\n\n" +
                    "When enabled, spreading becomes MORE AGGRESSIVE the FURTHER over threshold you are:\n" +
                    "• Light congestion (1-2x threshold): Process baseline (e.g., 100/frame)\n" +
                    "• Medium congestion (2-3x threshold): Process baseline/2 (e.g., 50/frame)\n" +
                    "• Heavy congestion (3x+ threshold): Process baseline/4 (e.g., 25/frame)\n\n" +
                    "BENEFITS:\n" +
                    "• Self-correcting: Recovers faster from severe congestion\n" +
                    "• Minimal impact during light load (baseline throughput)\n" +
                    "• Aggressive protection during RPC floods (25 msg/frame when needed)\n\n" +
                    "PANIC VALVE (always active):\n" +
                    "• If congestion > 10x threshold (queue > 2000), disable spreading entirely\n" +
                    "• Better to lag than lose synchronization\n" +
                    "• System processes everything to catch up (frame time be damned)\n\n" +
                    "When disabled, uses fixed 'reliableProcessingBaselineLimit' setting.\n\n" +
                    "Default: Enabled (smart adaptive behavior)")]
            public bool enableAdaptiveFrameSpreading;

            [Tooltip("Enable detailed logging for frame spreading events (for debugging/profiling).\n\n" +
                    "When enabled, logs:\n" +
                    "• [RECV-SPREAD] Queue count trigger activation (before processing)\n" +
                    "• [RECV-SPREAD-CPU] CPU budget trigger activation (during processing)\n" +
                    "• [RECV-SPREAD-PANIC] Panic valve activation (queue > 2000)\n" +
                    "• Congestion severity, processing limits, deferred message counts\n\n" +
                    "WHEN TO ENABLE:\n" +
                    "• Profiling frame time spikes\n" +
                    "• Debugging reliable message delivery delays\n" +
                    "• Understanding when/why frame spreading activates\n\n" +
                    "⚠️ WARNING: Logs every frame when spreading active (can be spammy!)\n\n" +
                    "Default: Disabled (enable only for debugging)")]
            public bool enableFrameSpreadingLogging;
        }

        [Header("Reliable Message Frame Spreading (Main Thread Protection)")]
        [Tooltip("Frame spreading prevents Unity main thread stutter when reliable message queues back up.\n\n" +
                "See individual fields for detailed configuration.")]
        public ReliableFrameSpreadingSettings frameSpreadingSettings = new ReliableFrameSpreadingSettings
        {
            enableReliableFrameSpreading = true,
            reliableProcessingThreshold = 200,
            reliableProcessingBaselineLimit = 100,
            enableAdaptiveFrameSpreading = true,
            enableFrameSpreadingLogging = false
        };

        [Header("Late-Joiner Backpressure (Per-Client Congestion Control)")]
        [Tooltip("⭐ CRITICAL FIX: Enable per-client backpressure to prevent late-joiner initialization failures (STRONGLY RECOMMENDED).\n\n" +
                "PROBLEM SOLVED:\n" +
                "• Early-joiners work: Connect when scene is quiet (no unreliable traffic yet)\n" +
                "• Late-joiners fail: Connect while 800 objects syncing → unreliable flood saturates OS socket → reliable InitComplete message blocked → timeout\n\n" +
                "HOW IT WORKS:\n" +
                "• Monitors each client's reliable message queue depth (from transport GetUsageStatistics)\n" +
                "• When reliable queue > high watermark (500), DROPS unreliable messages for that client only\n" +
                "• When reliable queue < low watermark (150), RESUMES unreliable messages\n" +
                "• Hysteresis prevents oscillation (requires N consecutive checks before state change)\n\n" +
                "BENEFITS:\n" +
                "• Late-joiners complete initialization even with 800+ objects actively syncing\n" +
                "• Per-client isolation (one slow client doesn't affect others)\n" +
                "• Zero configuration needed (smart defaults work for most games)\n\n" +
                "WHEN TO DISABLE:\n" +
                "• You have < 50 objects in scene (backpressure not needed)\n" +
                "• You want to debug late-joiner issues without backpressure interfering\n\n" +
                "Default: TRUE (recommended for production games with 100+ objects)\n" +
                "See also: reliableQueueHighWatermark, reliableQueueLowWatermark, congestionHysteresisCount")]
        public bool enableLateJoinerBackpressure = true;

        [Tooltip("Allow a tiny trickle of unreliable messages EVEN WHILE backpressure suppression is active.\n\n" +
                "WHY THIS EXISTS:\n" +
                "• Prevents clients from appearing completely frozen under extreme congestion\n" +
                "• Maintains minimum visual continuity while reliable queues drain\n\n" +
                "HOW IT WORKS:\n" +
                "• When suppressed, only 1 unreliable packet is allowed per interval\n" +
                "• Keeps bandwidth low but avoids 30s \"dead\" windows\n\n" +
                "Default: TRUE (recommended for production to avoid total freeze)\n" +
                "See also: backpressureUnreliableTrickleIntervalMs")]
        public bool enableBackpressureUnreliableTrickle = true;

        [Tooltip("Minimum interval (ms) between unreliable packets allowed while suppressed.\n\n" +
                "TUNING GUIDANCE:\n" +
                "• Lower (50-100ms): Smoother motion, more bandwidth\n" +
                "• Higher (200-500ms): Less bandwidth, more stutter\n" +
                "• 0 = Disable trickle entirely (FULL suppression)\n\n" +
                "Default: 200ms (~5 packets/sec)\n" +
                "Range: 0-1000ms\n\n" +
                "NOTE: If enableAdaptiveTrickle is TRUE, this is used as fallback only.")]
        [Range(0, 1000)]
        public int backpressureUnreliableTrickleIntervalMs = 200;

        [Header("Adaptive Trickle Rate")]
        [Tooltip("Enable ADAPTIVE trickle rate that scales based on congestion severity.\n\n" +
                "HOW IT WORKS:\n" +
                "• Uses congestion severity (queue depth relative to watermarks) as input\n" +
                "• Lerps between minTrickleIntervalMs (fast, near recovery) and maxTrickleIntervalMs (slow, heavy load)\n" +
                "• EMA smoothing prevents jitter from momentary fluctuations\n\n" +
                "WHY THIS MATTERS:\n" +
                "• Fixed 200ms trickle = 5Hz regardless of conditions\n" +
                "• Adaptive trickle = 20Hz at recovery, 2Hz at heavy load (10x range)\n" +
                "• Acts as SAFETY VALVE: frees CPU by not serializing packets destined for full buffers\n\n" +
                "When disabled, uses fixed 'backpressureUnreliableTrickleIntervalMs'.\n" +
                "Default: TRUE (starts at ~200ms like fixed, adapts 50-500ms based on congestion)")]
        public bool enableAdaptiveTrickle = true;

        [Tooltip("Fastest trickle rate (ms) when congestion severity is LOW (queue near low watermark).\n\n" +
                "This is the floor for adaptive trickle - used when congestion is clearing.\n" +
                "Lower = faster updates, more bandwidth usage.\n\n" +
                "Default: 50ms (~20Hz)\n" +
                "Range: 20-200ms")]
        [Range(20, 200)]
        public int minTrickleIntervalMs = 50;

        [Tooltip("Slowest trickle rate (ms) when congestion severity is HIGH (queue at/above high watermark).\n\n" +
                "This is the ceiling for adaptive trickle - used during heavy congestion.\n" +
                "Acts as SAFETY VALVE: at 500ms (2Hz), server isn't wasting CPU serializing packets\n" +
                "that are just going to sit in a full buffer or be dropped.\n\n" +
                "Default: 500ms (~2Hz)\n" +
                "Range: 200-1000ms")]
        [Range(200, 1000)]
        public int maxTrickleIntervalMs = 500;

        [Tooltip("EMA smoothing factor for trickle rate adaptation.\n\n" +
                "Controls how quickly trickle rate responds to congestion changes:\n" +
                "• Higher (0.5-0.8): More responsive, potential jitter\n" +
                "• Lower (0.1-0.3): Smoother transitions, slower to react\n\n" +
                "Applied to congestion severity before Lerping to trickle interval.\n" +
                "Default: 0.3 (good balance between responsiveness and stability)")]
        [Range(0.1f, 0.8f)]
        public float trickleAdaptationAlpha = 0.3f;

        [Header("Trickle Batch Size (Fragment-Complete Delivery)")]
        [Tooltip("Number of unreliable packets/fragments to allow per trickle interval.\n\n" +
                "CRITICAL FOR LARGE SYNC BUNDLES:\n" +
                "When 800+ objects sync, the bundle often exceeds MTU (~1200 bytes) and fragments.\n" +
                "Without batching, trickle allows only 1 fragment per interval → incomplete bundles.\n" +
                "Client receives partial data → most objects don't update → perceived freeze.\n\n" +
                "HOW BATCHING FIXES THIS:\n" +
                "• Batch size >= fragment count ensures COMPLETE bundles get through\n" +
                "• Example: 800 objects × ~10 bytes = ~8KB = ~7 fragments at 1.2KB MTU\n" +
                "• Batch size of 10 guarantees complete bundle delivery per interval\n\n" +
                "DYNAMIC SIZING (recommended):\n" +
                "Set to 0 for automatic sizing based on registered GONetParticipants:\n" +
                "• Auto-calculates: max(20, TotalParticipants / 8)\n" +
                "• 800 objects → batch size 100\n" +
                "• 1600 objects → batch size 200\n\n" +
                "PERFORMANCE TARGET:\n" +
                "• Full sync loop in ~1.5 seconds at 200ms trickle interval\n" +
                "• 800 objects at batch=100, 5Hz = 500 updates/sec = 1.6s full loop\n" +
                "• OLD formula (/80) was 10x too slow: batch=10 = 16 seconds!\n\n" +
                "MANUAL SIZING:\n" +
                "• Set to specific value (1-500) for explicit control\n" +
                "• Use when you know your exact sync requirements\n\n" +
                "BANDWIDTH IMPACT:\n" +
                "• Higher batch = more bytes per interval = higher bandwidth burst\n" +
                "• At 200ms interval with batch=100: 100 × 1.2KB = 120KB burst = 600KB/s peak\n\n" +
                "Default: 0 (dynamic auto-sizing)\n" +
                "Range: 0-500 (0=auto)")]
        [Range(0, 100)]
        public int trickleUnreliableBatchSize = 0;

        [Tooltip("Reliable queue depth threshold to START suppressing unreliable traffic (high watermark).\n\n" +
                "When client's reliable message queue exceeds this depth:\n" +
                "• Unreliable messages (position, rotation, animation) are DROPPED for that client only\n" +
                "• Reliable messages (spawns, RPCs, InitComplete) continue to be sent\n" +
                "• Allows reliable queue to drain without unreliable competition\n\n" +
                "TUNING GUIDANCE:\n" +
                "• Higher (700-1000): More tolerant - allows larger reliable queue before suppression\n" +
                "  Use when: You have fast network, high bandwidth, want to preserve more unreliable data\n" +
                "• Lower (300-500): More aggressive - suppresses unreliable earlier\n" +
                "  Use when: Slow networks, limited bandwidth, prioritize initialization speed\n\n" +
                "TRADE-OFFS:\n" +
                "• Too high (>1000): Reliable queue may still back up too much (late-joiner timeout)\n" +
                "• Too low (<300): Unnecessary unreliable drops during normal operation\n\n" +
                "⚠️ MUST BE HIGHER THAN LOW WATERMARK (creates hysteresis zone)\n\n" +
                "Default: 500 (works well for 800+ objects)\n" +
                "Range: 100-2000")]
        [Range(100, 2000)]
        public int reliableQueueHighWatermark = 500;

        [Tooltip("Reliable queue depth threshold to RESUME unreliable traffic (low watermark).\n\n" +
                "When client's reliable message queue drops below this depth:\n" +
                "• Unreliable messages (position, rotation, animation) RESUME being sent\n" +
                "• Client returns to normal operation\n\n" +
                "HYSTERESIS ZONE:\n" +
                "The gap between low and high watermark creates stability:\n" +
                "• Prevents oscillation (suppressing/resuming every frame)\n" +
                "• Larger gap (300) = More stable, slower to recover\n" +
                "• Smaller gap (200) = Less stable, faster recovery\n\n" +
                "TUNING GUIDANCE:\n" +
                "• Higher (300-450): Easier recovery - 20% drop from trigger is enough\n" +
                "• Lower (100-200): Harder recovery - requires deeper queue drain\n\n" +
                "⚠️ MUST BE LOWER THAN HIGH WATERMARK (creates hysteresis zone)\n\n" +
                "PRODUCTION RECOMMENDATION (Dec 2025):\n" +
                "Use 80% of highWatermark for 'Safety Margin' exit.\n" +
                "If trigger=500, exit at 400. A 20% drop is statistically significant.\n" +
                "Prevents 'Limbo' where queue stabilizes at 200-300 but never recovers.\n\n" +
                "Default: 400 (100-message hysteresis zone with high=500)\n" +
                "Range: 50-1000")]
        [Range(50, 1000)]
        public int reliableQueueLowWatermark = 400;

        [Tooltip("Number of consecutive checks above/below watermark required before changing suppression state (prevents oscillation).\n\n" +
                "HYSTERESIS MECHANISM:\n" +
                "• Must see queue depth > high watermark N times in a row before suppressing\n" +
                "• Must see queue depth < low watermark N times in a row before resuming\n" +
                "• Resets counter if queue depth enters hysteresis zone\n\n" +
                "WHY IT MATTERS:\n" +
                "Without hysteresis (count=1):\n" +
                "• Frame 1: Queue=510 → Suppress unreliable\n" +
                "• Frame 2: Queue=490 (dropped some) → Resume unreliable\n" +
                "• Frame 3: Queue=510 (resumed too soon) → Suppress unreliable\n" +
                "• ... OSCILLATES FOREVER (high CPU overhead, choppy sync)\n\n" +
                "With hysteresis (count=3):\n" +
                "• Frame 1-3: Queue>500 (consecutive 3 checks) → Suppress unreliable\n" +
                "• Frame 4-10: Queue drops 500→300 (suppression working)\n" +
                "• Frame 11-13: Queue<150 (consecutive 3 checks) → Resume unreliable\n" +
                "• Frame 14+: Queue stable at 200-300 (no further state changes)\n\n" +
                "TUNING GUIDANCE:\n" +
                "• Higher (5-10): More stable - fewer state changes, but slower to react\n" +
                "• Lower (1-2): Less stable - faster reaction, but more flapping\n\n" +
                "Default: 3 (good balance between stability and responsiveness)\n" +
                "Range: 1-10")]
        [Range(1, 10)]
        public int congestionHysteresisCount = 3;

        [Tooltip("Maximum time (seconds) a client can remain in unreliable suppression mode before automatic recovery check.\n\n" +
                "SAFETY NET:\n" +
                "• If backpressure gets stuck (e.g., GetUsageStatistics() failing), this timeout triggers a recovery check\n" +
                "• If queue depth is UNKNOWN or has recovered below the high watermark, unreliable traffic RESUMES\n" +
                "• If queue is still above high watermark, suppression remains (timer resets) to avoid bursts\n\n" +
                "WHEN IT TRIGGERS:\n" +
                "• Late-joiner reliable queue depth check fails/stalls\n" +
                "• Transport GetUsageStatistics() returns stale/invalid data\n" +
                "• Prevents permanent 'objects not moving' state\n\n" +
                "TUNING GUIDANCE:\n" +
                "• 5s (RECOMMENDED): Aggressive probing - if spike was temporary, recover NOW\n" +
                "• 10-15s: Conservative - wait longer before probe\n" +
                "• 30s+: Too slow - user suffers for too long after brief hiccups\n\n" +
                "PRODUCTION INSIGHT (Dec 2025):\n" +
                "If congestion is REAL (bad WiFi), queue stays high after 5s anyway.\n" +
                "If it was a SPIKE (window drag), queue drains instantly - recover in 5s not 30s.\n\n" +
                "Default: 5 seconds (aggressive short-cycle probe)\n" +
                "Range: 0-120 seconds (0 = disabled, no timeout)")]
        [Range(0, 120)]
        public int maxSuppressionTimeoutSeconds = 5;

        [Tooltip("TIMEOUT RECOVERY: Multiplier for high watermark threshold during timeout probe.\n\n" +
                "WITH 5-SECOND TIMEOUT + LOWWATERMARK=400:\n" +
                "Most recovery happens via normal path (queue < 400).\n" +
                "This multiplier is a secondary safety net for edge cases.\n\n" +
                "SOLUTION:\n" +
                "During timeout, allow recovery if queue <= highWatermark * this multiplier.\n" +
                "With default 1.2: timeout recovers if queue <= 600 (instead of <= 500).\n\n" +
                "TUNING:\n" +
                "• 1.0 = strict (same as highWatermark, only recover if queue < 500)\n" +
                "• 1.2 = recommended (recover if queue < 600, covers edge cases)\n" +
                "• 2.0+ = aggressive (rarely needed with new lowWatermark=400)\n\n" +
                "Default: 1.2 (allows recovery with queue up to 600)")]
        [Range(1.0f, 4.0f)]
        public float timeoutRecoveryThresholdMultiplier = 1.2f;

        [Tooltip("Enable detailed logging for congestion state changes (USEFUL FOR DEBUGGING LATE-JOINER ISSUES).\n\n" +
                "When enabled, logs:\n" +
                "• [BACKPRESSURE] Client X SUPPRESSING unreliable traffic (queue at 523)\n" +
                "• [BACKPRESSURE] Client X RESUMING unreliable traffic (suppressed for 2341ms, dropped 1245 msgs)\n" +
                "• Periodic drop summaries (every 100 unreliable drops per client)\n\n" +
                "WHEN TO ENABLE:\n" +
                "• Debugging late-joiner initialization failures\n" +
                "• Tuning watermark thresholds for your game\n" +
                "• Understanding backpressure behavior under load\n\n" +
                "PERFORMANCE IMPACT:\n" +
                "• Minimal - only logs state transitions (2-4 times during late-joiner init)\n" +
                "• Throttled drop logging (batched every 100 drops)\n\n" +
                "Default: FALSE (only enable when debugging congestion issues)\n" +
                "See also: enableCongestionLogging (different - that logs general packet drops)")]
        public bool enableCongestionStateLogging = false;

        [Header("Sync Bundle Handling - OnGONetReady Race Condition")]
        [Tooltip("Forces sync-bundle deferral ON at runtime, regardless of the serialized value in the prefab/scene.\n\n" +
                "WHY THIS EXISTS:\n" +
                "• Unity serializes component field values into prefabs/scenes.\n" +
                "• Older GONetGlobal prefabs may have deferSyncBundlesWaitingForGONetReady serialized as FALSE.\n" +
                "• Even if the code default changes to TRUE, the serialized FALSE will still win.\n\n" +
                "Set this to FALSE only if you intentionally want drop-first behavior for out-of-order bundles.\n\n" +
                "Default: true")]
        public bool forceEnableSyncBundleDeferral = true;

        [Tooltip("DEFAULT: true (DEFER bundles when participant not ready).\n\n" +
                "When enabled, sync bundles (reliable OR unreliable) that arrive before the referenced participant has completed\n" +
                "OnGONetReady / companion registration will be queued and retried as participants become ready.\n\n" +
                "WHY THIS EXISTS:\n" +
                "• Prevents spawn/sync races under load (sync arrives before spawn processing completes)\n" +
                "• Avoids dropping entire bundles when a single participant is missing\n\n" +
                "WHEN TO DISABLE:\n" +
                "• If you prefer to always drop out-of-order bundles and rely on frequent re-sends (high-frequency action games)\n\n" +
                "AUTHORITY-AGNOSTIC:\n" +
                "• Works on clients AND servers receiving sync data\n" +
                "• Handles ALL network topologies (client→server, server→client, peer-to-peer)\n\n" +
                "Default: true (recommended for dynamic spawn / hot-standby / p2p mesh sessions)")]
        public bool deferSyncBundlesWaitingForGONetReady = true;

        [Tooltip("Maximum sync bundles to queue per receiver while waiting for participants to complete OnGONetReady.\n\n" +
                "TYPICAL VALUES:\n" +
                "• Awake() completes in 1-2 frames typically\n" +
                "• At 200 spawns/sec, only 6-12 bundles queued\n" +
                "• Queue size of 100 handles extreme burst scenarios\n\n" +
                "FIFO DROP POLICY:\n" +
                "• When queue fills, oldest bundles are dropped to make room\n" +
                "• Warning logged prompting you to increase limit or disable deferral\n\n" +
                "Only used when deferSyncBundlesWaitingForGONetReady=true.\n\n" +
                "Default: 100 bundles\n" +
                "Range: 10-500")]
        [Range(10, 500)]
        public int maxSyncBundlesWaitingForGONetReady = 100;

        [Tooltip("Maximum bundles to process per OnGONetReady callback (prevents frame stutter during burst processing).\n\n" +
                "PERFORMANCE RATIONALE:\n" +
                "• OnGONetReady fires for EVERY participant that becomes ready\n" +
                "• Processing all queued bundles at once would cause frame stutter during mass spawns\n" +
                "• Remaining bundles will be processed in subsequent OnGONetReady callbacks\n\n" +
                "TUNING:\n" +
                "• Higher (20-50): Faster queue drainage, but potential frame spikes\n" +
                "• Lower (5-10): Smoother frame times, but slower queue drainage\n\n" +
                "Only used when deferSyncBundlesWaitingForGONetReady=true.\n\n" +
                "Default: 10 bundles/callback\n" +
                "Range: 1-50")]
        [Range(1, 50)]
        public int maxBundlesProcessedPerGONetReadyCallback = 10;

        [Tooltip("Maximum time (in seconds) to defer sync bundles waiting for missing participants.\n\n" +
                "PROBLEM THIS SOLVES:\n" +
                "• Under high load, sync bundles can arrive BEFORE spawn messages complete\n" +
                "• Participant not yet in instantiation map → sync bundle would be dropped\n" +
                "• This timeout allows deferring bundles while waiting for spawn to complete\n\n" +
                "HOW IT WORKS:\n" +
                "• When sync bundle arrives for unknown InstantiationId, defer it (don't drop)\n" +
                "• Retry processing each frame as new participants spawn\n" +
                "• If participant doesn't appear within timeout, drop the bundle\n\n" +
                "TUNING GUIDELINES:\n" +
                "• Reliable spawn messages: Should arrive within 1-2 seconds under normal network\n" +
                "• Network jitter/lag: Add 1-3 seconds buffer for extreme conditions\n" +
                "• Too low (<2s): May drop bundles during legitimate lag\n" +
                "• Too high (>10s): Wastes memory on bundles for truly missing participants\n\n" +
                "RECOMMENDED VALUES:\n" +
                "• LAN/low latency: 2-3 seconds\n" +
                "• Internet/moderate latency: 5 seconds (default)\n" +
                "• High latency/unreliable: 8-10 seconds\n\n" +
                "Set to 0 to disable timeout (defer indefinitely until queue full).\n\n" +
                "Default: 5 seconds\n" +
                "Range: 0-30 seconds")]
        [Range(0f, 30f)]
        public float maxSecondsToWaitForMissingParticipant = 5f;

        [Header("Time Synchronization")]
        [Tooltip("Maximum sane time adjustment (in seconds) for subsequent time sync responses.\n\n" +
                "PURPOSE:\n" +
                "• Protects against corrupted server timestamps causing massive time jumps\n" +
                "• FIRST sync is EXEMPT - allows any adjustment to align client/server clocks\n" +
                "• SUBSEQUENT syncs reject adjustments exceeding this threshold\n\n" +
                "RATIONALE - Why 10 seconds?\n" +
                "• Normal adjustments: <100ms (CLIENT_MAX_ADJUSTMENT_TOLERANCE_TICKS)\n" +
                "• Network jitter/lag: Up to 1-2 seconds in extreme cases\n" +
                "• Queue backup delays: Up to 3-5 seconds during late-joiner initialization\n" +
                "• 10 seconds = 100x normal tolerance, catches corruption while allowing legitimate delays\n\n" +
                "REAL-WORLD BUG PREVENTED:\n" +
                "• Server sent corrupted timestamps: 128K-385K seconds (35-107 HOURS!)\n" +
                "• Without this check: Infinite loop of massive corrections, time never converges\n" +
                "• With this check: Bad responses rejected, system waits for good response\n\n" +
                "FIRST SYNC EXEMPTION (CRITICAL!):\n" +
                "• Late-joiners connect when server has been running for minutes/hours\n" +
                "• Client clock starts at 0, server might be at 100+ seconds\n" +
                "• This creates LEGITIMATE 10+ second gap on first sync\n" +
                "• First sync MUST be allowed to make large adjustment\n" +
                "• Example: Client 2 at 72s, Server at 100s = 28s adjustment (VALID!)\n\n" +
                "TUNING:\n" +
                "• Too low (1-5s): False positives during queue backups, late-joiners might fail to sync\n" +
                "• Too high (60s+): Allows more corrupted data through before detection\n" +
                "• 10 seconds: Sweet spot balancing corruption detection vs legitimate delays\n\n" +
                "Default: 10.0 seconds\n" +
                "Range: 1.0 - 60.0 seconds")]
        [Range(1.0f, 60.0f)]
        public float client_MaxSaneTimeSyncAdjustmentSeconds = 10.0f;

        [Tooltip("Number of redundant time sync responses to send per request (unreliable channel protection).\n\n" +
                "PURPOSE:\n" +
                "• Time sync uses unreliable channel to prevent RTT corruption from retries\n" +
                "• Under high load (800+ objects), unreliable packets get dropped\n" +
                "• Sending N redundant responses dramatically increases delivery probability\n\n" +
                "DELIVERY PROBABILITY (assuming 10% packet loss rate):\n" +
                "• 1 send: 90% delivery (1 in 10 lost)\n" +
                "• 2 sends: 99% delivery (both lost = 1%)\n" +
                "• 3 sends: 99.9% delivery (all 3 lost = 0.1%)\n" +
                "• 5 sends: 99.999% delivery (all 5 lost = 0.001%)\n\n" +
                "COST vs BENEFIT:\n" +
                "• Time sync response = ~20 bytes\n" +
                "• 3 sends = 60 bytes total (tiny overhead)\n" +
                "• Benefit: Client converges reliably even under extreme packet loss\n\n" +
                "INTERNET ROBUSTNESS:\n" +
                "• LAN (1% loss): 2-3 sends sufficient\n" +
                "• WiFi (5-10% loss): 3-5 sends recommended\n" +
                "• Poor internet (20%+ loss): 5+ sends\n\n" +
                "TUNING:\n" +
                "• Too low (1): Clients may fail to converge under load\n" +
                "• Too high (10+): Wastes bandwidth, no significant benefit\n" +
                "• 3 is sweet spot for most scenarios\n\n" +
                "Default: 3 sends (99.9% delivery)\n" +
                "Range: 1-10")]
        [Range(1, 10)]
        public int server_TimeSyncResponseRedundancy = 3;

        [Tooltip("Time sync request interval (ms) during gap-closing phase.\n\n" +
                "PURPOSE:\n" +
                "• Controls how frequently client sends time sync requests during initial synchronization\n" +
                "• Lower values = faster convergence but more network traffic\n" +
                "• Higher values = slower convergence but less traffic\n\n" +
                "DEFAULT (200ms) - OPTIMAL FOR PRODUCTION:\n" +
                "• Fast convergence (~1 second to stable sync)\n" +
                "• Works correctly with real network latency\n" +
                "• Responses return before next request (no queue buildup)\n\n" +
                "CLUMSY/NETWORK SIMULATOR USERS (500ms):\n" +
                "• Some network simulation tools (e.g., Clumsy) buffer packets at high traffic density\n" +
                "• This causes artificially inflated RTT measurements (2000ms+ instead of ~100ms)\n" +
                "• Setting to 500ms avoids buffer accumulation in these tools\n" +
                "• Trade-off: Slower initial sync (~2.5 seconds vs ~1 second)\n\n" +
                "WHEN TO CHANGE:\n" +
                "• Testing with Clumsy: Set to 500\n" +
                "• Production builds: Leave at 200 (default)\n" +
                "• High-latency networks (300ms+ RTT): Consider 400-500\n\n" +
                "Default: 200ms\n" +
                "Range: 100-1000ms")]
        [Range(100, 1000)]
        public int client_TimeSyncGapClosingIntervalMs = 200;

        [Header("Transport Configuration")]
        [Tooltip("Enable pluggable transport layer (NEW in v1.6).\n\n" +
                "When DISABLED (default for backward compatibility):\n" +
                "• Uses NetcodeIO.NET + ReliableNetcode (existing stack)\n" +
                "• Zero code changes required\n" +
                "• Existing projects work unchanged\n\n" +
                "When ENABLED:\n" +
                "• Create custom transport in code: new NetcodeIOTransport() / new SteamTransport() / etc.\n" +
                "• Pass to GONetServer/GONetClient constructors\n" +
                "• Supports Steam P2P, Epic Online Services, Unity Relay, custom transports\n\n" +
                "USAGE EXAMPLE (in your startup code):\n" +
                "if (GONetGlobal.Instance.usePluggableTransport) {\n" +
                "    IGONetTransport transport = new NetcodeIOTransport();\n" +
                "    transport.Initialize(GONetTransportConfig.CreateDefault());\n" +
                "    server = new GONetServer(maxClients, port, transport);\n" +
                "}\n\n" +
                "MIGRATION PATH:\n" +
                "1. Leave disabled, test existing project (verify backward compatibility)\n" +
                "2. Enable, create NetcodeIOTransport in code (verify abstraction works)\n" +
                "3. Swap to SteamTransport/EOSTransport/custom (production deployment)\n\n" +
                "Default: true (uses pluggable transport abstraction)")]
        public bool usePluggableTransport = true;

        [Tooltip("Transport implementation to use when pluggable transport is enabled.\n\n" +
                "NetcodeIO: IP address/port based networking, suitable for LAN and dedicated servers\n" +
                "Steamworks: Steam ID based with NAT traversal via Steam Datagram Relay\n\n" +
                "Default: NetcodeIO")]
        public GONetTransportType transportType = GONetTransportType.NetcodeIO;

        #region Distributed Host Authority Configuration

        [Header("Distributed Host Authority (Experimental)")]
        [Tooltip("Enable distributed host topology where any peer can become the host.\n\n" +
                "WHEN ENABLED:\n" +
                "• Any peer can become 'host' based on fitness metrics (RTT, CPU, stability)\n" +
                "• Authority transfers seamlessly when host leaves or better candidate joins\n" +
                "• No dedicated server required (affordable for indie studios)\n\n" +
                "WHEN DISABLED (default):\n" +
                "• Traditional client-server model: first machine to start = server\n" +
                "• IsServer and IsClient work as expected\n" +
                "• Zero behavioral changes to existing code\n\n" +
                "SCALE TARGETS:\n" +
                "• Primary: 8-32 players (Phase 1-3)\n" +
                "• Experimental: Up to 100 players (Phase 4, requires explicit opt-in)\n\n" +
                "WARNING: This is an experimental feature. Enable only for testing or\n" +
                "if you understand the distributed authority implications.\n\n" +
                "Default: false (backward compatible)")]
        public bool enableDistributedHostAuthority = false;

        [Tooltip("When enabled, this node is a 'pinned host' (dedicated server) that never relinquishes authority.\n\n" +
                "WHEN ENABLED:\n" +
                "• This node is the eternal host - election is completely disabled\n" +
                "• Vice host is still tracked for warm standby, but migration never triggers\n" +
                "• Other nodes with isPinnedHost=false will defer to this node\n\n" +
                "USE CASES:\n" +
                "• Dedicated server deployments\n" +
                "• When you want distributed host metrics but manual control over host selection\n" +
                "• Testing: Pin host to observe distributed authority without migrations\n\n" +
                "Default: false (allow dynamic host selection)")]
        public bool isPinnedHost = false;

        [Tooltip("Cooldown period between host migrations (prevents rapid flapping).\n\n" +
                "After a migration completes, no new migration can trigger until cooldown expires.\n" +
                "Bypassed only when: host disconnected, host score drops to 0, or manual request.\n\n" +
                "TUNING:\n" +
                "• Higher (20-30s): More stable, fewer migrations, but slower to adapt\n" +
                "• Lower (5-10s): Faster adaptation, but risk of 'musical chairs'\n\n" +
                "Default: 15 seconds (good balance)\n" +
                "Range: 5-60 seconds")]
        [Range(5f, 60f)]
        public float hostMigrationCooldownSeconds = 15f;

        [Tooltip("Score improvement threshold to trigger proactive migration (as percentage).\n\n" +
                "A challenger must exceed the current host's smoothed score by this margin.\n" +
                "Combined with duration requirement (6 seconds by default) to prevent flapping.\n\n" +
                "EXAMPLE: With 0.2 (20%), if host has score 500, challenger needs 600+ to trigger.\n\n" +
                "TUNING:\n" +
                "• Higher (0.3-0.5): Only migrate for significantly better candidates\n" +
                "• Lower (0.1-0.15): More aggressive optimization, more migrations\n\n" +
                "Default: 0.2 (20% improvement required)\n" +
                "Range: 0.1-0.5 (10%-50%)")]
        [Range(0.1f, 0.5f)]
        public float hostMigrationScoreThreshold = 0.2f;

        [Tooltip("Heartbeat timeout to detect host crash (emergency failover).\n\n" +
                "If no heartbeat received from host within this time, assume host has crashed.\n" +
                "Vice host will self-promote; others wait for vice host or fall back to tiebreaker.\n\n" +
                "TUNING:\n" +
                "• Higher (3-5s): More tolerant of network hiccups, but slower failover\n" +
                "• Lower (1-2s): Faster failover, but risk of false-positive detection\n\n" +
                "Default: 2 seconds (matches plan target <500ms emergency failover)\n" +
                "Range: 1-10 seconds")]
        [Range(1f, 10f)]
        public float hostTimeoutSeconds = 2f;

        [Tooltip("Enable vice host (warm standby) for fast failover.\n\n" +
                "WHEN ENABLED:\n" +
                "• Second-best candidate receives continuous state replication from host\n" +
                "• On host crash, vice host can assume control within 1-2 RTTs\n" +
                "• Handoff payload is delta-only (bytes, not megabytes)\n\n" +
                "WHEN DISABLED:\n" +
                "• No warm standby - emergency failover requires full state transfer\n" +
                "• Failover takes longer (500ms+ depending on state size)\n\n" +
                "BANDWIDTH COST: ~1-5 KB/s extra to vice host for replication\n\n" +
                "Default: true (recommended for production)")]
        public bool enableViceHost = true;

        [Tooltip("Time in seconds before a new joiner can be considered for hosting.\n\n" +
                "Prevents a new player from instantly winning host by claiming perfect metrics.\n" +
                "Allows time for metrics to stabilize and connection quality to be verified.\n\n" +
                "SECURITY RATIONALE:\n" +
                "• New joiners have untested connections\n" +
                "• Initial metrics may be misleading (burst of good conditions)\n" +
                "• Gives time for cross-validation with other peers' RTT measurements\n\n" +
                "Default: 45 seconds\n" +
                "Range: 15-120 seconds")]
        [Range(15f, 120f)]
        public float newJoinerHostEligibilityDelaySeconds = 45f;

        [Tooltip("Maximum players allowed in distributed host mode.\n\n" +
                "If player count exceeds this limit:\n" +
                "• If isPinnedHost is set somewhere, continue normally\n" +
                "• Otherwise, log warning and require pinned host designation\n\n" +
                "RATIONALE:\n" +
                "• Distributed host works best at 8-32 players\n" +
                "• 33-100 players need Phase 4 clustering (experimental)\n" +
                "• >100 players typically need dedicated server due to N² bandwidth\n\n" +
                "Default: 32 players\n" +
                "Range: 8-100")]
        [Range(8, 100)]
        public int maxPlayersForDistributedMode = 32;

        [Header("Better Host Detection (Voluntary Migration)")]
        [Tooltip("Minimum absolute score difference required before a better host notification is raised.\n\n" +
                "Prevents triggers on tiny score gaps even if percentage threshold is met.\n\n" +
                "Default: 50 points")]
        public float betterHostMinimumDifference = 50f;

        [Tooltip("Number of consecutive evaluation samples (1 Hz) the candidate must remain better before notifying.\n\n" +
                "Default: 5 samples (5 seconds)")]
        [Range(1, 20)]
        public int betterHostSustainSamples = 5;

        [Tooltip("Cooldown after a better-host notification before notifying again for the SAME candidate.\n\n" +
                "Different candidates can notify immediately if conditions are met.\n\n" +
                "Default: 30 seconds")]
        [Range(5f, 120f)]
        public float betterHostEventCooldownSeconds = 30f;

        [Tooltip("Maximum time since the vice host last acknowledged a sync before we consider its state stale.\n\n" +
                "Voluntary migration is blocked if the vice host is stale.\n\n" +
                "Default: 2 seconds")]
        [Range(0.5f, 10f)]
        public float viceHostSyncStaleSeconds = 2f;

        [Tooltip("When enabled, the host will automatically initiate voluntary migration once a better host is stable.\n\n" +
                "Default: false (requires explicit user action)")]
        public bool betterHostAutoMigrateEnabled = false;

        [NonSerialized]
        public Func<bool> betterHostCanMigrateNowCallback = null;

        #endregion

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Header("Network Condition Simulation (Dev/Editor Only)")]
        [Tooltip("Enable network condition simulation for testing.\n\n" +
                "PURPOSE:\n" +
                "• Test your game under various network conditions without external tools\n" +
                "• Simulate latency, jitter, and packet loss\n" +
                "• More accurate than external tools like Clumsy (per-packet delays, not buffering)\n\n" +
                "IMPORTANT:\n" +
                "• Only available in Editor and Development builds\n" +
                "• Compiles out completely from production builds (zero overhead)\n" +
                "• Applied at transport layer, affects ALL network traffic\n\n" +
                "Default: false")]
        public bool networkSimulation_Enabled = false;

        [Tooltip("One-way latency in milliseconds.\n" +
                "Total round-trip time will be approximately 2x this value.\n\n" +
                "REFERENCE VALUES:\n" +
                "• LAN: 1-5ms\n" +
                "• Same city: 10-30ms\n" +
                "• Same country: 30-80ms\n" +
                "• Cross-continent: 100-200ms\n" +
                "• Satellite: 300-600ms\n\n" +
                "Default: 0ms (no latency)\n" +
                "Range: 0-500ms")]
        [Range(0, 500)]
        public int networkSimulation_LatencyMs = 0;

        [Tooltip("Random latency variance (jitter) in milliseconds.\n" +
                "Each packet's latency = LatencyMs ± JitterMs.\n\n" +
                "REFERENCE VALUES:\n" +
                "• Stable connection: 1-5ms\n" +
                "• WiFi: 5-20ms\n" +
                "• Mobile/4G: 20-50ms\n" +
                "• Congested network: 50-100ms\n\n" +
                "Default: 0ms (no jitter)\n" +
                "Range: 0-200ms")]
        [Range(0, 200)]
        public int networkSimulation_JitterMs = 0;

        [Tooltip("Packet loss percentage.\n" +
                "Each packet has this chance of being dropped entirely.\n\n" +
                "REFERENCE VALUES:\n" +
                "• Excellent: 0-0.1%\n" +
                "• Good: 0.1-1%\n" +
                "• Acceptable: 1-2%\n" +
                "• Poor: 2-5%\n" +
                "• Bad: 5-10%+\n\n" +
                "Default: 0% (no loss)\n" +
                "Range: 0-20%")]
        [Range(0f, 20f)]
        public float networkSimulation_PacketLossPercent = 0f;

        [Tooltip("Packet duplication percentage.\n" +
                "Each packet has this chance of being sent twice.\n" +
                "Useful for testing idempotent message handling.\n\n" +
                "Default: 0% (no duplication)\n" +
                "Range: 0-10%")]
        [Range(0f, 10f)]
        public float networkSimulation_DuplicatePercent = 0f;

        /// <summary>
        /// Creates a NetworkConditionConfig from the current GONetGlobal settings.
        /// </summary>
        public GONet.Transport.NetworkConditionConfig GetNetworkSimulationConfig()
        {
            return new GONet.Transport.NetworkConditionConfig
            {
                IsEnabled = networkSimulation_Enabled,
                LatencyMs = networkSimulation_LatencyMs,
                JitterMs = networkSimulation_JitterMs,
                PacketLossPercent = networkSimulation_PacketLossPercent,
                DuplicatePercent = networkSimulation_DuplicatePercent
            };
        }

        /// <summary>
        /// Applies a preset network condition configuration.
        /// </summary>
        public void ApplyNetworkSimulationPreset(GONet.Transport.NetworkConditionConfig preset)
        {
            networkSimulation_Enabled = preset.IsEnabled;
            networkSimulation_LatencyMs = preset.LatencyMs;
            networkSimulation_JitterMs = preset.JitterMs;
            networkSimulation_PacketLossPercent = preset.PacketLossPercent;
            networkSimulation_DuplicatePercent = preset.DuplicatePercent;
        }

        /// <summary>
        /// Wraps a transport with network condition simulation if enabled in settings.
        /// Call this after creating your transport but before Initialize().
        ///
        /// <para>
        /// USAGE:
        /// <code>
        /// IGONetTransport transport = new NetcodeIOTransport();
        /// transport = GONetGlobal.Instance.WrapTransportWithSimulation(transport);
        /// transport.Initialize(config);
        /// </code>
        /// </para>
        /// </summary>
        /// <param name="transport">The transport to potentially wrap</param>
        /// <returns>The original transport (if simulation disabled) or a NetworkConditionSimulator wrapping it</returns>
        public GONet.Transport.IGONetTransport WrapTransportWithSimulation(GONet.Transport.IGONetTransport transport)
        {
            if (!networkSimulation_Enabled)
            {
                return transport;
            }

            var config = GetNetworkSimulationConfig();
            GONetLog.Info($"[NetworkSimulation] Wrapping transport with simulation: Latency={config.LatencyMs}ms, Jitter=±{config.JitterMs}ms, Loss={config.PacketLossPercent}%, Dup={config.DuplicatePercent}%");
            return new GONet.Transport.NetworkConditionSimulator(transport, config);
        }
#endif

        [Header("Velocity-Augmented Sync")]
        [Tooltip("Interval (in seconds) between mandatory VALUE anchor bundles during VELOCITY-augmented sync.\n\n" +
                "PURPOSE:\n" +
                "When slow-moving values (rotation, position) use VELOCITY bundles for jitter elimination,\n" +
                "periodic VALUE anchors prevent drift accumulation from packet loss on unreliable channels.\n\n" +
                "HOW IT WORKS:\n" +
                "• Values within velocity quantization range → VELOCITY bundles (smooth client extrapolation)\n" +
                "• Every N seconds → Force VALUE anchor (re-sync client to server truth)\n" +
                "• Values exceeding velocity range → Automatic VALUE bundles (no waiting for anchor interval)\n\n" +
                "TUNING GUIDELINES:\n" +
                "• Lower (0.5s): Less drift, more frequent corrections (slight jitter risk)\n" +
                "• Medium (1.0s): Balanced - recommended for most games\n" +
                "• Higher (2-5s): Maximum smoothness, tolerate more drift (physics-heavy games)\n\n" +
                "EXAMPLE USE CASE:\n" +
                "Slowly rotating platform (5°/s) with players fighting on it:\n" +
                "• 1-second anchors → Max drift: ~2.1° (~36cm at 10m radius)\n" +
                "• 2-second anchors → Max drift: ~4.2° (~73cm at 10m radius)\n\n" +
                "OVERRIDE:\n" +
                "Per-value override available in GONetAutoMagicalSyncSettings_ProfileTemplate.VelocityAnchorIntervalSeconds\n" +
                "(0 = use this global default, >0 = custom interval for specific sync profile)\n\n" +
                "Default: 1.0 second\n" +
                "Range: 0.5-5.0 seconds")]
        [Range(0.5f, 5.0f)]
        public float velocityAnchorIntervalSeconds = 1.0f;

        [Header("SoA Blending Architecture")]
        [Tooltip("Use unified SoA blending pipeline for position/rotation sync. Default: TRUE")]
        public bool useUnifiedSoABlending = true;

        [Header("GONetId Reuse Protection")]
        [Tooltip("Time in seconds to wait after an object despawns before allowing its GONetId to be reused.\n\n" +
                "PURPOSE:\n" +
                "Prevents GONetId reuse while despawn messages are still in flight across the network.\n" +
                "If a GONetId is reused too quickly, despawn messages for the old object may arrive\n" +
                "after a new object has already claimed that ID, causing the wrong object to despawn.\n\n" +
                "RECOMMENDED VALUES:\n" +
                "• LAN (low latency): 2-3 seconds\n" +
                "• Internet (normal): 5 seconds (default)\n" +
                "• High latency/packet loss: 10-15 seconds\n\n" +
                "HOW IT WORKS:\n" +
                "• When an object despawns, its GONetId is marked with a timestamp\n" +
                "• The ID cannot be reused until this delay has elapsed\n" +
                "• Ensures all despawn messages have been delivered and processed\n" +
                "• Based on network RTT + safety margin for packet reordering\n\n" +
                "SYMPTOMS OF TOO-LOW VALUE:\n" +
                "• 'Despawn event received but no matching GONetParticipant found' warnings\n" +
                "• Objects stuck on client after server despawns them\n" +
                "• Wrong objects getting despawned (premature destroys)\n\n" +
                "Default: 5 seconds (handles typical internet latency)\n" +
                "Range: 1-30 seconds")]
        [Range(1f, 30f)]
        public float gonetIdReuseDelaySeconds = 5f;

        [Header("Reliable Message Queue")]
        [Tooltip("Maximum reliable message queue size before messages are dropped (lower-level transport setting).\n\n" +
                "PURPOSE:\n" +
                "When reliable messages are sent faster than they can be transmitted and acknowledged,\n" +
                "they queue up waiting for sendBuffer space. This setting prevents unbounded memory growth.\n\n" +
                "WHEN EXHAUSTION OCCURS:\n" +
                "• [RELIABLE-QUEUE-EXHAUSTION] error will be logged\n" +
                "• Message will be DROPPED (spawn events, RPCs, etc. will fail silently)\n" +
                "• This is EXTREMELY RARE - requires sustained burst + high packet loss + slow ACKs\n\n" +
                "COMMON CAUSES:\n" +
                "• Sustained 100+ messages/sec + high packet loss (>10%)\n" +
                "• Very high RTT (>250ms) with slow ACKs\n" +
                "• SendBuffer full (1024 capacity) AND continued high message rate\n\n" +
                "RECOMMENDED VALUES:\n" +
                "• LAN/Low latency: 1000-2000 (default: 2000)\n" +
                "• Internet/Normal latency: 2000-5000\n" +
                "• High latency/packet loss: 5000-10000\n\n" +
                "SYMPTOMS OF EXHAUSTION:\n" +
                "• Spawn events never propagate (objects appear only on one client)\n" +
                "• RPCs fail to deliver\n" +
                "• [RELIABLE-QUEUE-EXHAUSTION] errors in logs\n\n" +
                "Default: 2000 messages\n" +
                "Range: 1000-10000")]
        [Range(1000, 10000)]
        public int maxReliableMessageQueueSize = 2000;

        [Header("Persistence Queue (Record & Replay / Debugging)")]
        [Tooltip("⚠️ EXPERT SETTING - Leave at default unless you understand memory/CPU trade-offs.\n\n" +
                "PURPOSE:\n" +
                "Maximum sync event persistence queue size before thinning is triggered.\n" +
                "Used for record+replay functionality and debugging session history.\n\n" +
                "Higher values = More memory usage but complete session history\n" +
                "Lower values = Less memory but may trigger more aggressive thinning\n\n" +
                "Default: 10000 events (~1.3 sec buffer at 7500 events/sec)\n" +
                "Range: 1000-100000 events\n\n" +
                "CONFIGURATION GUIDELINES:\n" +
                "• Normal gameplay: 10000 (default - sufficient for debugging)\n" +
                "• Extended replay sessions: 50000-100000 (requires more memory)\n" +
                "• Memory-constrained platforms: 5000-10000\n\n" +
                "NOTE: Queue auto-thins at 80% capacity to preserve continuous timeline.")]
        [Range(1000, 100000)]
        public int persistenceQueueMaxSize = 10000;

        [Tooltip("⚠️ EXPERT SETTING - Leave at default unless you understand thinning behavior.\n\n" +
                "PURPOSE:\n" +
                "When queue reaches this percentage of max size, trigger temporal thinning.\n" +
                "Thinning drops UNRELIABLE events evenly across timeline (reliable events ALWAYS kept).\n\n" +
                "Lower values = More aggressive (better CPU/memory, more frequent thinning)\n" +
                "Higher values = Less aggressive (better fidelity, thinning triggers closer to limit)\n\n" +
                "Default: 0.8 (80% - triggers at 8000 events for 10000 max)\n" +
                "Range: 50%-95%\n\n" +
                "BEHAVIOR:\n" +
                "• At 80%: Thin queue, drop every 2nd unreliable event (see keepEveryNth)\n" +
                "• Reliable events NEVER dropped during thinning\n" +
                "• Preserves continuous timeline (no gaps in replay)\n\n" +
                "⚠️ Don't change unless experiencing frame stutters or memory issues.")]
        [Range(0.5f, 0.95f)]
        public float persistenceQueueThinningTriggerPercent = 0.8f;

        [Tooltip("⚠️ EXPERT SETTING - Leave at default unless you understand replay fidelity trade-offs.\n\n" +
                "PURPOSE:\n" +
                "During thinning, keep every Nth UNRELIABLE event (reliable events always kept).\n\n" +
                "Lower values = More aggressive thinning (2 = keep 50%, better memory/CPU)\n" +
                "Higher values = Less aggressive thinning (10 = keep 10%, better fidelity)\n\n" +
                "Default: 2 (keep every 2nd event = 50% fidelity for unreliable data)\n" +
                "Range: 2-10\n\n" +
                "EXAMPLES:\n" +
                "• keepEveryNth=2: Drop 50% of unreliable events (keep 1st, 3rd, 5th...)\n" +
                "• keepEveryNth=3: Drop 66% of unreliable events (keep 1st, 4th, 7th...)\n" +
                "• keepEveryNth=10: Drop 90% of unreliable events (keep 1st, 11th, 21st...)\n\n" +
                "RELIABLE EVENTS:\n" +
                "Always kept regardless of this setting (critical for debugging).\n\n" +
                "⚠️ Only change if replay quality is poor or memory is constrained.")]
        [Range(2, 10)]
        public int persistenceQueueThinningKeepEveryNth = 2;

        [Tooltip("⚠️ EXPERT SETTING - Recommended: ENABLED (default).\n\n" +
                "PURPOSE:\n" +
                "Always preserve RELIABLE sync events during thinning.\n" +
                "Reliable events are critical for debugging (guaranteed delivery contract).\n\n" +
                "When ENABLED (default):\n" +
                "• Reliable events NEVER dropped during thinning\n" +
                "• Only unreliable events thinned (every Nth kept)\n" +
                "• Best for debugging (preserves critical state changes)\n\n" +
                "When DISABLED:\n" +
                "• Reliable and unreliable treated equally during thinning\n" +
                "• More aggressive memory reduction\n" +
                "• May lose critical state changes in replay\n\n" +
                "Default: TRUE (recommended)\n\n" +
                "⚠️ Only disable if you need MAXIMUM memory efficiency and don't care about\n" +
                "guaranteed delivery replay. Disabling may break replay debugging.")]
        public bool persistenceQueueRespectReliability = true;

        [Tooltip("⚠️ EXPERT SETTING - Leave at 0 (disabled) unless experiencing frame stutters.\n\n" +
                "PURPOSE:\n" +
                "Maximum CPU time (milliseconds) spent processing persistence queue per frame.\n" +
                "If exceeded, triggers EMERGENCY thinning to prevent frame drops.\n\n" +
                "0 = Disabled (size-based thinning only, recommended)\n" +
                ">0 = Enable CPU-based emergency thinning\n\n" +
                "Default: 0 (disabled)\n" +
                "Range: 0-5ms\n\n" +
                "WHEN TO ENABLE:\n" +
                "• Set to 0.5-1.0ms if you experience frame stutters during high event volume\n" +
                "• Lower-end hardware may need 0.5ms cap\n" +
                "• High-end hardware can typically handle 2-5ms\n\n" +
                "⚠️ CAUTION: Setting too low causes VERY aggressive thinning.\n" +
                "Monitor logs for '[PERSISTENCE-EMERGENCY]' warnings.\n\n" +
                "NOTE: This is per-frame CPU time, NOT total CPU usage.")]
        [Range(0f, 5f)]
        public float persistenceQueueMaxCpuTimeMs = 0f;

        [Header("Network Message Processing Budget")]
        [Tooltip("Maximum real-world time (milliseconds) to spend processing network messages per Update() call.\n\n" +
                "PURPOSE:\n" +
                "Prevents catastrophic frame-time explosion when client/server cannot keep up with incoming message rate.\n" +
                "Applies to BOTH client AND server (same processing pressure on both).\n\n" +
                "HOW IT WORKS:\n" +
                "• Each Update(), process messages until time budget exhausted\n" +
                "• Remaining messages defer to next Update() (spread across frames)\n" +
                "• Maintains target framerate under heavy network load\n\n" +
                "RECOMMENDED VALUES:\n" +
                "• 60fps target: 5ms (30% of 16.67ms frame budget) - DEFAULT\n" +
                "• 30fps target: 10ms (30% of 33.33ms frame budget)\n" +
                "• Conservative: 3ms (aggressive frame-rate protection)\n" +
                "• Aggressive: 8-10ms (maximize throughput, risk frame drops)\n\n" +
                "TRADE-OFFS:\n" +
                "• Lower budget: Better frame times, but messages queue up (latency increases)\n" +
                "• Higher budget: Process more messages/frame, but risk frame drops\n\n" +
                "TYPICAL USAGE:\n" +
                "Most games: 5ms (default) - Handles 60fps with 30% network processing overhead\n" +
                "High-tick servers: 8-10ms - Maximize throughput on dedicated hardware\n" +
                "Mobile/VR: 3ms - Strict frame budgets require aggressive limiting\n\n" +
                "Default: 5.0ms (maintains 60fps)\n" +
                "Range: 1-16ms")]
        [Range(1f, 16f)]
        public float maxNetworkProcessingBudgetMs = 5.0f;

        [Tooltip("Queue size threshold to trigger unreliable message dropping (emergency safety valve).\n\n" +
                "PURPOSE:\n" +
                "When incoming message queue exceeds this size, drop unreliable messages to prevent catastrophic backup.\n" +
                "Reliable messages (spawns, despawns, RPCs) are NEVER dropped.\n\n" +
                "HOW IT WORKS:\n" +
                "• Queue size > threshold → Start dropping unreliable messages (keep every Nth)\n" +
                "• Queue drains below 50% of threshold → Stop dropping (hysteresis)\n" +
                "• Only unreliable channels affected (position updates, transient state)\n" +
                "• Authority re-sends state 30-60 times/sec → Auto-recovery from drops\n\n" +
                "WHEN DROPS OCCUR:\n" +
                "• [NETWORK-EMERGENCY] warning logged when dropping starts\n" +
                "• [NETWORK-RECOVERY] info logged when dropping stops\n" +
                "• Periodic stats show drop counts\n\n" +
                "RECOMMENDED VALUES:\n" +
                "• Conservative (smooth over correctness): 200 messages\n" +
                "• Balanced (default): 100 messages\n" +
                "• Aggressive (correctness over smooth): 50 messages\n" +
                "• Disabled: 0 (no dropping, queue grows unbounded - NOT RECOMMENDED)\n\n" +
                "SYMPTOMS OF QUEUE BACKUP:\n" +
                "• [QUEUE-BACKUP] warnings in logs\n" +
                "• Frame time increases (CPU spent processing old messages)\n" +
                "• Latency increases (messages delayed by queued backlog)\n\n" +
                "WHY DROPPING IS SAFE:\n" +
                "• Unreliable channels designed to be droppable (that's the contract)\n" +
                "• Position updates sent 30-60x/sec → Dropping 1-2 is imperceptible\n" +
                "• Value blending smooths over dropped frames\n" +
                "• Old position data (5+ seconds) is worse than dropping it\n\n" +
                "Default: 100 messages\n" +
                "Range: 0-500 (0 = disabled)")]
        [Range(0, 500)]
        public int networkQueueDropThreshold = 100;

        [Tooltip("When dropping unreliable messages, keep every Nth message (drop the rest).\n\n" +
                "PURPOSE:\n" +
                "Controls aggressiveness of unreliable message dropping during queue backup.\n\n" +
                "VALUES:\n" +
                "• 2: Keep every 2nd message (drop 50%) - DEFAULT\n" +
                "• 3: Keep every 3rd message (drop 66%)\n" +
                "• 4: Keep every 4th message (drop 75%)\n" +
                "• 10: Keep every 10th message (drop 90%)\n\n" +
                "TRADE-OFFS:\n" +
                "• Lower (2-3): Less aggressive, smoother but slower queue drainage\n" +
                "• Higher (5-10): More aggressive, faster queue drainage but noticeable jitter\n\n" +
                "RECOMMENDED:\n" +
                "• Most games: 2 (drop 50%) - Balanced smoothness and recovery\n" +
                "• Extreme load: 3-4 (drop 66-75%) - Aggressive recovery\n" +
                "• High-fidelity: 2 (drop 50%) - Minimize visible degradation\n\n" +
                "NOTE: Only active when queue size exceeds networkQueueDropThreshold.\n" +
                "During normal operation (queue < threshold), NO drops occur.\n\n" +
                "Default: 2 (drop every other unreliable message = 50% drop rate)\n" +
                "Range: 2-10")]
        [Range(2, 10)]
        public int unreliableDropRate = 2;

        [Tooltip("⚠️ EXPERT SETTING - Leave at 0 (disabled) unless memory-constrained.\n\n" +
                "PURPOSE:\n" +
                "Maximum memory (MB) for persistence queue before emergency thinning.\n" +
                "Useful for memory-constrained platforms (mobile, low-end PCs).\n\n" +
                "0 = Disabled (size-based thinning only, recommended)\n" +
                ">0 = Enable memory-based emergency thinning\n\n" +
                "Default: 0 (disabled)\n" +
                "Range: 0-500MB\n\n" +
                "WHEN TO ENABLE:\n" +
                "• Mobile platforms: 50-100MB\n" +
                "• Low-end PCs: 100-200MB\n" +
                "• Memory-constrained servers: 200-300MB\n\n" +
                "MEMORY CALCULATION:\n" +
                "Approximate estimate (~200 bytes/event):\n" +
                "• 10000 events ≈ 2MB\n" +
                "• 50000 events ≈ 10MB\n" +
                "• 100000 events ≈ 20MB\n\n" +
                "⚠️ NOTE: Memory calculation is APPROXIMATE (varies by event type).\n" +
                "Monitor actual memory usage via diagnostics logs.")]
        [Range(0, 500)]
        public int persistenceQueueMaxMemoryMB = 0;

        [Tooltip("GONet needs to know immediately on start of the program whether or not this game instance is a client or the server in order to initialize properly.  When using the provided Start_CLIENT.bat and Start_SERVER.bat files with builds, that will be taken care of for you.  However, when using the editor as a client (connecting to a server build), setting this flag to true is the only way for GONet to know immediately this game instance is a client.  If you run in the editor and see errors in the log on start up (e.g., \"[Log:Error] (Thread:1) (29 Dec 2019 20:24:06.970) (frame:-1s) (GONetEventBus handler error) Event Type: GONet.GONetParticipantStartedEvent\"), then it is likely because you are running as a client and this flag is not set to true.")]
        public bool shouldAttemptAutoStartAsClient = true;

        /// <summary>
        /// NOTE: GONetGlobal contains RUNTIME settings that affect gameplay behavior.
        /// For EDITOR-ONLY settings (code generation, asset processing, etc.), see GONetProjectSettings.
        /// </summary>
        [Header("Runtime Debug Settings")]
        [Tooltip("Automatically create the GONet Status UI overlay when the game starts.\n\n" +
                "⚠️ EDITOR & DEVELOPMENT BUILDS ONLY - stripped from release builds.\n\n" +
                "The Status UI shows:\n" +
                "• Role (Server/Client) and Authority ID\n" +
                "• Connected clients (server) or connection status (client)\n" +
                "• Synchronized time, FPS, RTT\n" +
                "• Network simulation settings (dev builds)\n" +
                "• Mesh/distributed host info (if enabled)\n\n" +
                "The UI starts minimized and can be expanded with the + button or F10 key.\n\n" +
                "Alternative: Add GONetStatusUIInitializer component to a scene object.\n\n" +
                "Default: Disabled")]
        public bool enableStatusUI = false;

        [Tooltip("Enable comprehensive message flow logging for debugging network issues.\n\n" +
                "When enabled, logs every send/receive/process event to: gonet-MessageFlow-YYYY-MM-DD.log\n\n" +
                "⚠️ WARNING: Generates large log files. Only enable for targeted debugging sessions.\n\n" +
                "Logs include:\n" +
                "• [MSG-SEND] - When messages are sent (timestamp, target, channel, bytes)\n" +
                "• [MSG-RECV] - When messages arrive (timestamp, source, latency)\n" +
                "• [MSG-PROC] - When OnGONetReady events are broadcast\n\n" +
                "The MessageFlow logging profile is automatically registered with:\n" +
                "• Separate file output (gonet-MessageFlow-YYYY-MM-DD.log)\n" +
                "• No stack traces (clean, readable output)\n" +
                "• Info level and above\n\n" +
                "Default: Disabled")]
        public bool enableMessageFlowLogging = false;

#if UNITY_EDITOR
        [Header("Editor-Only: Problematic GNP Handling")]
        [Tooltip("How to handle GONetParticipants detected as problematic (modified after last build).\n\n" +
                "DISABLE (Recommended):\n" +
                "• Disables the GONetParticipant component\n" +
                "• Also disables all GONetParticipantCompanionBehaviour components on the same GameObject\n" +
                "• Prevents runtime errors but object won't network\n" +
                "• Recommended for testing to catch issues early\n\n" +
                "LOG_ONLY:\n" +
                "• Only logs an error message\n" +
                "• Does NOT disable any components\n" +
                "• Object will attempt to network (may cause errors)\n" +
                "• Useful if you want to proceed despite warnings\n\n" +
                "This setting only affects the Unity Editor. Builds always assume correct state.")]
        public ProblematicGNPHandling problematicGNPHandling = ProblematicGNPHandling.Disable;

        /// <summary>
        /// How to handle GONetParticipants detected as problematic in the editor.
        /// </summary>
        public enum ProblematicGNPHandling
        {
            /// <summary>
            /// Disable the GONetParticipant and all GONetParticipantCompanionBehaviour components.
            /// Prevents runtime errors but object won't network.
            /// </summary>
            Disable,

            /// <summary>
            /// Only log an error, don't disable anything.
            /// Object will attempt to network (may cause errors).
            /// </summary>
            LogOnly
        }
#endif

        private readonly List<GONetParticipant> enabledGONetParticipants = new List<GONetParticipant>(1000);
        /// <summary>
        /// <para>A convenient collection of all the <see cref="GONetParticipant"/> instances that are currently enabled no matter what the value of <see cref="GONetParticipant.OwnerAuthorityId"/> value is.</para>
        /// <para>Elements are added here once Start() was called on the <see cref="GONetParticipant"/> and removed once OnDisable() is called.</para>
        /// <para>Do NOT attempt to modify this collection as to avoid creating issues for yourself/others.</para>
        /// </summary>
        public IEnumerable<GONetParticipant> EnabledGONetParticipants => enabledGONetParticipants;

        public static readonly string ServerIPAddress_Default = GONetMain.isServerOverride ? "0.0.0.0" : "127.0.0.1";
        public const int ServerPort_Default = 40000;

        public delegate void ServerConnectionInfoChanged(string serverIP, int serverPort);
        public static event ServerConnectionInfoChanged ActualServerConnectionInfoSet;

        public static bool AreAllServerConnectionInfoActualsSet => !string.IsNullOrWhiteSpace(serverIPAddress_Actual) && serverPort_Actual != -1;

        [SerializeField]
        [Tooltip("Server connection ip or hostname.  If not provided, GONetGlobal.ServerIPAddress_Default is used instead.")]
        private string server;
        [SerializeField]
        [Tooltip("Server connection port.  If not provided, GONetGlobal.ServerPort_Default is used instead.")]
        private int serverPort;

        /// <summary>
        /// DO NOT SET THIS OUTSIDE GONET INTERNAL CODE!
        /// </summary>
        internal static string serverIPAddress_Actual;
        /// <summary>
        /// IMPORTANT: This will be NULL/empty when the actual serer ip address is not known!
        /// </summary>
        public static string ServerIPAddress_Actual { get => serverIPAddress_Actual; internal set { serverIPAddress_Actual = value; FireEventIfBothActualsSet(); } }

        /// <summary>
        /// DO NOT SET THIS OUTSIDE GONET INTERNAL CODE!
        /// </summary>
        internal static int serverPort_Actual = -1;
        /// <summary>
        /// IMPORTANT: This will be -1 when the actual server ip address is not known!
        /// </summary>
        public static int ServerPort_Actual { get => serverPort_Actual; internal set { serverPort_Actual = value; FireEventIfBothActualsSet(); } }

        public static IPEndPoint ServerP2pEndPoint { get; internal set; }

        private static void FireEventIfBothActualsSet()
        {
            if (AreAllServerConnectionInfoActualsSet)
            {
                ActualServerConnectionInfoSet?.Invoke(serverIPAddress_Actual, serverPort_Actual);
            }
        }

        protected override void Awake()
        {
            // Self-destroying singleton pattern: Prevent duplicate GONetGlobal instances
            if (instance != null && instance != this)
            {
                // Expected during LoadSceneMode.Single - the new scene's GONetGlobal prefab is destroyed
                GONetLog.Debug($"[GONetGlobal] Duplicate GONetGlobal detected in scene '{gameObject.scene.name}'. Destroying duplicate immediately to prevent any processing.");

                // CRITICAL: Use DestroyImmediate (not Destroy) to prevent any further processing on this duplicate
                // This ensures GONetParticipant and other components don't try to initialize or process on a duplicate that shouldn't exist
                DestroyImmediate(gameObject);
                return;
            }
            instance = this;

            // CRITICAL: Force GONetLog initialization early in Awake.
            // GONetLog uses a static constructor that may not run until first call.
            // Without this, logging in standalone builds may silently fail because
            // GONetGlobal.Awake() runs before any GONetLog call triggers initialization.
            GONetLog.Debug("[GONetGlobal] Awake starting - GONetLog initialized");

            // MIGRATION FIX: Sync-bundle deferral settings were added after many prefabs/scenes existed.
            // Unity does not always apply field initializers to existing serialized prefabs, so these may come in as 0/false.
            // Guard against invalid values (e.g., queue size 0 causing Dequeue() exceptions) and align older prefabs to the new defaults.
            bool migratedSyncBundleDeferralDefaults = false;
            if (maxSyncBundlesWaitingForGONetReady < 1)
            {
                maxSyncBundlesWaitingForGONetReady = 100;
                migratedSyncBundleDeferralDefaults = true;
            }

            if (maxBundlesProcessedPerGONetReadyCallback < 1)
            {
                maxBundlesProcessedPerGONetReadyCallback = 10;
                migratedSyncBundleDeferralDefaults = true;
            }

            if (migratedSyncBundleDeferralDefaults)
            {
                // Older prefabs should converge to the safer runtime behavior unless explicitly opted out.
                forceEnableSyncBundleDeferral = true;
                deferSyncBundlesWaitingForGONetReady = true;
                GONetLog.Info("[GONetGlobal] Migrated sync-bundle deferral defaults for older prefab/scene");
            }

            // CRITICAL: Ensure sync-bundle deferral is enabled for existing prefabs that still have the old serialized default (false).
            // Without this, logs will show "[GONETREADY-DROP-REAL] ... RECOMMENDATION: Enable deferral ...", and spawn/sync races can drop whole bundles.
            if (forceEnableSyncBundleDeferral && !deferSyncBundlesWaitingForGONetReady)
            {
                deferSyncBundlesWaitingForGONetReady = true;
                GONetLog.Info("[GONetGlobal] Force-enabled deferSyncBundlesWaitingForGONetReady (forceEnableSyncBundleDeferral=true)");
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Apply command-line network simulation arguments (overrides inspector values)
            // This runs whenever GONetGlobal is created - works for both:
            // 1. GONetGlobal in scene from start (Awake runs early)
            // 2. Lobby flow (GONetGlobal instantiated later when user selects role)
            // Either way, this runs BEFORE transport is created in OnReadyToStartGONet()
            if (GONetCommandLineParser.HasNetworkSimulationArgs())
            {
                GONetCommandLineParser.ApplyNetworkSimulationArgs();
            }

            // CPU throttling initialization (for stress testing without external tools like BES)
            if (GONetCommandLineParser.HasCpuThrottlingArgs())
            {
                GONetCommandLineParser.ApplyCpuThrottlingArgs();
            }
#endif

            // MIGRATION FIX: Initialize frame spreading settings if struct is uninitialized (all zeros)
            // This handles existing GONetGlobal prefabs created before frame spreading was added
            // Unity doesn't auto-apply field initializers to existing serialized prefabs
            if (frameSpreadingSettings.reliableProcessingThreshold == 0)
            {
                frameSpreadingSettings = new ReliableFrameSpreadingSettings
                {
                    enableReliableFrameSpreading = true,
                    reliableProcessingThreshold = 200,
                    reliableProcessingBaselineLimit = 100,
                    enableAdaptiveFrameSpreading = true,
                    enableFrameSpreadingLogging = false
                };
                GONetLog.Info("[GONetGlobal] Initialized frame spreading settings with defaults (migrated from older prefab)");
            }

            // Apply unified SoA blending feature flag from inspector setting
            GONetFeatureFlags.UseUnifiedSoABlending = useUnifiedSoABlending;
            if (useUnifiedSoABlending)
            {
                GONetLog.Info("[GONetGlobal] Unified SoA blending pipeline ENABLED");
            }

            // CRITICAL: Record scene load baseline timestamp FIRST
            // GONetGlobal is the first object to Awake in the scene (ExecutionOrder -32000)
            // This timestamp represents when the scene started loading
            // All scene-defined objects will have awakeTimeTicks close to this value
            // Runtime-spawned objects will have awakeTimeTicks significantly AFTER this
            gonetGlobalAwakeTicks = HighResolutionTimeUtils.UtcNowTicks;

            // PHASE 2 FIX: Force Application.runInBackground = true for multiplayer servers/clients
            // This ensures servers keep processing network traffic even when Unity window loses focus
            // Critical for dedicated servers and multi-instance local testing (editor + builds)
            // Without this, unfocused instances pause and cause timeouts/disconnects
            Application.runInBackground = true;

            // Register the MessageFlow logging profile for comprehensive message flow debugging
            // This profile writes to a separate file (gonet-MessageFlow-YYYY-MM-DD.log) with no stack traces
            GONetLog.RegisterLoggingProfile(new GONetLog.LoggingProfile(
                GONetMain.MessageFlowLoggingProfile,
                outputToSeparateFile: true,
                includeStackTraces: false,  // CRITICAL: Prevents stack trace spam
                minimumLogLevel: GONetLog.LogLevel.Info));

            // Enable message flow logging if inspector checkbox is set
            GONetMain.EnableMessageFlowLogging = enableMessageFlowLogging;
            if (enableMessageFlowLogging)
            {
                GONetLog.Info($"[GONetGlobal] Message flow logging ENABLED - output to: gonet-MessageFlow-{System.DateTime.Now:yyyy-MM-dd}.log");
            }

            if (gonetLocalPrefab == null)
            {
                Debug.LogError("Sorry.  We have to exit the application.  GONet requires GONetGlobal to have a prefab for GONetLocal set in the field named " + nameof(gonetLocalPrefab));
#if UNITY_EDITOR
                // Application.Quit() does not work in the editor so
                // UnityEditor.EditorApplication.isPlaying need to be set to false to end the game
                UnityEditor.EditorApplication.isPlaying = false;
#else
         Application.Quit();
#endif
            }

            if (!string.IsNullOrWhiteSpace(server))
            {
                serverIPAddress_Actual = server;
            }
            if (serverPort != default && serverPort > 0)
            {
                serverPort_Actual = serverPort;
            }
            ServerIPAddress_Actual = string.IsNullOrWhiteSpace(serverIPAddress_Actual) ? ServerIPAddress_Default : serverIPAddress_Actual;
            ServerPort_Actual = (serverPort_Actual == default || serverPort_Actual < 0) ? ServerPort_Default : serverPort_Actual;

            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            GONetMain.InitOnUnityMainThread(this, gameObject.GetComponent<GONetSessionContext>(), valueBlendingBufferLeadTimeMilliseconds);

            // CRITICAL: Force GONetGlobal to use reserved hardcoded GONetId BEFORE base.Awake()
            // This ensures both server and clients use the same GONetId (raw: 1, authority: 1023 → composed: 2047)
            // Prevents GONetId mismatch when GONetGlobal is instantiated at runtime (lobby pattern)
            // This fix resolves RPC delivery issues where client couldn't receive RPCs sent to GONetGlobal
            GONetParticipant myParticipant = GetComponent<GONetParticipant>();
            if (myParticipant != null)
            {
                myParticipant.OwnerAuthorityId = GONetMain.OwnerAuthorityId_Server; // Always server authority (1023)
                uint composedGONetId = (GONetParticipant.GONetGlobal_GONetId_Raw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED) | GONetMain.OwnerAuthorityId_Server;
                myParticipant.GONetId = composedGONetId;
                GONetLog.Info($"[GONetGlobal] Forced hardcoded GONetId: {composedGONetId} (raw: {GONetParticipant.GONetGlobal_GONetId_Raw}, authority: {GONetMain.OwnerAuthorityId_Server})");
            }
            else
            {
                GONetLog.Error($"[GONetGlobal] CRITICAL: Could not find GONetParticipant component!");
            }

            base.Awake(); // YUK: code smell...having to break OO protocol here and call base here as it needs to come AFTER the init stuff is done in GONetMain.InitOnUnityMainThread() and unity main thread identified or exceptions will be thrown in base.Awake() when subscribing

            // IMPORTANT: Cache design time metadata BEFORE any other initialization
            // This ensures metadata is available when GONetParticipants start their Awake() calls
            StartCoroutine(CacheDesignTimeMetadata_ThenContinueInit());

            enabledGONetParticipants.Clear();

            // Start physics sync coroutine (server-only, runs after all physics processing)
            StartCoroutine(PhysicsSync_WaitForFixedUpdate());

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Create Status UI if enabled in inspector (editor/development builds only)
            // This provides the same functionality as GONetStatusUIInitializer but via inspector toggle
            if (enableStatusUI)
            {
                CreateStatusUI();
            }
#endif

            if (shouldAttemptAutoStartAsClient)
            {
                Editor_AttemptStartAsClientIfAppropriate(ServerIPAddress_Actual, ServerPort_Actual);
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Creates the GONet Status UI overlay if it doesn't already exist.
        /// Called automatically when <see cref="enableStatusUI"/> is true.
        /// Can also be called manually at runtime to enable the status UI.
        /// Only available in Editor and Development builds.
        /// </summary>
        public void CreateStatusUI()
        {
            // Check if GONetStatusUI already exists (e.g., from GONetStatusUIInitializer)
            GONet.Sample.GONetStatusUI existingUI = FindObjectOfType<GONet.Sample.GONetStatusUI>();
            if (existingUI != null)
            {
                GONetLog.Debug("[GONetGlobal] GONetStatusUI already exists, skipping creation");
                return;
            }

            // Create a persistent GameObject for status UI
            GameObject statusUIObject = new GameObject("GONetStatusUI");
            statusUIObject.AddComponent<GONet.Sample.GONetStatusUI>();

            // Ensure it persists across scene changes
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(statusUIObject);
            }

            GONetLog.Info("[GONetGlobal] Created GONetStatusUI (enableStatusUI=true)");
        }
#endif

        /// <summary>
        /// Physics sync coroutine - Runs AFTER all FixedUpdate() calls AND physics processing.
        /// This is the correct timing to capture final Rigidbody state for synchronization.
        ///
        /// Unity execution order per physics frame:
        /// 1. FixedUpdate() on all scripts (including companion FixedUpdateAfterGONetReady)
        /// 2. Internal physics simulation (forces, velocities, positions)
        /// 3. OnCollisionEnter/Stay/Exit callbacks
        /// 4. OnTriggerEnter/Stay/Exit callbacks
        /// 5. WaitForFixedUpdate resumes ← WE CAPTURE PHYSICS STATE HERE!
        ///
        /// This timing ensures we sync the FINAL physics state after ALL physics processing,
        /// not intermediate state before collisions/triggers have been handled.
        /// </summary>
        private System.Collections.IEnumerator PhysicsSync_WaitForFixedUpdate()
        {
            while (true)
            {
                // Wait for all physics processing to complete
                // This includes: FixedUpdate calls, physics simulation, collision/trigger callbacks
                yield return new WaitForFixedUpdate();

                // NOW capture and sync final physics state (server-only check inside method)
                GONetMain.PhysicsSync_ProcessASAP();
            }
        }

        protected override void OnDestroy()
        {
            // IMPORTANT: Call base first to ensure proper Unity cleanup order
            base.OnDestroy();

            // CRITICAL: Only the singleton instance should cleanup subscriptions
            // Duplicate instances that were destroyed in Awake() never subscribed in the first place
            if (instance == this)
            {
                // Unsubscribe from scene events BEFORE clearing instance reference
                // This ensures any cleanup code can still reference the singleton
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
                GONetLog.Debug("[GONetGlobal] Unsubscribed from sceneLoaded event");

                // Clear singleton reference last
                instance = null;
                GONetLog.Debug("[GONetGlobal] Cleared singleton instance reference on destroy");
            }
            // NOTE: Duplicate instances do nothing here - they never subscribed, never set instance reference
        }

        public override void OnGONetClientVsServerStatusKnown(bool isClient, bool isServer, ushort myAuthorityId)
        {
            base.OnGONetClientVsServerStatusKnown(isClient, isServer, myAuthorityId);

            if (isServer)
            {
                GONetMain.gonetServer.ClientDisconnected += Server_ClientDisconnected;
            }
        }

        private void Server_ClientDisconnected(GONetConnection_ServerToClient gonetConnection_ServerToClient)
        {
            Server_MakeDoublySureAllClientOwnedGNPsDestroyed(gonetConnection_ServerToClient.OwnerAuthorityId);
            GONetPoolManager.Server_ReturnAllBorrowedByAuthority(gonetConnection_ServerToClient.OwnerAuthorityId);
        }

        /// <summary>
        /// Called during failover promotion to subscribe the client disconnect cleanup handler.
        /// When a client promotes to host, OnGONetClientVsServerStatusKnown() is not called again,
        /// so we need this separate method to ensure the cleanup handler is subscribed.
        /// Without this, client-owned GNPs (like GONetLocal) are not destroyed when clients disconnect
        /// from the promoted host.
        /// </summary>
        internal void SubscribeClientDisconnectedHandlerForPromotion()
        {
            if (GONetMain.gonetServer != null)
            {
                GONetMain.gonetServer.ClientDisconnected += Server_ClientDisconnected;
                GONetLog.Info("[GONetGlobal] Subscribed Server_ClientDisconnected cleanup handler after failover promotion");
            }
            else
            {
                GONetLog.Warning("[GONetGlobal] Cannot subscribe cleanup handler - gonetServer is null");
            }
        }

        private void Server_MakeDoublySureAllClientOwnedGNPsDestroyed(ushort ownerAuthorityId)
        {
            // CRITICAL FAILOVER FIX: Never destroy server-owned objects (authority 1023).
            // After host failover, stale connections from the old host (also authority 1023) may time out.
            // Without this check, the cleanup would destroy ALL server-owned GNPs (scene objects, etc.)
            // when the promoted host processes the stale connection timeout.
            if (ownerAuthorityId == GONetMain.OwnerAuthorityId_Server)
            {
                GONetLog.Warning($"[GONetGlobal] Server_MakeDoublySureAllClientOwnedGNPsDestroyed skipping authority {ownerAuthorityId} (server authority) - this is likely a stale connection from dead host");
                return;
            }

            for (int i = enabledGONetParticipants.Count - 1; i >= 0; --i)
            {
                GONetParticipant enabledGNP = enabledGONetParticipants[i];
                if (enabledGNP.OwnerAuthorityId == ownerAuthorityId && enabledGNP && enabledGNP.gameObject)
                {
                    Destroy(enabledGNP.gameObject);
                }
            }
        }

        public override void OnGONetParticipantEnabled(GONetParticipant gonetParticipant)
        {
            base.OnGONetParticipantEnabled(gonetParticipant);

            AddIfAppropriate(gonetParticipant);
        }

        public override void OnGONetParticipantStarted(GONetParticipant gonetParticipant)
        {
            base.OnGONetParticipantStarted(gonetParticipant);

            AddIfAppropriate(gonetParticipant);

            ushort toBeRemotelyControlledByAuthorityId;
            if (GONetMain.IsServer && GONetSpawnSupport_Runtime.Server_TryGetMarkToBeRemotelyControlledBy(gonetParticipant, out toBeRemotelyControlledByAuthorityId))
            {
                GONetMain.Server_AssumeAuthorityOver(gonetParticipant);

                // IMPORTANT: only now, after assuming authority, will the following change actually get propogated to the non-owners (i.e., since only the owner can make a auto-propogated change)
                gonetParticipant.RemotelyControlledByAuthorityId = toBeRemotelyControlledByAuthorityId;

                GONetSpawnSupport_Runtime.Server_UnmarkToBeRemotelyControlled_ProcessingComplete(gonetParticipant);
            }
        }

        private void AddIfAppropriate(GONetParticipant gonetParticipant)
        {
            if (!enabledGONetParticipants.Contains(gonetParticipant)) // may have already been added elsewhere
            {
                enabledGONetParticipants.Add(gonetParticipant);
            }
        }

        public override void OnGONetParticipantDisabled(GONetParticipant gonetParticipant)
        {
            enabledGONetParticipants.Remove(gonetParticipant); // regardless of whether or not it was present before this call, it will not be present afterward
        }

        private void Editor_AttemptStartAsClientIfAppropriate(string serverIP, int serverPort)
        {
            if (!Application.isEditor) return;

            if (!AreAllServerConnectionInfoActualsSet)
            {
                ActualServerConnectionInfoSet += Editor_AttemptStartAsClientIfAppropriate;
                return;
            }

            ActualServerConnectionInfoSet -= Editor_AttemptStartAsClientIfAppropriate;

            // Check if auto-detection is enabled
            if (!enableAutoRoleDetection)
            {
                GONetLog.Info("[GONetGlobal] Auto role detection is disabled. Use command line args (-server/-client) or keyboard shortcuts (Ctrl+Alt+S/C) to start manually.");
                return;
            }

            // Auto-detect whether to start as server or client based on port availability
            // Only do this if we're not already a client or server
            if (!GONetMain.IsClient && !GONetMain.IsServer)
            {
                var sampleSpawner = GetComponent<GONetSampleSpawner>();
                if (sampleSpawner)
                {
                    bool isServerRemote = !NetworkUtils.IsIPAddressOnLocalMachine(serverIP);
                    bool isPortOccupied = NetworkUtils.IsLocalPortListening(serverPort);

                    if (isServerRemote || isPortOccupied)
                    {
                        // Server is running remotely or port is occupied locally → start as client
                        sampleSpawner.InstantiateClientIfNotAlready();
                        GONetLog.Info($"[GONetGlobal] Editor auto-detection: Starting as CLIENT (server at {serverIP}:{serverPort})");
                    }
                    else
                    {
                        // Port is free and server would be local → start as server
                        sampleSpawner.InstantiateServerIfNotAlready();
                        GONetLog.Info($"[GONetGlobal] Editor auto-detection: Port {serverPort} is free, starting as SERVER");
                    }
                }
                else
                {
                    const string UNABLE = "Unable to honor your setting of true on ";
                    const string BECAUSE = " because we could not find ";
                    const string ATTACHED = " attached to this GameObject, which is required to automatically start in this manner.";
                    GONetLog.Error(string.Concat(UNABLE, nameof(shouldAttemptAutoStartAsClient), BECAUSE, nameof(GONetSampleSpawner), ATTACHED));
                }
            }
        }

        private void OnSceneLoaded(Scene sceneLoaded, LoadSceneMode loadMode)
        {
            // CRITICAL: Record scene load baseline at TIME OF THIS CALLBACK
            // For initial scene (with GONetGlobal): Use GONetGlobal's awake time (more accurate - first object to Awake)
            // For additive scenes: Use current time (GONetGlobal not in this scene)
            // For LoadSceneMode.Single: Use current time (GONetGlobal may have been destroyed/recreated)

            long sceneLoadBaseline;
            if (sceneLoaded.name == gameObject.scene.name && gonetGlobalAwakeTicks > 0)
            {
                // This is the scene GONetGlobal is in - use its Awake time as baseline
                sceneLoadBaseline = gonetGlobalAwakeTicks;
            }
            else
            {
                // Different scene (additive or new Single mode scene) - use current time
                sceneLoadBaseline = HighResolutionTimeUtils.UtcNowTicks;
            }

            sceneLoadTimesTicks[sceneLoaded.name] = sceneLoadBaseline;

            // DIAGNOSTIC: Log scene load with high-resolution timestamp
            double sceneLoadSeconds = sceneLoadBaseline * HighResolutionTimeUtils.TICKS_TO_SECONDS;
            double gonetElapsedSeconds = GONetMain.Time != null ? GONetMain.Time.ElapsedSeconds : 0;

            GONetLog.Info($"[OnSceneLoaded] ENTRY - Scene: '{sceneLoaded.name}', LoadMode: {loadMode}, " +
                          $"IsServer: {GONetMain.IsServer}, IsClient: {GONetMain.IsClient}, " +
                          $"BaselineTicks: {sceneLoadBaseline}, BaselineSeconds: {sceneLoadSeconds:F6}, " +
                          $"GONetTime: {gonetElapsedSeconds:F3}s, " +
                          $"UsedGONetGlobalBaseline: {sceneLoadBaseline == gonetGlobalAwakeTicks}, " +
                          $"MetadataCached: {GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached}");

            // SCENE CHANGE TIME SYNC FIX: Request aggressive time sync on clients after scene load
            // This ensures RTT is updated with fresh measurements instead of being stuck at the
            // potentially high RTT measured during scene load (when processing was delayed).
            // Aggressive mode: 1-second sync intervals for 10 seconds (vs normal 5-second intervals)
            GONetMain.RequestAggressiveTimeSync($"SceneLoaded:{sceneLoaded.name}");

            // CRITICAL: Defer scene-defined participant filtering until metadata is ready
            // This ensures Signal #2 (DesignTimeLocation) works reliably on all platforms
            // On Windows/Mac/iOS/Linux, metadata is synchronous and already ready (exits immediately)
            // On Android/WebGL, metadata loads asynchronously via UnityWebRequest (~50-200ms)
            StartCoroutine(ProcessSceneDefinedParticipants_WhenMetadataReady(sceneLoaded, loadMode));
        }

        /// <summary>
        /// Waits for DesignTimeMetadata to be cached, then filters scene-defined participants.
        /// This ensures Signal #2 (DesignTimeLocation) works reliably across all platforms.
        /// On Windows/Mac/iOS/Linux: Metadata synchronous, exits immediately (zero latency)
        /// On Android/WebGL: Metadata async (~50-200ms), waits with timeout protection
        /// </summary>
        private System.Collections.IEnumerator ProcessSceneDefinedParticipants_WhenMetadataReady(Scene sceneLoaded, LoadSceneMode loadMode)
        {
            // Wait for global metadata cache with timeout
            const float METADATA_TIMEOUT_SECONDS = 5.0f;
            float startTime = Time.time;
            float elapsed = 0f;

            while (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached && elapsed < METADATA_TIMEOUT_SECONDS)
            {
                yield return null;
                elapsed = Time.time - startTime;
            }

            // Log diagnostic info
            if (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
            {
                GONetLog.Error($"[OnSceneLoaded] Metadata cache TIMEOUT after {elapsed:F3}s for scene '{sceneLoaded.name}'! " +
                               $"Falling back to timestamp-only detection (Signal #3/4). This may cause false positives for early instantiations. " +
                               $"Check that DesignTimeMetadata.json exists in StreamingAssets/GONet/");
            }
            else if (elapsed > 0.001f)
            {
                // Metadata took more than 1ms - likely Android/WebGL async loading
                GONetLog.Info($"[OnSceneLoaded] Metadata cache ready for scene '{sceneLoaded.name}' after {elapsed * 1000:F1}ms wait (async load detected - likely Android/WebGL)");
            }
            else
            {
                // Metadata already ready - Windows/Mac/iOS/Linux synchronous loading
                GONetLog.Debug($"[OnSceneLoaded] Metadata cache already ready for scene '{sceneLoaded.name}' (synchronous load detected - Windows/Mac/iOS/Linux)");
            }

            // NOW filter scene-defined participants - metadata ready (or timed out)
            List<GONetParticipant> gonetParticipantsInLevel = new List<GONetParticipant>();
            GameObject[] sceneObjects = sceneLoaded.GetRootGameObjects();

            GONetLog.Debug($"OnSceneLoaded: '{sceneLoaded.name}' with {sceneObjects.Length} root objects");

            FindAndAppend(sceneObjects, gonetParticipantsInLevel, (gnp) => !WasInstantiated(gnp)); // IMPORTANT: or else!

            GONetMain.RecordParticipantsAsDefinedInScene(gonetParticipantsInLevel);

            // DIAGNOSTIC: Log which participants were marked as scene-defined
//            GONetLog.Info($"[OnSceneLoaded] Scene '{sceneLoaded.name}' - Marked {gonetParticipantsInLevel.Count} participants as scene-defined:");
//            foreach (var gnp in gonetParticipantsInLevel)
//            {
//                double createdAtSeconds = gnp.awakeTimeTicks * HighResolutionTimeUtils.TICKS_TO_SECONDS;
//                GONetLog.Info($"  - '{gnp.name}' (InstanceID: {gnp.GetInstanceID()}, AwakeTime: {createdAtSeconds:F6}s)");
//            }

            if (GONetMain.IsClientVsServerStatusKnown)
            {
                GONetLog.Info($"[OnSceneLoaded] About to call AssignOwnerAuthorityIds_IfAppropriate for {gonetParticipantsInLevel.Count} participants (IsServer: {GONetMain.IsServer})");
                GONetMain.AssignOwnerAuthorityIds_IfAppropriate(gonetParticipantsInLevel);

                // IMPORTANT: If this is the server, initialize scene-defined objects with IGONetSyncdBehaviourInitializer
                // This must happen EVEN IF no clients are connected (server needs to initialize its own objects)
                if (GONetMain.IsServer)
                {
                    StartCoroutine(Server_SyncSceneDefinedObjectIds_WhenReady(sceneLoaded.name, gonetParticipantsInLevel));
                }
                else if (GONetMain.IsClient)
                {
                    // CLIENT: Check if we have buffered GONetId assignments for this scene
                    // Server sends GONetIds proactively (no round-trip wait), so they may arrive BEFORE scene loads
                    if (bufferedGONetIdAssignmentsByScene.TryGetValue(sceneLoaded.name, out BufferedGONetIdAssignments buffered))
                    {
                        double receiveDelay = GONetMain.Time.ElapsedSeconds - buffered.receivedAtTime;

                        if (buffered.isCompressed)
                        {
                            GONetLog.Info($"[GONetId-BUFFER-APPLY-COMPRESSED] CLIENT applying buffered COMPRESSED GONetId assignments for scene '{sceneLoaded.name}' ({buffered.locationIndices.Length} objects, metadata count: {buffered.expectedMetadataCount}, received {receiveDelay * 1000:F1}ms ago, time: {GONetMain.Time.ElapsedSeconds:F3}s)");
                            ApplyGONetIdAssignments_Compressed(buffered.sceneName, buffered.expectedMetadataCount, buffered.locationIndices, buffered.gonetIds, buffered.customInitData);
                        }
                        else
                        {
                            GONetLog.Info($"[GONetId-BUFFER-APPLY] CLIENT applying buffered GONetId assignments for scene '{sceneLoaded.name}' ({buffered.designTimeLocations.Length} objects, received {receiveDelay * 1000:F1}ms ago, time: {GONetMain.Time.ElapsedSeconds:F3}s)");
                            ApplyGONetIdAssignments(buffered.sceneName, buffered.designTimeLocations, buffered.gonetIds, buffered.customInitData);
                        }

                        // Clear the buffer entry
                        bufferedGONetIdAssignmentsByScene.Remove(sceneLoaded.name);
                        GONetLog.Info($"[GONetId-BUFFER-CLEARED] Cleared buffer for scene '{sceneLoaded.name}' (time: {GONetMain.Time.ElapsedSeconds:F3}s)");
                    }
                    else
                    {
                        GONetLog.Info($"[GONetId-BUFFER-CHECK] No buffered GONetId assignments for scene '{sceneLoaded.name}' (will receive via RPC later, time: {GONetMain.Time.ElapsedSeconds:F3}s)");
                    }
                }
            }
            else
            {
                StartCoroutine(AssignOwnerAuthorityIds_WhenAppropriate(gonetParticipantsInLevel));
            }
        }

        /// <summary>
        /// Determines if a GONetParticipant was runtime-spawned vs scene-defined.
        /// Uses multiple independent signals for maximum reliability with sub-millisecond precision.
        /// </summary>
        bool WasInstantiated(GONetParticipant gnp)
        {
                // ==================================================
                // SIGNAL #1: wasInstantiatedForce (100% reliable for network spawns)
                // ==================================================
                if (gnp.wasInstantiatedForce)
                {
                    //GONetLog.Debug($"[WasInstantiated] TRUE (Signal #1: wasInstantiatedForce) - '{gnp.name}'");
                    return true;
                }

                // ==================================================
                // SIGNAL #2: DesignTimeLocation (with manual lookup fallback)
                // ==================================================
                string location = null;

                if (gnp.IsDesignTimeMetadataInitd)
                {
                    // BEST CASE: Participant metadata already initialized via its AwakeCoroutine
                    location = gnp.DesignTimeLocation;
                }
                else if (GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
                {
                    // FALLBACK: Global cache ready, but participant's AwakeCoroutine hasn't completed yet
                    // This can happen if WasInstantiated() is called before participant coroutines run
                    // Manually look up metadata from the cached library
                    location = GONetSpawnSupport_Runtime.GetDesignTimeMetadata_Location(gnp, force: false);

                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        //GONetLog.Debug($"[WasInstantiated] Signal #2 using manual metadata lookup (participant coroutine not complete yet) - '{gnp.name}'");
                    }
                }

                if (!string.IsNullOrWhiteSpace(location))
                {
                    // Prefab-based instantiation (resources or addressables)
                    if (location.StartsWith("resources://") ||
                        location.StartsWith("addressables://") ||
                        location.Contains("/Resources/") ||
                        location.EndsWith(".prefab"))
                    {
                        //GONetLog.Debug($"[WasInstantiated] TRUE (Signal #2: DesignTimeLocation prefab) - '{gnp.name}', Location: {location}");
                        return true;
                    }

                    // Scene hierarchy path (scene-defined)
                    if (location.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX))
                    {
                        //GONetLog.Debug($"[WasInstantiated] FALSE (Signal #2: DesignTimeLocation scene_hierarchy) - '{gnp.name}', Location: {location}");
                        return false;
                    }
                }
                else if (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
                {
                    // Global cache not ready yet - Signal #2 unavailable
                    // This should be RARE since ProcessSceneDefinedParticipants_WhenMetadataReady waits for cache
                    // But could happen if WasInstantiated() called from elsewhere (not OnSceneLoaded)
                    //GONetLog.Debug($"[WasInstantiated] Signal #2 unavailable - metadata not cached yet for '{gnp.name}', falling back to timing signals");
                }
                else
                {
                    // Metadata cached but lookup returned empty - possible edge case
                    //GONetLog.Debug($"[WasInstantiated] Signal #2 lookup returned empty location for '{gnp.name}', falling back to timing signals");
                }

                // ==================================================
                // SIGNAL #3: High-Resolution Timestamp Comparison
                // ==================================================
                // Compare object creation time vs scene load time using sub-millisecond precision
                // Scene-defined objects are created DURING scene load (awakeTime ≈ sceneLoadTime)
                // Runtime-spawned objects are created AFTER scene load (awakeTime > sceneLoadTime + threshold)

                if (gnp.awakeTimeTicks > 0)
                {
                    string sceneName = gnp.gameObject.scene.name;

                    // Special case: DontDestroyOnLoad pseudo-scene
                    if (sceneName == "DontDestroyOnLoad")
                    {
                        // Object already moved to DDOL, fall back to recency check (Signal #4)
                        //GONetLog.Debug($"[WasInstantiated] Signal #3 skipped - '{gnp.name}' already in DontDestroyOnLoad, using Signal #4");
                    }
                    else if (sceneLoadTimesTicks.TryGetValue(sceneName, out long sceneLoadTicks))
                    {
                        // Calculate time delta with sub-millisecond precision
                        long deltaTicks = gnp.awakeTimeTicks - sceneLoadTicks;
                        double deltaSeconds = deltaTicks * HighResolutionTimeUtils.TICKS_TO_SECONDS;

                        // Threshold: 50ms (conservative - handles scene load processing variance)
                        // This accounts for:
                        // - Multiple objects' Awake() calls during scene load
                        // - Platform-specific timing variance
                        // - Coroutine scheduling delays
                        const double THRESHOLD_SECONDS = 0.050; // 50ms

                        if (deltaSeconds > THRESHOLD_SECONDS)
                        {
                            //GONetLog.Debug($"[WasInstantiated] TRUE (Signal #3: high-res timestamp) - '{gnp.name}', " +
//                                           $"created {deltaSeconds * 1000:F3}ms after scene load (threshold: {THRESHOLD_SECONDS * 1000:F0}ms)");
                            return true;
                        }
                        else
                        {
                            //GONetLog.Debug($"[WasInstantiated] FALSE (Signal #3: high-res timestamp) - '{gnp.name}', " +
//                                           $"created {deltaSeconds * 1000:F3}ms after scene load (within threshold)");
                            return false;
                        }
                    }
                    else
                    {
                        //GONetLog.Debug($"[WasInstantiated] Signal #3 unavailable - scene '{sceneName}' load time not tracked, using Signal #4");
                    }
                }

                // ==================================================
                // SIGNAL #4: Absolute Recency Check (LAST RESORT)
                // ==================================================
                // If object was created VERY recently (< 20ms ago), it's likely runtime-spawned
                // This catches objects instantiated BEFORE OnSceneLoaded registered the scene

                if (gnp.awakeTimeTicks > 0)
                {
                    long currentTicks = HighResolutionTimeUtils.UtcNowTicks;
                    long timeSinceAwakeTicks = currentTicks - gnp.awakeTimeTicks;
                    double timeSinceAwakeSeconds = timeSinceAwakeTicks * HighResolutionTimeUtils.TICKS_TO_SECONDS;

                    // Threshold: 20ms (very conservative - catches brand-new objects only)
                    const double RECENCY_THRESHOLD_SECONDS = 0.020; // 20ms

                    if (timeSinceAwakeSeconds < RECENCY_THRESHOLD_SECONDS)
                    {
                        //GONetLog.Debug($"[WasInstantiated] TRUE (Signal #4: recency) - '{gnp.name}', " +
//                                       $"created {timeSinceAwakeSeconds * 1000:F3}ms ago (threshold: {RECENCY_THRESHOLD_SECONDS * 1000:F0}ms)");
                        return true;
                    }
                }

                // ==================================================
                // DEFAULT: Assume Scene-Defined (Conservative)
                // ==================================================
                //GONetLog.Debug($"[WasInstantiated] FALSE (default: all signals inconclusive) - '{gnp.name}'");
                return false;
            }

        private IEnumerator AssignOwnerAuthorityIds_WhenAppropriate(List<GONetParticipant> gonetParticipantsInLevel)
        {
            while (!GONetMain.IsClientVsServerStatusKnown)
            {
                yield return null;
            }

            GONetMain.AssignOwnerAuthorityIds_IfAppropriate(gonetParticipantsInLevel);
        }

        /// <summary>
        /// Cache of serialized scene object initialization data, keyed by "sceneName|designTimeLocation".
        /// This ensures server sends IDENTICAL data to all clients (avoiding re-randomization).
        /// Populated during initial scene load, reused for late-joiners.
        /// </summary>
        private static readonly Dictionary<string, byte[]> sceneObjectInitDataCache = new Dictionary<string, byte[]>();

        /// <summary>
        /// Helper class to store buffered GONetId assignments received before scene is loaded.
        /// Server sends GONetIds proactively (no round-trip wait), client buffers if scene not ready.
        /// </summary>
        private class BufferedGONetIdAssignments
        {
            public string sceneName;

            // Full path mode (existing)
            public string[] designTimeLocations;

            // Compressed mode (new)
            public ushort expectedMetadataCount;
            public ushort[] locationIndices;
            public bool isCompressed;  // Flag to distinguish modes

            // Shared fields
            public uint[] gonetIds;
            public byte[][] customInitData;
            public double receivedAtTime; // For diagnostics
        }

        /// <summary>
        /// Client-side buffer for GONetId assignments received before scene loads.
        /// Keyed by scene name. Cleared when assignments are applied.
        /// </summary>
        private static readonly Dictionary<string, BufferedGONetIdAssignments> bufferedGONetIdAssignmentsByScene = new Dictionary<string, BufferedGONetIdAssignments>();

        /// <summary>
        /// Tracks which clients have already received proactive GONetId assignments for each scene.
        /// Prevents duplicate sends when both proactive (server OnSceneLoaded) and reactive (client SceneLoadCompleteEvent) flows fire.
        /// Key: (sceneName, clientAuthorityId)
        /// - Proactive flow records clients when broadcasting GONetIds
        /// - Reactive flow checks this set to skip sending duplicates to early-joiners
        /// - Late-joiners (not in set) still receive GONetIds via reactive flow
        /// </summary>
        private static readonly HashSet<(string sceneName, ushort clientAuthorityId)> clientsReceivedProactiveGonetIds = new HashSet<(string, ushort)>();
        private const float PROACTIVE_GONETID_RESEND_COOLDOWN_SECONDS = 2.0f;
        private static readonly Dictionary<(string sceneName, ushort clientAuthorityId), long> lastProactiveGonetIdSyncSentTicks = new Dictionary<(string, ushort), long>();
        private static readonly Dictionary<(string sceneName, ushort clientAuthorityId), int> sceneLoadCompleteReceiptCounts = new Dictionary<(string, ushort), int>();

        /// <summary>
        /// Retrieves cached scene object initialization data for late-joiners.
        /// Returns null if not cached (object has no IGONetSyncdBehaviourInitializer or cache miss).
        /// </summary>
        internal static byte[] GetCachedSceneObjectInitData(string sceneName, string designTimeLocation)
        {
            string cacheKey = $"{sceneName}|{designTimeLocation}";
            bool found = sceneObjectInitDataCache.TryGetValue(cacheKey, out byte[] cachedData);

            if (found && cachedData != null)
            {
                GONetLog.Info($"[CACHE-HIT] Found cached init data for '{designTimeLocation}' in scene '{sceneName}' - {cachedData.Length} bytes");
            }
            else
            {
                GONetLog.Warning($"[CACHE-MISS] No cached init data for '{designTimeLocation}' in scene '{sceneName}' (total cache entries: {sceneObjectInitDataCache.Count})");
            }

            return cachedData; // null if not found
        }

        /// <summary>
        /// Checks if a client has already received proactive GONetId assignments for a scene.
        /// Used by reactive flow (Server_OnClientSceneLoadComplete) to avoid duplicate sends to early-joiners.
        /// </summary>
        /// <param name="sceneName">The scene name</param>
        /// <param name="clientAuthorityId">The client's authority ID</param>
        /// <returns>True if client already received proactive GONetIds, false if late-joiner needs reactive send</returns>
        internal static bool HasClientReceivedProactiveGonetIds(string sceneName, ushort clientAuthorityId)
        {
            return clientsReceivedProactiveGonetIds.Contains((sceneName, clientAuthorityId));
        }

        internal static void RecordProactiveGonetIdSyncSent(string sceneName, ushort clientAuthorityId)
        {
            long nowTicks = GONetMain.Time != null ? GONetMain.Time.ElapsedTicks : HighResolutionTimeUtils.UtcNowTicks;
            lastProactiveGonetIdSyncSentTicks[(sceneName, clientAuthorityId)] = nowTicks;
        }

        internal static bool ShouldResendProactiveGonetIds(string sceneName, ushort clientAuthorityId)
        {
            long nowTicks = GONetMain.Time != null ? GONetMain.Time.ElapsedTicks : HighResolutionTimeUtils.UtcNowTicks;
            if (!lastProactiveGonetIdSyncSentTicks.TryGetValue((sceneName, clientAuthorityId), out long lastTicks))
                return true;

            long cooldownTicks = (long)(PROACTIVE_GONETID_RESEND_COOLDOWN_SECONDS * TimeSpan.TicksPerSecond);
            return nowTicks - lastTicks >= cooldownTicks;
        }

        internal static int IncrementSceneLoadCompleteReceiptCount(string sceneName, ushort clientAuthorityId)
        {
            var key = (sceneName, clientAuthorityId);
            sceneLoadCompleteReceiptCounts.TryGetValue(key, out int count);
            count++;
            sceneLoadCompleteReceiptCounts[key] = count;
            return count;
        }

        /// <summary>
        /// Clears proactive GONetId tracking for a scene when it unloads.
        /// Prevents memory leaks and ensures fresh tracking if scene is reloaded.
        /// </summary>
        /// <param name="sceneName">The scene name</param>
        internal static void ClearProactiveGonetIdTrackingForScene(string sceneName)
        {
            clientsReceivedProactiveGonetIds.RemoveWhere(entry => entry.sceneName == sceneName);
            foreach (var key in lastProactiveGonetIdSyncSentTicks.Keys.Where(entry => entry.sceneName == sceneName).ToList())
            {
                lastProactiveGonetIdSyncSentTicks.Remove(key);
            }
            foreach (var key in sceneLoadCompleteReceiptCounts.Keys.Where(entry => entry.sceneName == sceneName).ToList())
            {
                sceneLoadCompleteReceiptCounts.Remove(key);
            }
            GONetLog.Debug($"[GONETID-TRACKING] Cleared proactive GONetId tracking for scene '{sceneName}'");
        }

        private IEnumerator Server_SyncSceneDefinedObjectIds_WhenReady(string sceneName, List<GONetParticipant> sceneParticipants)
        {
            // DIAGNOSTIC: Track coroutine start
            GONetLog.Info($"[SYNC-COROUTINE-START] Server starting SyncSceneDefinedObjectIds_WhenReady for scene '{sceneName}' with {sceneParticipants.Count} participants at time {GONetMain.Time.ElapsedSeconds:F3}s");

            // EARLY DEDUPLICATION: Record which clients will receive this proactive send
            // This MUST happen at coroutine START (not END) to prevent race condition:
            // - Coroutine takes ~1 second to complete
            // - Client might finish loading and send SceneLoadCompleteEvent before coroutine completes
            // - If we record clients after coroutine completes, reactive send will fire (duplicate!)
            // - By recording early, reactive send checks and skips duplicate for early-joiners
            int recordedClientCount = 0;
            foreach (GONetRemoteClient client in GONetMain.gonetServer.remoteClients)
            {
                ushort clientAuthorityId = client.ConnectionToClient.OwnerAuthorityId;
                clientsReceivedProactiveGonetIds.Add((sceneName, clientAuthorityId));
                recordedClientCount++;
            }
            GONetLog.Info($"[SYNC-COROUTINE-EARLY-RECORD] Recorded {recordedClientCount} clients as receiving proactive GONetIds for scene '{sceneName}' at time {GONetMain.Time.ElapsedSeconds:F3}s");

            // CRITICAL: Process ALL scene participants to send their GONetIds to clients
            // Init data is OPTIONAL (only for objects with IGONetSyncdBehaviourInitializer)
            // GONetId assignment is MANDATORY (all scene objects need them)
            //
            // This happens SYNCHRONOUSLY (not after yield) because:
            // 1. Update() runs every frame starting immediately after scene load
            // 2. Spawner_SerializeSpawnData() sets isInitialized=true and randomizes values
            // 3. Server needs initialization even if no clients are connected
            //
            // We cache the serialized data to avoid calling Spawner_SerializeSpawnData() twice
            // (which would generate different random values!)
            List<string> designTimeLocations = new List<string>();
            List<uint> gonetIds = new List<uint>();
            List<byte[]> customInitDataList = new List<byte[]>();

            int participantProcessedCount = 0;
            int participantWithInitDataCount = 0;
            foreach (var participant in sceneParticipants)
            {
                participantProcessedCount++;
                if (participant != null &&
                    participant.IsDesignTimeMetadataInitd &&
                    !string.IsNullOrEmpty(participant.DesignTimeLocation))
                {
                    // ALWAYS add participant to send list (GONetIds are mandatory for all)
                    designTimeLocations.Add(participant.DesignTimeLocation);

                    // Serialize initialization data - this ALSO initializes the server's own instance!
                    // Spawner_SerializeSpawnData() sets fields and isInitialized=true
                    // NOTE: This can be null if participant doesn't implement IGONetSyncdBehaviourInitializer
                    byte[] initData = GONetMain.SerializeSceneObjectInitData(participant);

                    // Cache for late-joiners (avoids re-randomization on second serialization call)
                    // Key format: sceneName|designTimeLocation to handle same prefab in different scenes
                    if (initData != null)
                    {
                        string cacheKey = $"{sceneName}|{participant.DesignTimeLocation}";
                        sceneObjectInitDataCache[cacheKey] = initData;
                        GONetLog.Info($"[CACHE-ADD] Cached init data for '{participant.DesignTimeLocation}' in scene '{sceneName}' - {initData.Length} bytes (total cache entries: {sceneObjectInitDataCache.Count})");
                        participantWithInitDataCount++;
                    }
                    else
                    {
                        //GONetLog.Debug($"[CACHE-SKIP] No init data to cache for '{participant.DesignTimeLocation}' (no IGONetSyncdBehaviourInitializer)");
                    }

                    // Add init data to list (can be null)
                    customInitDataList.Add(initData);
                }
            }

            // DIAGNOSTIC: Show processing summary before yield
            GONetLog.Info($"[SYNC-COROUTINE-PRE-YIELD] Processed {participantProcessedCount} participants, {participantWithInitDataCount} with init data, designTimeLocations.Count={designTimeLocations.Count} at time {GONetMain.Time.ElapsedSeconds:F3}s");

            // Wait a frame to ensure all GONetIds have been assigned
            yield return null;

            // DIAGNOSTIC: Track post-yield continuation
            GONetLog.Info($"[SYNC-COROUTINE-POST-YIELD] Resumed after yield, collecting GONetIds at time {GONetMain.Time.ElapsedSeconds:F3}s");

            // Now collect GONetIds (which are guaranteed to be assigned after the yield)
            int gonetIdCollectedCount = 0;
            int gonetIdMissingCount = 0;
            for (int i = 0; i < designTimeLocations.Count; i++)
            {
                string location = designTimeLocations[i];

                // Find participant by location (linear search - could be optimized with dictionary)
                GONetParticipant participant = sceneParticipants.Find(p =>
                    p != null &&
                    p.IsDesignTimeMetadataInitd &&
                    p.DesignTimeLocation == location);

                if (participant != null && participant.GONetId != 0)
                {
                    gonetIds.Add(participant.GONetId);
                    gonetIdCollectedCount++;
                }
                else
                {
                    GONetLog.Warning($"[GONetGlobal] Could not find GONetId for participant at location '{location}'");
                    gonetIds.Add(0); // Placeholder to maintain array alignment
                    gonetIdMissingCount++;
                }
            }

            // DIAGNOSTIC: Show GONetId collection results
            GONetLog.Info($"[SYNC-COROUTINE-GONETIDS] Collected {gonetIdCollectedCount} GONetIds, {gonetIdMissingCount} missing for scene '{sceneName}' at time {GONetMain.Time.ElapsedSeconds:F3}s");

            // DIAGNOSTIC: Show connection check details
            int numConnections = (GONetMain.gonetServer != null) ? (int)GONetMain.gonetServer.numConnections : -1;
            bool hasServer = GONetMain.gonetServer != null;
            bool hasLocations = designTimeLocations.Count > 0;
            bool hasConnections = numConnections > 0;
            GONetLog.Info($"[SYNC-COROUTINE-CHECK] Scene '{sceneName}': hasLocations={hasLocations} (count={designTimeLocations.Count}), hasServer={hasServer}, numConnections={numConnections}, hasConnections={hasConnections} at time {GONetMain.Time.ElapsedSeconds:F3}s");

            // Only send RPCs if there are connected clients
            if (GONetMain.gonetServer != null && GONetMain.gonetServer.numConnections > 0)
            {
                if (designTimeLocations.Count > 0)
                {
                    // STEP 1: Try to build compressed message using indices
                    ushort[] locationIndices = new ushort[designTimeLocations.Count];
                    bool canUseCompressed = true;
                    int failedIndexCount = 0;

                    for (int i = 0; i < designTimeLocations.Count; i++)
                    {
                        ushort locationIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(designTimeLocations[i]);

                        if (locationIndex == ushort.MaxValue)
                        {
                            // Couldn't get index - metadata missing or library not loaded
                            failedIndexCount++;
                            canUseCompressed = false;
                            GONetLog.Warning($"[GONETID-COMPRESS] Failed to get location index for '{designTimeLocations[i]}'");
                        }

                        locationIndices[i] = locationIndex;
                    }

                    // STEP 2: Get metadata count for client validation
                    ushort expectedMetadataCount = (ushort)GONetSpawnSupport_Runtime.GetTotalMetadataCount();

                    // STEP 3: Send compressed or fallback to full paths
                    if (canUseCompressed)
                    {
                        int uncompressedSize = designTimeLocations.Sum(loc => loc.Length);
                        int compressedSize = locationIndices.Length * 2;
                        GONetLog.Info($"[GONETID-COMPRESS] SERVER sending COMPRESSED GONetId sync for '{sceneName}' - {designTimeLocations.Count} objects, {compressedSize} bytes indices (vs {uncompressedSize} bytes paths), metadata count: {expectedMetadataCount}, bandwidth saved: {uncompressedSize - compressedSize} bytes ({(float)(uncompressedSize - compressedSize) / uncompressedSize * 100:F1}%)");
                        SendSceneDefinedObjectIdSync_Compressed(sceneName, expectedMetadataCount, locationIndices, gonetIds.ToArray(), customInitDataList.ToArray());
                    }
                    else
                    {
                        // Fallback to full paths (current system)
                        GONetLog.Info($"[GONETID] SERVER sending GONetId sync for '{sceneName}' using full paths - {designTimeLocations.Count} objects");
                        SendSceneDefinedObjectIdSync(sceneName, designTimeLocations.ToArray(), gonetIds.ToArray(), customInitDataList.ToArray());
                    }

                    // Note: Client recording for deduplication happens at START of coroutine (not here)
                    // This prevents race condition where client finishes loading before coroutine completes
                    GONetLog.Info($"[SYNC-COROUTINE-SENT] Successfully sent proactive GONetId sync RPC for scene '{sceneName}' at time {GONetMain.Time.ElapsedSeconds:F3}s");
                }
                else
                {
                    GONetLog.Info($"[GONETID-EMPTY] SERVER sending empty GONetId sync for '{sceneName}' (no scene-defined objects)");
                    SendSceneDefinedObjectIdSync(sceneName, Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<byte[]>());
                }

                // CRITICAL FIX (November 2025): Also send AllCurrentValues for scene-defined objects to early-joiners
                // Without this, early-joiner clients receive GONetIds but not the current state values,
                // causing objects to appear at wrong positions (delta sync only works from a known base state)
                foreach (GONetRemoteClient client in GONetMain.gonetServer.remoteClients)
                {
                    if (designTimeLocations.Count > 0)
                    {
                        GONetMain.Server_SendClientCurrentState_ForSceneDefinedObjects(sceneName, client.ConnectionToClient.OwnerAuthorityId);
                    }

                    // NOW resume sync for this early-joiner (GONetIds + AllCurrentValues have been sent)
                    client.IsCurrentlyLoadingScene = false;
                    client.CurrentlyLoadingSceneName = null;
                    RecordProactiveGonetIdSyncSent(sceneName, client.ConnectionToClient.OwnerAuthorityId);
                    string sendSummary = designTimeLocations.Count > 0 ? "proactive GONetIds + AllCurrentValues" : "empty GONetId sync";
                    GONetLog.Info($"[PROACTIVE-COMPLETE] Client {client.ConnectionToClient.OwnerAuthorityId} - resuming unreliable sync after {sendSummary} sent for '{sceneName}'");
                }
            }
            else
            {
                GONetLog.Warning($"[SYNC-COROUTINE-SKIP] NOT sending GONetId sync RPC for scene '{sceneName}' - Reason: hasLocations={hasLocations}, hasServer={hasServer}, numConnections={numConnections} at time {GONetMain.Time.ElapsedSeconds:F3}s");
            }
        }

        private static void FindAndAppend<T>(GameObject[] gameObjects, /* IN/OUT */ List<T> listToAppend, Func<T, bool> filter)
        {
            int count = gameObjects != null ? gameObjects.Length : 0;
            for (int i = 0; i < count; ++i)
            {
                T t = gameObjects[i].GetComponent<T>();
                if (t != null && filter(t))
                {
                    listToAppend.Add(t);
                }
                foreach (Transform childTransform in gameObjects[i].transform)
                {
                    FindAndAppend(new[] { childTransform.gameObject }, listToAppend, filter);
                }
            }
        }

        /// <summary>
        /// Frame start time for CPU throttling calculations.
        /// </summary>
        private float cpuThrottleFrameStartTime;

        private void Update()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Store frame start time for CPU throttling (must be first!)
            cpuThrottleFrameStartTime = UnityEngine.Time.realtimeSinceStartup;
#endif

            GONetMain.Update(this);

            // Process deferred RPCs - handle cases where GONetParticipants weren't available during initial processing
            GONetEventBus.ProcessDeferredRpcs();

            // GONetId Reuse Prevention: Periodic cleanup of expired despawned GONetIds
            GONetMain.CleanupExpiredDespawnedGONetIds();

            // DIAGNOSTIC: Periodic resource usage logging (every 5 seconds)
            // DISABLED: Causes visible stutters due to expensive diagnostic gathering
            // LogPeriodicDiagnostics();

            // UI TOGGLE: Left Shift + X to disable/enable all GONet UI (for profiling)
            if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.X))
            {
                ToggleUI();
            }

            // Apply UI state if it changed (from toggle or other sources)
            if (lastUIState != uiEnabled)
            {
                ApplyUIState();
                lastUIState = uiEnabled;
            }

            // DEBUG: Ctrl+Alt+K to toggle kinematic state for all physics objects (DEVELOPMENT ONLY)
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.K))
            {
                DebugToggleAllPhysicsObjectsKinematic();
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // CPU throttling: Apply frame sleep at end of Update to simulate slow CPU
            // This is more predictable than external tools like BES
            GONetCommandLineParser.ApplyFrameThrottling(cpuThrottleFrameStartTime);
#endif
        }

        #region UI Toggle System

        private static bool uiEnabled = true;
        private static bool lastUIState = true;
        private static Canvas[] cachedCanvases = null;
        private static MonoBehaviour[] cachedUIComponents = null;

        /// <summary>
        /// Global UI enabled state. When false, all GONet UI components stop updating.
        /// </summary>
        public static bool IsUIEnabled => uiEnabled;

        private static void ToggleUI()
        {
            uiEnabled = !uiEnabled;
            GONetLog.Info($"[UI-TOGGLE] UI {(uiEnabled ? "ENABLED" : "DISABLED")} (Press Left Shift + X to toggle)");
            ApplyUIState();
        }

        private static void ApplyUIState()
        {
            // Find all canvases in the scene (including DontDestroyOnLoad)
            cachedCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true); // Include inactive objects

            int canvasCount = 0;
            foreach (Canvas canvas in cachedCanvases)
            {
                // Only toggle GONet-related canvases (check for GONet components in hierarchy)
                if (IsGONetCanvas(canvas))
                {
                    canvas.enabled = uiEnabled;
                    canvasCount++;
                }
            }

            // Also find all GONet UI components and disable their Update() methods
            cachedUIComponents = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
            int componentCount = 0;
            foreach (var component in cachedUIComponents)
            {
                // Check if it's a GONet UI component (Sample namespace ending with "UI")
                if (component != null && component.GetType().Namespace == "GONet.Sample" &&
                    component.GetType().Name.EndsWith("UI"))
                {
                    component.enabled = uiEnabled;
                    componentCount++;
                }
            }

            GONetLog.Info($"[UI-TOGGLE] Toggled {canvasCount} canvases and {componentCount} UI components to {(uiEnabled ? "enabled" : "disabled")}");
        }

        private static bool IsGONetCanvas(Canvas canvas)
        {
            // Check if canvas or any parent has GONet-related components
            Transform current = canvas.transform;
            while (current != null)
            {
                // Check for GONet namespace components
                foreach (var component in current.GetComponents<MonoBehaviour>())
                {
                    if (component != null && component.GetType().Namespace != null &&
                        component.GetType().Namespace.StartsWith("GONet"))
                    {
                        return true;
                    }
                }
                current = current.parent;
            }
            return false;
        }

        #endregion

        #region Diagnostic Logging

        private static float _lastDiagnosticLogTime = 0f;
        private const float DIAGNOSTIC_LOG_INTERVAL_SECONDS = 5.0f;

        /// <summary>
        /// DIAGNOSTIC: Periodic resource usage logging to identify memory/CPU leaks.
        /// Logs every 5 seconds with counts of active resources.
        /// </summary>
        private void LogPeriodicDiagnostics()
        {
            float currentTime = UnityEngine.Time.time;
            if (currentTime - _lastDiagnosticLogTime < DIAGNOSTIC_LOG_INTERVAL_SECONDS)
            {
                return;
            }

            _lastDiagnosticLogTime = currentTime;

            // Get counts from GONetMain
            var diagnostics = GONetMain.GetResourceDiagnostics();

            // Get EventBus performance metrics
            string eventBusMetrics = GONetEventBus.Instance != null ? GONetEventBus.Instance.GetPerformanceMetrics() : "N/A";

            // Build persistence queue info with CPU/memory metrics (when enabled)
            string persistenceInfo = $"PersistenceQueue: {diagnostics.PersistenceQueueSize}";

            // Add CPU monitoring if enabled
            if (persistenceQueueMaxCpuTimeMs > 0 && GONetMain.persistenceQueueLastProcessingTimeMs > 0)
            {
                persistenceInfo += $" (CPU: {GONetMain.persistenceQueueLastProcessingTimeMs:F2}ms)";
            }

            // Add memory monitoring if enabled
            if (persistenceQueueMaxMemoryMB > 0)
            {
                int memMB = GONetMain.GetApproximateQueueMemoryMB();
                persistenceInfo += $" (Mem: {memMB}MB)";
            }

            GONetLog.Info($"[DIAGNOSTICS] " +
                         $"SyncCompanions: {diagnostics.ActiveSyncCompanionCount} " +
                         $"(nullParticipants: {diagnostics.NullParticipantCount}), " +
                         $"DeferredRPCs: {diagnostics.DeferredRpcCount}, " +
                         $"GONetParticipants: {diagnostics.ActiveGONetParticipantCount}, " +
                         $"RecentlyDespawned: {diagnostics.RecentlyDespawnedCount}, " +
                         $"EventBusSubscriptions: {diagnostics.EventBusSubscriptionCount}, " +
                         $"PoolBorrowed: {diagnostics.PoolBorrowedCount}, " +
                         $"{persistenceInfo}, " +
                         $"EventBus: {eventBusMetrics}");
        }

        #endregion

        /// <summary>
        /// DEBUG ONLY: Toggle kinematic state for all physics objects and log the results.
        /// Triggered by Ctrl+Alt+K key combination.
        /// This is for development/testing purposes to verify physics snapping behavior.
        /// </summary>
        private void DebugToggleAllPhysicsObjectsKinematic()
        {
            GONetParticipant[] allParticipants = UnityEngine.Object.FindObjectsOfType<GONetParticipant>();

            int physicsObjectCount = 0;
            int toggledCount = 0;
            bool? newKinematicState = null; // Will be set based on first object found

            System.Text.StringBuilder sb = new System.Text.StringBuilder(2048);
            sb.AppendLine("========================================");
            sb.AppendLine("[DEBUG-KINEMATIC] Ctrl+Alt+K pressed - Toggling physics objects");
            sb.AppendLine("========================================");

            foreach (var participant in allParticipants)
            {
                if (participant.IsRigidBodyOwnerOnlyControlled && participant.myRigidBody != null)
                {
                    physicsObjectCount++;

                    // Determine toggle state based on first object (all will match)
                    if (!newKinematicState.HasValue)
                    {
                        newKinematicState = !participant.myRigidBody.isKinematic;
                    }

                    bool oldKinematic = participant.myRigidBody.isKinematic;
                    participant.myRigidBody.isKinematic = newKinematicState.Value;
                    toggledCount++;

                    sb.AppendLine($"[{participant.gameObject.name}] GONetId:{participant.GONetId} IsMine:{participant.IsMine}");
                    sb.AppendLine($"  BEFORE: isKinematic={oldKinematic}");
                    sb.AppendLine($"  AFTER:  isKinematic={participant.myRigidBody.isKinematic}");
                    sb.AppendLine($"  Position: {participant.transform.position}");
                    sb.AppendLine($"  Rotation: {participant.transform.rotation.eulerAngles}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("========================================");
            sb.AppendLine($"[DEBUG-KINEMATIC] Total GONetParticipants: {allParticipants.Length}");
            sb.AppendLine($"[DEBUG-KINEMATIC] Physics objects found: {physicsObjectCount}");
            sb.AppendLine($"[DEBUG-KINEMATIC] Objects toggled: {toggledCount}");
            sb.AppendLine($"[DEBUG-KINEMATIC] New kinematic state: {(newKinematicState.HasValue ? newKinematicState.Value.ToString() : "N/A - no physics objects found")}");
            sb.AppendLine("========================================");

            GONetLog.Warning(sb.ToString()); // Use Warning level so it's always visible
        }

        /// <summary>
        /// Unity's FixedUpdate() hook - Calls GONetMain.FixedUpdate_AfterGONetReady() for physics frame updates.
        /// Runs at Unity's fixed timestep (default: 50Hz / 0.02 seconds).
        /// </summary>
        private void FixedUpdate()
        {
            GONetMain.FixedUpdate_AfterGONetReady();
        }

        private void OnApplicationQuit()
        {
            // DIAGNOSTIC DUMP: Log lifecycle state of ALL GONetParticipants before shutdown
            // This helps us understand what prevented OnGONetReady from firing
            //DumpLifecycleStateDiagnostics();

            GONetMain.Shutdown();
        }

        /// <summary>
        /// Diagnostic dump of all GONetParticipants showing which lifecycle gates prevented OnGONetReady.
        /// Called on application quit to capture final state for analysis.
        /// </summary>
        private void DumpLifecycleStateDiagnostics()
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(4096);
                sb.AppendLine("========================================");
                sb.AppendLine("[QUIT-DIAGNOSTIC] Application quitting - dumping GONetParticipant lifecycle states");
                sb.AppendLine("========================================");

                // Find ALL GONetParticipants (even destroyed ones might still exist)
                GONetParticipant[] allParticipants = UnityEngine.Object.FindObjectsOfType<GONetParticipant>(includeInactive: true);

                sb.AppendLine($"[QUIT-DIAGNOSTIC] Found {allParticipants.Length} total GONetParticipants");
                sb.AppendLine();

                int neverFiredOnGONetReady = 0;
                int awakeIncomplete = 0;
                int startIncomplete = 0;
                int deserializeIncomplete = 0;
                int missingGONetId = 0;
                int missingAuthority = 0;

                foreach (var participant in allParticipants)
                {
                    if (participant == null) continue; // Unity fake null check

                    bool firedReady = participant.didOnGONetReadyFire;
                    if (!firedReady)
                    {
                        neverFiredOnGONetReady++;

                        // Log detailed state for participants that never fired OnGONetReady
                        sb.AppendLine($"[QUIT-DIAGNOSTIC] NEVER FIRED OnGONetReady:");
                        sb.AppendLine($"  InstanceID: {participant.GetInstanceID()}");
                        sb.AppendLine($"  GameObject: {participant.gameObject.name}");
                        sb.AppendLine($"  GONetId: {participant.GONetId} (Unset={participant.GONetId == GONetParticipant.GONetId_Unset})");
                        sb.AppendLine($"  OwnerAuthorityId: {participant.OwnerAuthorityId} (Unset={participant.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Unset})");
                        sb.AppendLine($"  IsMine: {participant.IsMine}");
                        sb.AppendLine($"  WasInstantiated: {participant.WasInstantiated}");
                        sb.AppendLine($"  IsInternallyConfigured: {participant.IsInternallyConfigured}");
                        sb.AppendLine($"  LIFECYCLE GATES:");
                        sb.AppendLine($"    didAwakeComplete: {participant.didAwakeComplete}");
                        sb.AppendLine($"    didStartComplete: {participant.didStartComplete}");
                        sb.AppendLine($"    requiresDeserializeInit: {participant.requiresDeserializeInit}");
                        sb.AppendLine($"    didDeserializeInitComplete: {participant.didDeserializeInitComplete}");
                        sb.AppendLine($"    didOnGONetReadyFire: {participant.didOnGONetReadyFire}");
                        sb.AppendLine($"  CLIENT LIMBO STATE:");
                        sb.AppendLine($"    client_isInLimbo: {participant.client_isInLimbo}");
                        sb.AppendLine();

                        // Count failure reasons
                        if (!participant.didAwakeComplete) awakeIncomplete++;
                        if (!participant.didStartComplete) startIncomplete++;
                        if (participant.requiresDeserializeInit && !participant.didDeserializeInitComplete) deserializeIncomplete++;
                        if (participant.GONetId == GONetParticipant.GONetId_Unset) missingGONetId++;
                        if (participant.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Unset) missingAuthority++;
                    }
                }

                // Summary statistics
                sb.AppendLine("========================================");
                sb.AppendLine("[QUIT-DIAGNOSTIC] SUMMARY:");
                sb.AppendLine($"  Total participants: {allParticipants.Length}");
                sb.AppendLine($"  OnGONetReady fired: {allParticipants.Length - neverFiredOnGONetReady}");
                sb.AppendLine($"  OnGONetReady NEVER fired: {neverFiredOnGONetReady}");
                sb.AppendLine();
                sb.AppendLine("  Failure breakdown (participants may have multiple issues):");
                sb.AppendLine($"    didAwakeComplete = false: {awakeIncomplete}");
                sb.AppendLine($"    didStartComplete = false: {startIncomplete}");
                sb.AppendLine($"    Deserialization incomplete: {deserializeIncomplete}");
                sb.AppendLine($"    GONetId unset: {missingGONetId}");
                sb.AppendLine($"    OwnerAuthorityId unset: {missingAuthority}");
                sb.AppendLine("========================================");

                // Output entire diagnostic as ONE log statement
                GONetLog.Info(sb.ToString());
            }
            catch (System.Exception ex)
            {
                GONetLog.Error($"[QUIT-DIAGNOSTIC] Exception during lifecycle dump: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private System.Collections.IEnumerator CacheDesignTimeMetadata_ThenContinueInit()
        {
            // Start the caching process and wait for it to complete
            bool cachingComplete = false;
            GONetSpawnSupport_Runtime.CacheAllProjectDesignTimeMetadata(this, () => cachingComplete = true);

            // Wait until caching is actually complete
            while (!cachingComplete)
            {
                yield return null;
            }

            GONetLog.Debug("GONetGlobal: Design time metadata caching completed - ready for scene processing");
        }

        // ========================================
        // SCENE MANAGEMENT RPCs (Phase 5)
        // ========================================
        //
        // IMPORTANT NOTES FOR ADDING RPCs TO CLASSES IN NAMESPACES:
        //
        // 1. RPC methods can be internal or public (both work with code generator)
        // 2. Classes with RPCs MUST derive from GONetParticipantCompanionBehaviour
        // 3. Classes CAN be in namespaces - generator handles this correctly
        // 4. For TargetRpc with validation:
        //    - Use property-based targeting: [TargetRpc(nameof(PropertyName), validationMethod: nameof(ValidateMethod))]
        //    - First constructor parameter must be a property/field name (string), NOT RpcTarget enum
        //    - Validation methods must return RpcValidationResult (not bool)
        //    - Validation methods must use ref parameters matching the RPC signature
        //    - Example: private RpcValidationResult ValidateMyRpc(ref string param1, ref int param2)
        // 5. Get validation context via GONetMain.EventBus.GetValidationContext()
        // 6. Use context.GetValidationResult() to get the result object
        // 7. Call result.AllowAll() or result.DenyAll() to control RPC execution
        //
        // See RPC_RequestLoadScene and Validate_RequestLoadScene below for complete examples.
        // ========================================

        /// <summary>
        /// Property that returns the server's authority ID for TargetRpc targeting.
        /// </summary>
        internal ushort ServerAuthorityId => GONetMain.OwnerAuthorityId_Server;

        /// <summary>
        /// TARGET RPC: Request server to load a scene (usable by both clients and server).
        /// Uses TargetRpc for built-in validation support.
        /// </summary>
        [TargetRpc(nameof(ServerAuthorityId), validationMethod: nameof(Validate_RequestLoadScene))]
        internal async Task<RpcDeliveryReport> RPC_RequestLoadScene(string sceneName, byte modeRaw, byte loadTypeRaw)
        {
            // IMPORTANT: This RPC should only execute on the server
            // When called from client, it sends to server but also executes locally
            // We need to check if we're the server before processing
            if (!GONetMain.IsServer)
            {
                // This is the client-side call that triggers the RPC send
                // Don't execute the logic here, just let it send to server
                return default;
            }

            LoadSceneMode mode = (LoadSceneMode)modeRaw;
            SceneLoadType loadType = (SceneLoadType)loadTypeRaw;

            GONetLog.Info($"[GONetGlobal] Scene load request received: '{sceneName}' (Mode: {mode}, Type: {loadType})");

            // IMPORTANT: If async approval is required, validation hook will show UI
            // and the actual scene load happens when user approves (in SceneSelectionUI.OnApproveClicked)
            // so we must NOT load the scene here - just let validation handle it
            bool requiresAsyncApproval = GONetMain.SceneManager.RequiresAsyncApproval;
            GONetLog.Info($"[GONetGlobal] Checking RequiresAsyncApproval: {requiresAsyncApproval}");
            if (requiresAsyncApproval)
            {
                GONetLog.Info($"[GONetGlobal] Scene load requires async approval - validation will handle UI, scene will load after approval");
                return default;
            }
            else
            {
                GONetLog.Info($"[GONetGlobal] RequiresAsyncApproval is FALSE - proceeding with immediate scene load");
            }

            // Forward to scene manager (only when async approval NOT required)
            if (loadType == SceneLoadType.BuildSettings)
            {
                GONetMain.SceneManager.LoadSceneFromBuildSettings(sceneName, mode);
            }
#if ADDRESSABLES_AVAILABLE
            else if (loadType == SceneLoadType.Addressables)
            {
                GONetMain.SceneManager.LoadSceneFromAddressables(sceneName, mode);
            }
#endif
            else
            {
                GONetLog.Error($"[GONetGlobal] Unsupported scene load type: {loadType}");
            }

            return default;
        }

        /// <summary>
        /// Validation method for scene load requests.
        /// Called by TargetRpc system before executing RPC_RequestLoadScene.
        /// </summary>
        internal RpcValidationResult Validate_RequestLoadScene(ref string sceneName, ref byte modeRaw, ref byte loadTypeRaw)
        {
            LoadSceneMode mode = (LoadSceneMode)modeRaw;

            // Get validation context and result
            var context = GONetMain.EventBus.GetValidationContext();
            if (!context.HasValue)
            {
                GONetLog.Error("[GONetGlobal] No validation context available for scene load request");
                var errorResult = RpcValidationResult.CreatePreAllocated(0);
                errorResult.DenyAll("No validation context");
                return errorResult;
            }

            var validationContext = context.Value;
            var result = validationContext.GetValidationResult();
            ushort callerAuthorityId = validationContext.SourceAuthorityId;

            // Use scene manager's validation hook
            var sceneManager = GONetMain.SceneManager;
            bool allowed = sceneManager.InvokeValidation(sceneName, mode, callerAuthorityId);
            if (!allowed)
            {
                GONetLog.Warning($"[GONetGlobal] Scene load request denied by validation: '{sceneName}' from client {callerAuthorityId}");
                result.DenyAll($"Scene load denied for '{sceneName}'");
                return result;
            }

            // Allow all targets (in this case, should only be the server)
            result.AllowAll();

            // If validation requires async approval (e.g., server UI), set ExpectFollowOnResponse
            // This signals to the caller that a follow-on RPC will be sent with the final decision
            result.ExpectFollowOnResponse = sceneManager.RequiresAsyncApproval;

            return result;
        }

        /// <summary>
        /// TARGET RPC: Server sends scene load request response to client.
        /// Uses first ushort parameter to specify target client authority ID.
        /// </summary>
        /// <param name="targetClientId">Authority ID of the client to receive the response</param>
        /// <param name="approved">True if request was approved, false if denied</param>
        /// <param name="sceneName">Name of the scene that was requested</param>
        /// <param name="denialReason">If denied, the reason for denial (optional)</param>
        [TargetRpc]
        internal void RPC_SceneRequestResponse(ushort targetClientId, bool approved, string sceneName, string denialReason = "")
        {
            if (approved)
            {
                GONetLog.Info($"[GONetGlobal] Scene request approved: '{sceneName}'");
            }
            else
            {
                string reason = string.IsNullOrEmpty(denialReason) ? "Request denied" : denialReason;
                GONetLog.Warning($"[GONetGlobal] Scene request denied: '{sceneName}' - {reason}");
            }

            // Notify scene manager of response
            GONetMain.SceneManager.InvokeSceneRequestResponse(approved, sceneName, denialReason);
        }

        /// <summary>
        /// INTERNAL: Sends scene load request RPC to server.
        /// <para><b>USER NOTE:</b> This method is internal infrastructure and should NOT be called by user code.</para>
        /// <para>Due to GONet being in the same assembly as your game code, internal methods are technically accessible,
        /// but calling them directly bypasses the intended public API.</para>
        /// <para><b>Instead, use:</b> <c>GONetMain.SceneManager.RequestLoadBuildSettingsScene(...)</c> or <c>RequestLoadAddressablesScene(...)</c></para>
        /// </summary>
        internal void SendSceneLoadRequest(string sceneName, byte modeRaw, byte loadTypeRaw)
        {
            CallRpc(nameof(RPC_RequestLoadScene), sceneName, modeRaw, loadTypeRaw);
        }

        /// <summary>
        /// INTERNAL: Sends scene unload request RPC to server.
        /// <para><b>USER NOTE:</b> This method is internal infrastructure and should NOT be called by user code.</para>
        /// <para>Due to GONet being in the same assembly as your game code, internal methods are technically accessible,
        /// but calling them directly bypasses the intended public API.</para>
        /// <para><b>Instead, use:</b> <c>GONetMain.SceneManager.RequestUnloadScene(...)</c></para>
        /// </summary>
        internal void SendSceneUnloadRequest(string sceneName)
        {
            CallRpc(nameof(RPC_RequestUnloadScene), sceneName);
        }

        /// <summary>
        /// INTERNAL: Sends scene request response RPC to client.
        /// <para><b>USER NOTE:</b> This method is internal infrastructure and should NOT be called by user code.</para>
        /// <para>Due to GONet being in the same assembly as your game code, internal methods are technically accessible,
        /// but calling them directly bypasses the intended public API.</para>
        /// <para><b>Instead, use:</b> <c>GONetMain.SceneManager.SendSceneRequestResponse(...)</c></para>
        /// </summary>
        internal void SendSceneRequestResponse(ushort clientId, bool approved, string sceneName, string reason = "")
        {
            CallRpc(nameof(RPC_SceneRequestResponse), clientId, approved, sceneName, reason);
        }

        // ========================================
        // POOLING RPCs
        // ========================================

        [ServerRpc(IsMineRequired = false)]
        internal void RPC_RequestBorrowFromPool(PoolBorrowRequest request)
        {
            var context = GONetEventBus.CurrentRpcContext;
            if (!context.HasValue)
            {
                if (GONetMain.IsServer && GONetMain.IsClient && GONetMain.MyAuthorityId != GONetMain.OwnerAuthorityId_Unset)
                {
                    GONetLog.Warning("[POOL] Borrow request missing RPC context; treating as local host request.");
                    GONetPoolManager.Server_HandleBorrowRequest(request, GONetMain.MyAuthorityId);
                    return;
                }

                GONetLog.Error("[POOL] Borrow request received without RPC context.");
                return;
            }

            ushort requesterAuthorityId = context.Value.SourceAuthorityId;
            GONetPoolManager.Server_HandleBorrowRequest(request, requesterAuthorityId);
        }

        [ServerRpc(IsMineRequired = false)]
        internal void RPC_RequestReturnToPool(uint gonetId)
        {
            var context = GONetEventBus.CurrentRpcContext;
            if (!context.HasValue)
            {
                if (GONetMain.IsServer && GONetMain.IsClient && GONetMain.MyAuthorityId != GONetMain.OwnerAuthorityId_Unset)
                {
                    GONetLog.Warning("[POOL] Return request missing RPC context; treating as local host request.");
                    GONetPoolManager.Server_HandleReturnRequest(gonetId, GONetMain.MyAuthorityId);
                    return;
                }

                GONetLog.Error("[POOL] Return request received without RPC context.");
                return;
            }

            ushort requesterAuthorityId = context.Value.SourceAuthorityId;
            GONetPoolManager.Server_HandleReturnRequest(gonetId, requesterAuthorityId);
        }

        [TargetRpc]
        internal void RPC_PoolBorrowResponse(ushort targetClientId, uint requestId, byte statusRaw, uint gonetId)
        {
            GONetPoolManager.OnBorrowResponseReceived(requestId, (PoolBorrowResponseStatus)statusRaw, gonetId);
        }

        internal void SendPoolBorrowRequest(PoolBorrowRequest request)
        {
            CallRpc(nameof(RPC_RequestBorrowFromPool), request);
        }

        internal void SendPoolReturnRequest(uint gonetId)
        {
            CallRpc(nameof(RPC_RequestReturnToPool), gonetId);
        }

        internal void SendPoolBorrowResponse(ushort targetClientId, uint requestId, PoolBorrowResponseStatus status, uint gonetId)
        {
            CallRpc(nameof(RPC_PoolBorrowResponse), targetClientId, requestId, (byte)status, gonetId);
        }

        /// <summary>
        /// TARGET RPC: Request server to unload a scene (usable by both clients and server).
        /// Uses TargetRpc for built-in validation support.
        /// </summary>
        [TargetRpc(nameof(ServerAuthorityId), validationMethod: nameof(Validate_RequestUnloadScene))]
        internal void RPC_RequestUnloadScene(string sceneName)
        {
            GONetLog.Info($"[GONetGlobal] Scene unload request received: '{sceneName}'");
            GONetMain.SceneManager.UnloadScene(sceneName);
        }

        /// <summary>
        /// Validation method for scene unload requests.
        /// Called by TargetRpc system before executing RPC_RequestUnloadScene.
        /// </summary>
        internal RpcValidationResult Validate_RequestUnloadScene(ref string sceneName)
        {
            // Get validation context and result
            var context = GONetMain.EventBus.GetValidationContext();
            if (!context.HasValue)
            {
                GONetLog.Error("[GONetGlobal] No validation context available for scene unload request");
                var errorResult = RpcValidationResult.CreatePreAllocated(0);
                errorResult.DenyAll("No validation context");
                return errorResult;
            }

            var result = context.Value.GetValidationResult();

            // Can add validation hook for unload if needed in future
            // For now, allow all unload requests (scene manager will validate if scene is loaded)
            result.AllowAll();
            return result;
        }

        /// <summary>
        /// TARGET RPC: Server sends scene-defined object GONetId assignments to client(s).
        /// First parameter specifies target: use OwnerAuthorityId_Unset for all clients, or specific authority ID for single client.
        /// Called after client initialization is complete, so all scene objects should be ready.
        /// </summary>
        [TargetRpc(RpcTarget.SpecificAuthority)]
        internal void RPC_SyncSceneDefinedObjectIds(ushort targetClientId, string sceneName, string[] designTimeLocations, uint[] gonetIds, byte[][] customInitData)
        {
            // Only process on clients
            if (GONetMain.IsServer)
                return;

            // Check if scene is loaded yet
            Scene scene = GONetMain.SceneManager.GetSceneByName(sceneName);

            if (!scene.isLoaded)
            {
                // Scene not loaded yet - BUFFER assignments for later
                bufferedGONetIdAssignmentsByScene[sceneName] = new BufferedGONetIdAssignments
                {
                    sceneName = sceneName,
                    designTimeLocations = designTimeLocations,
                    gonetIds = gonetIds,
                    customInitData = customInitData,
                    receivedAtTime = GONetMain.Time.ElapsedSeconds
                };
                GONetLog.Info($"[GONetId-BUFFER] Buffered GONetId assignments for scene '{sceneName}' (scene not loaded yet, {designTimeLocations.Length} objects, time: {GONetMain.Time.ElapsedSeconds:F3}s)");
                return;
            }

            // Scene already loaded - apply immediately
            ApplyGONetIdAssignments(sceneName, designTimeLocations, gonetIds, customInitData);
        }

        /// <summary>
        /// CLIENT RPC: Server sends scene-defined object GONetId assignments to ALL connected clients (COMPRESSED - PROACTIVE PATH).
        /// Uses 16-bit location indices instead of full paths for bandwidth optimization.
        /// Called when server loads the scene - broadcasts to all early-joiners.
        /// </summary>
        [ClientRpc]
        internal void RPC_SyncSceneDefinedObjectIds_Compressed_Broadcast(string sceneName, ushort expectedMetadataCount, ushort[] locationIndices, uint[] gonetIds, byte[][] customInitData)
        {
            GONetLog.Info($"[GONETID-RPC-BROADCAST] Compressed BROADCAST RPC received - scene: '{sceneName}', objects: {locationIndices.Length}, time: {GONetMain.Time.ElapsedSeconds:F3}s");

            if (GONetMain.IsServer)
            {
                GONetLog.Info($"[GONETID-RPC-SKIP] Skipping broadcast RPC on server");
                return;
            }

            ProcessCompressedGONetIdAssignments(sceneName, expectedMetadataCount, locationIndices, gonetIds, customInitData);
        }

        /// <summary>
        /// TARGET RPC: Server sends scene-defined object GONetId assignments to a SPECIFIC client (COMPRESSED - REACTIVE PATH).
        /// Uses 16-bit location indices instead of full paths for bandwidth optimization.
        /// Called when a late-joiner connects - targeted to that specific client only.
        /// </summary>
        [TargetRpc(RpcTarget.SpecificAuthority)]
        internal void RPC_SyncSceneDefinedObjectIds_Compressed_Target(ushort targetClientAuthorityId, string sceneName, ushort expectedMetadataCount, ushort[] locationIndices, uint[] gonetIds, byte[][] customInitData)
        {
            GONetLog.Info($"[GONETID-RPC-TARGET] Compressed TARGET RPC received - scene: '{sceneName}', objects: {locationIndices.Length}, target: {targetClientAuthorityId}, time: {GONetMain.Time.ElapsedSeconds:F3}s");

            if (GONetMain.IsServer)
            {
                GONetLog.Info($"[GONETID-RPC-SKIP] Skipping target RPC on server");
                return;
            }

            ProcessCompressedGONetIdAssignments(sceneName, expectedMetadataCount, locationIndices, gonetIds, customInitData);
        }

        /// <summary>
        /// Shared processing logic for both broadcast and targeted compressed GONetId RPCs.
        /// </summary>
        private void ProcessCompressedGONetIdAssignments(string sceneName, ushort expectedMetadataCount, ushort[] locationIndices, uint[] gonetIds, byte[][] customInitData)
        {
            // Check if scene is loaded yet
            Scene sceneForBuffer = GONetMain.SceneManager.GetSceneByName(sceneName);

            if (!sceneForBuffer.isLoaded)
            {
                // BUFFER for later application
                bufferedGONetIdAssignmentsByScene[sceneName] = new BufferedGONetIdAssignments
                {
                    sceneName = sceneName,
                    expectedMetadataCount = expectedMetadataCount,
                    locationIndices = locationIndices,
                    gonetIds = gonetIds,
                    customInitData = customInitData,
                    receivedAtTime = GONetMain.Time.ElapsedSeconds,
                    isCompressed = true
                };

                GONetLog.Info($"[GONetId-BUFFER-COMPRESSED] Buffered COMPRESSED GONetId assignments for scene '{sceneName}' (scene not loaded yet, {locationIndices.Length} objects, metadata count: {expectedMetadataCount})");
                return;
            }

            // Scene already loaded - apply immediately
            ApplyGONetIdAssignments_Compressed(sceneName, expectedMetadataCount, locationIndices, gonetIds, customInitData);
        }

        /// <summary>
        /// Applies GONetId assignments to scene objects.
        /// Extracted from RPC_SyncSceneDefinedObjectIds to support both immediate application and buffered application.
        /// </summary>
        private void ApplyGONetIdAssignments(string sceneName, string[] designTimeLocations, uint[] gonetIds, byte[][] customInitData)
        {
            // DIAGNOSTIC: Track APPLICATION-LEVEL processing start time
            // This shows when the RPC is pulled from the message queue and starts executing
            // Compare this to network receive time (MessageFlow log) to detect queue processing delays
            double processStartTime = GONetMain.Time.ElapsedSeconds;
            GONetLog.Info($"[GONETID-PROCESS-START] CLIENT processing GONetId sync RPC for '{sceneName}' - {designTimeLocations.Length} objects at time {processStartTime:F3}s");

            int assignedCount = 0;
            int notFoundCount = 0;
            int initDataCount = 0;

            // Match each design-time location to a GONetParticipant and assign its GONetId
            for (int i = 0; i < designTimeLocations.Length; i++)
            {
                string location = designTimeLocations[i];
                uint gonetId = gonetIds[i];
                byte[] initData = (customInitData != null && i < customInitData.Length) ? customInitData[i] : null;

                // Find the GONetParticipant with this design-time location
                GONetParticipant participant = GONetMain.FindParticipantByDesignTimeLocation(location, sceneName);
                if (participant != null)
                {
                    // Assign the GONetId to match the server's assignment
                    GONetMain.AssignGONetIdRaw_Direct(participant, gonetId);

                    // FIX (December 2025): Register scene objects in SoA IMMEDIATELY after GONetId assignment.
                    // Problem: OnGONetReady may not fire yet because IsGONetReady() returns false when
                    // GONetLocal.LookupByAuthorityId[OwnerAuthorityId] is null (server's GONetLocal not yet received).
                    // This causes scene objects to never be registered in SoA, so sync data is dropped (dataIns=0).
                    // Solution: Register directly here since we know:
                    // 1. This is a client receiving scene object GONetId assignment from server
                    // 2. GONetId is now assigned (contains OwnerAuthorityId)
                    // 3. If IsMine=false (server-owned), we need to receive sync data from server
                    if (!participant.IsMine && !participant.v2_isRegisteredInSoA)
                    {
                        GONetMain.RegisterObjectInSoA(participant);
                        GONetLog.Info($"[SoA-SCENE-REG] Registered scene object '{participant.name}' (GONetId {participant.GONetId}, IsMine={participant.IsMine}) in SoA at ApplyGONetIdAssignments");
                    }

                    // Deserialize custom initialization data if present
                    if (initData != null && initData.Length > 0)
                    {
                        GONetLog.Info($"[GONetGlobal] ABOUT TO DESERIALIZE init data for '{participant.gameObject.name}' - {initData.Length} bytes");
                        GONetMain.DeserializeSceneObjectInitData(participant, initData);
                        GONetLog.Info($"[GONetGlobal] FINISHED DESERIALIZE for '{participant.gameObject.name}'");
                        initDataCount++;
                    }
                    // NOTE: No init data is normal for objects without IGONetSyncdBehaviourInitializer (most scene objects)
                    // Logging at Debug level to avoid spam

                    GONetLog.Debug($"[GONetGlobal] Assigned GONetId {gonetId} to scene object '{participant.gameObject.name}' at location '{location}'{(initData != null ? $" with {initData.Length} bytes init data" : "")}");
                    assignedCount++;
                }
                else
                {
                    GONetLog.Warning($"[GONetGlobal] Could not find scene object at location '{location}' to assign GONetId {gonetId}");
                    notFoundCount++;
                }
            }

            if (notFoundCount > 0)
            {
                GONetLog.Warning($"[GONetGlobal] Assigned {assignedCount} of {designTimeLocations.Length} scene-defined object GONetIds for scene '{sceneName}' ({notFoundCount} not found, {initDataCount} with init data)");
            }
            else
            {
                GONetLog.Info($"[GONetGlobal] Successfully assigned all {assignedCount} scene-defined object GONetIds for scene '{sceneName}' ({initDataCount} with initialization data)");
            }

            // IMPORTANT: Update readiness and process queued messages if all pending scenes are ready
            if (GONetMain.IsClient && GONetMain.GONetClient != null)
            {
                GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();
            }

            // DIAGNOSTIC: Track APPLICATION-LEVEL processing completion time
            double processEndTime = GONetMain.Time.ElapsedSeconds;
            double processDuration = processEndTime - processStartTime;
            GONetLog.Info($"[GONETID-PROCESS-END] CLIENT finished processing GONetId sync RPC for '{sceneName}' at time {processEndTime:F3}s (duration: {processDuration * 1000:F1}ms, assigned: {assignedCount})");

            // Notify SceneManager that GONetId sync was received - stops retry loop for SceneLoadCompleteEvent
            GONetMain.SceneManager?.OnReceivedGONetIdSyncForScene(sceneName);
        }

        /// <summary>
        /// Applies COMPRESSED GONetId assignments to scene objects.
        /// Includes validation logic and selective fallback requests for failed indices.
        /// </summary>
        private void ApplyGONetIdAssignments_Compressed(string sceneName, ushort expectedMetadataCount, ushort[] locationIndices, uint[] gonetIds, byte[][] customInitData)
        {
            double processStartTime = GONetMain.Time.ElapsedSeconds;
            GONetLog.Info($"[GONETID-PROCESS-START-COMPRESSED] CLIENT processing COMPRESSED GONetId sync RPC for '{sceneName}' - {locationIndices.Length} objects, expected metadata count: {expectedMetadataCount} at time {processStartTime:F3}s");

            // VALIDATION STEP 1: Check metadata count (detect build mismatch)
            ushort clientMetadataCount = (ushort)GONetSpawnSupport_Runtime.GetTotalMetadataCount();

            if (clientMetadataCount != expectedMetadataCount)
            {
                GONetLog.Error($"[GONETID-COMPRESS-FAIL] BUILD VERSION MISMATCH! Client metadata count ({clientMetadataCount}) != server metadata count ({expectedMetadataCount}). Requesting FULL PATH FALLBACK for ALL {locationIndices.Length} objects in scene '{sceneName}'.");

                // Request fallback for ALL objects (builds don't match - indices are meaningless)
                CallRpc(nameof(RPC_RequestFullPathFallback_AllObjects), sceneName, gonetIds, customInitData);
                return;
            }

            // VALIDATION STEP 2: Apply assignments with per-object validation
            int assignedCount = 0;
            int notFoundCount = 0;
            int initDataCount = 0;
            List<int> failedIndices = new List<int>();  // Track positions in arrays where lookup failed

            for (int i = 0; i < locationIndices.Length; i++)
            {
                ushort locationIndex = locationIndices[i];
                uint gonetId = gonetIds[i];
                byte[] initData = (customInitData != null && i < customInitData.Length) ? customInitData[i] : null;

                // VALIDATION: Check index range
                if (locationIndex >= expectedMetadataCount)
                {
                    GONetLog.Error($"[GONETID-COMPRESS-FAIL] Index out of range! locationIndex: {locationIndex}, expectedMetadataCount: {expectedMetadataCount}. Adding to fallback list (position {i}).");
                    failedIndices.Add(i);
                    continue;
                }

                // Look up location from index
                string location = GONetSpawnSupport_Runtime.GetDesignTimeLocationFromIndex(locationIndex);

                if (string.IsNullOrEmpty(location))
                {
                    GONetLog.Error($"[GONETID-COMPRESS-FAIL] Failed to get location for index {locationIndex}. Adding to fallback list (position {i}).");
                    failedIndices.Add(i);
                    continue;
                }

                // Find the GONetParticipant with this design-time location
                GONetParticipant participant = GONetMain.FindParticipantByDesignTimeLocation(location, sceneName);

                if (participant != null)
                {
                    // Assign the GONetId to match the server's assignment
                    GONetMain.AssignGONetIdRaw_Direct(participant, gonetId);

                    // FIX (December 2025): Register scene objects in SoA IMMEDIATELY after GONetId assignment.
                    // Same fix as ApplyGONetIdAssignments - see detailed comment there.
                    if (!participant.IsMine && !participant.v2_isRegisteredInSoA)
                    {
                        GONetMain.RegisterObjectInSoA(participant);
                        //GONetLog.Info($"[SoA-SCENE-REG] Registered scene object '{participant.name}' (GONetId {participant.GONetId}, IsMine={participant.IsMine}) in SoA at ApplyGONetIdAssignments_Compressed");
                    }

                    assignedCount++;

                    // Deserialize custom initialization data if present
                    if (initData != null && initData.Length > 0)
                    {
                        GONetMain.DeserializeSceneObjectInitData(participant, initData);
                        initDataCount++;
                    }
                }
                else
                {
                    // Object destroyed on client OR never existed (build mismatch)
                    // This is EXPECTED if object was destroyed early - don't request fallback
                    notFoundCount++;
                    GONetLog.Debug($"[GONETID-COMPRESS] Object not found for location '{location}' (index {locationIndex}, GONetId {gonetId}) - likely destroyed before GONetId assignment or build mismatch");
                }
            }

            // VALIDATION STEP 3: Request selective fallback for failed indices (if any)
            if (failedIndices.Count > 0)
            {
                GONetLog.Warning($"[GONETID-COMPRESS-FALLBACK] {failedIndices.Count} indices failed validation. Requesting SELECTIVE fallback from server for scene '{sceneName}'.");

                // Extract failed GONetIds and init data
                uint[] failedGonetIds = new uint[failedIndices.Count];
                byte[][] failedInitData = new byte[failedIndices.Count][];

                for (int i = 0; i < failedIndices.Count; i++)
                {
                    int failedPosition = failedIndices[i];
                    failedGonetIds[i] = gonetIds[failedPosition];
                    failedInitData[i] = (customInitData != null && failedPosition < customInitData.Length) ? customInitData[failedPosition] : null;
                }

                CallRpc(nameof(RPC_RequestFullPathFallback_Selective), sceneName, failedGonetIds, failedInitData);
            }

            // IMPORTANT: Update readiness and process queued messages if all pending scenes are ready
            if (GONetMain.IsClient && GONetMain.GONetClient != null)
            {
                GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();
            }

            double processEndTime = GONetMain.Time.ElapsedSeconds;
            double processDuration = (processEndTime - processStartTime) * 1000.0;

            GONetLog.Info($"[GONETID-PROCESS-END-COMPRESSED] CLIENT finished processing COMPRESSED GONetId sync for '{sceneName}' - {assignedCount} assigned, {notFoundCount} not found (destroyed?), {initDataCount} with init data, {failedIndices.Count} failed (fallback requested), {processDuration:F2}ms processing time");

            // Notify SceneManager that GONetId sync was received - stops retry loop for SceneLoadCompleteEvent
            GONetMain.SceneManager?.OnReceivedGONetIdSyncForScene(sceneName);
        }

        /// <summary>
        /// INTERNAL: Sends scene-defined object GONetId assignments to all clients.
        /// Called by server after loading a scene with scene-defined objects.
        /// </summary>
        internal void SendSceneDefinedObjectIdSync(string sceneName, string[] designTimeLocations, uint[] gonetIds, byte[][] customInitData)
        {
            // DIAGNOSTIC: Track when SERVER sends GONetId assignments
            GONetLog.Info($"[GONETID-SEND] SERVER sending GONetId sync RPC for '{sceneName}' - {designTimeLocations.Length} objects at time {GONetMain.Time.ElapsedSeconds:F3}s");
            CallRpc(nameof(RPC_SyncSceneDefinedObjectIds), GONetMain.OwnerAuthorityId_Unset, sceneName, designTimeLocations, gonetIds, customInitData);
            GONetLog.Info($"[GONETID-SENT] SERVER finished sending GONetId sync RPC at time {GONetMain.Time.ElapsedSeconds:F3}s");
        }

        /// <summary>
        /// INTERNAL: Sends scene-defined object GONetId assignments to a specific client.
        /// Called by server when a late-joining client connects.
        /// </summary>
        internal void SendSceneDefinedObjectIdSync_ToSpecificClient(string sceneName, string[] designTimeLocations, uint[] gonetIds, byte[][] customInitData, ushort targetClientAuthorityId)
        {
            CallRpc(nameof(RPC_SyncSceneDefinedObjectIds), targetClientAuthorityId, sceneName, designTimeLocations, gonetIds, customInitData);
        }

        /// <summary>
        /// INTERNAL: Sends scene-defined object GONetId assignments to all clients (COMPRESSED).
        /// Uses 16-bit location indices instead of full paths (2 bytes vs 40 bytes per object).
        /// Called by server after loading a scene with scene-defined objects.
        /// </summary>
        internal void SendSceneDefinedObjectIdSync_Compressed(string sceneName, ushort expectedMetadataCount, ushort[] locationIndices, uint[] gonetIds, byte[][] customInitData)
        {
            GONetLog.Info($"[GONETID-SEND-BROADCAST] SERVER broadcasting COMPRESSED GONetId sync for '{sceneName}' - {locationIndices.Length} objects, {locationIndices.Length * 2} bytes indices, metadata count: {expectedMetadataCount}");
            CallRpc(nameof(RPC_SyncSceneDefinedObjectIds_Compressed_Broadcast), sceneName, expectedMetadataCount, locationIndices, gonetIds, customInitData);
            GONetLog.Info($"[GONETID-SENT-BROADCAST] SERVER finished broadcasting");
        }

        /// <summary>
        /// INTERNAL: Sends scene-defined object GONetId assignments to a specific client (COMPRESSED).
        /// Called by server when a late-joining client connects.
        /// NOTE: ClientRpc broadcasts to ALL clients, but only the target client will process it (others already have GONetIds).
        /// </summary>
        internal void SendSceneDefinedObjectIdSync_Compressed_ToSpecificClient(string sceneName, ushort expectedMetadataCount, ushort[] locationIndices, uint[] gonetIds, byte[][] customInitData, ushort targetClientAuthorityId)
        {
            GONetLog.Info($"[GONETID-SEND-TARGET] SERVER sending COMPRESSED GONetId sync to SPECIFIC client {targetClientAuthorityId} for '{sceneName}' - {locationIndices.Length} objects");
            CallRpc(nameof(RPC_SyncSceneDefinedObjectIds_Compressed_Target), targetClientAuthorityId, sceneName, expectedMetadataCount, locationIndices, gonetIds, customInitData);
            GONetLog.Info($"[GONETID-SENT-TARGET] SERVER finished sending to client {targetClientAuthorityId}");
        }

        /// <summary>
        /// SERVER RPC: Client requests full path fallback for ALL objects (build mismatch detected).
        /// Server re-sends GONetId assignments using full paths instead of indices.
        /// </summary>
        [ServerRpc]
        internal void RPC_RequestFullPathFallback_AllObjects(string sceneName, uint[] gonetIds, byte[][] customInitData)
        {
            // Get caller authority ID from RPC context
            var context = GONetEventBus.CurrentRpcContext;
            if (!context.HasValue)
            {
                GONetLog.Error("[GONETID-FALLBACK-FAIL] RPC called outside of RPC context");
                return;
            }
            ushort callerAuthorityId = context.Value.SourceAuthorityId;
            GONetLog.Warning($"[GONETID-FALLBACK-REQUEST-ALL] Client {callerAuthorityId} requested FULL PATH FALLBACK for ALL objects in scene '{sceneName}' ({gonetIds.Length} objects) - likely build version mismatch");

            // Rebuild full paths from GONetIds (find participants by GONetId)
            string[] designTimeLocations = new string[gonetIds.Length];

            for (int i = 0; i < gonetIds.Length; i++)
            {
                GONetParticipant participant = GONetMain.GetGONetParticipantById(gonetIds[i]);
                if (participant != null)
                {
                    designTimeLocations[i] = participant.DesignTimeLocation;
                }
                else
                {
                    GONetLog.Error($"[GONETID-FALLBACK-FAIL] Server couldn't find participant with GONetId {gonetIds[i]} for fallback - object destroyed or invalid ID");
                    designTimeLocations[i] = string.Empty;  // Client will skip this
                }
            }

            // Send full paths to this client only
            GONetLog.Info($"[GONETID-FALLBACK-SEND-ALL] SERVER sending FULL PATH fallback to client {callerAuthorityId} for scene '{sceneName}' ({gonetIds.Length} objects)");
            SendSceneDefinedObjectIdSync_ToSpecificClient(sceneName, designTimeLocations, gonetIds, customInitData, callerAuthorityId);
        }

        /// <summary>
        /// SERVER RPC: Client requests full path fallback for SPECIFIC objects (index validation failures).
        /// Server re-sends GONetId assignments for failed objects only using full paths.
        /// </summary>
        [ServerRpc]
        internal void RPC_RequestFullPathFallback_Selective(string sceneName, uint[] failedGonetIds, byte[][] failedInitData)
        {
            // Get caller authority ID from RPC context
            var context = GONetEventBus.CurrentRpcContext;
            if (!context.HasValue)
            {
                GONetLog.Error("[GONETID-FALLBACK-FAIL] RPC called outside of RPC context");
                return;
            }
            ushort callerAuthorityId = context.Value.SourceAuthorityId;
            GONetLog.Warning($"[GONETID-FALLBACK-REQUEST-SELECTIVE] Client {callerAuthorityId} requested SELECTIVE fallback for {failedGonetIds.Length} objects in scene '{sceneName}'");

            // Rebuild full paths for failed objects only
            string[] designTimeLocations = new string[failedGonetIds.Length];

            for (int i = 0; i < failedGonetIds.Length; i++)
            {
                GONetParticipant participant = GONetMain.GetGONetParticipantById(failedGonetIds[i]);
                if (participant != null)
                {
                    designTimeLocations[i] = participant.DesignTimeLocation;
                }
                else
                {
                    GONetLog.Error($"[GONETID-FALLBACK-FAIL] Server couldn't find participant with GONetId {failedGonetIds[i]} for fallback - object destroyed or invalid ID");
                    designTimeLocations[i] = string.Empty;  // Client will skip this
                }
            }

            // Send full paths for ONLY the failed objects to this client
            GONetLog.Info($"[GONETID-FALLBACK-SEND-SELECTIVE] SERVER sending SELECTIVE fallback to client {callerAuthorityId} for scene '{sceneName}' ({failedGonetIds.Length} objects)");
            CallRpc(nameof(RPC_SyncSceneDefinedObjectIds), callerAuthorityId, sceneName, designTimeLocations, failedGonetIds, failedInitData);
        }

        /// <summary>
        /// UNIVERSAL LOGGING: Captures OnGONetReady for ALL GONet participants (beacons, projectiles, physics cubes, etc.).
        /// This is the central coordinator that sees every participant's lifecycle, providing consistent logging
        /// regardless of companion script type or GameObject.
        ///
        /// Used for comprehensive log analysis to track OnGONetReady timing, frame delays, and reliability metrics.
        /// InstanceID enables correlation with Awake() events for complete lifecycle tracking.
        /// </summary>
        public override void OnGONetReady(GONetParticipant gonetParticipant)
        {
            base.OnGONetReady(gonetParticipant);

            // Log with InstanceID for Awake correlation and GONetId for other analysis
            //GONetLog.Info($"[GONetGlobal] ✅ OnGONetReady FIRED - InstanceID: {gonetParticipant.GetInstanceID()}, GONetId: {gonetParticipant.GONetId}, GameObject: {gonetParticipant.name}, IsMine: {gonetParticipant.IsMine}, Owner: {gonetParticipant.OwnerAuthorityId}");
        }
    }
}
