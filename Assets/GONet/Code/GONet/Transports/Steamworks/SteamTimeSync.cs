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

using System;
using Steamworks;
using GONet.Utils;

namespace GONet
{
    /// <summary>
    /// Synchronizes Steam's internal clock with GONet's high-resolution time system.
    ///
    /// <para><b>WHY THIS EXISTS (December 2025):</b></para>
    /// <para>
    /// Steam's <c>SteamNetworkingMessage_t.m_usecTimeReceived</c> contains the ACCURATE timestamp
    /// when Steam's networking layer received a packet - NOT when our code processed it.
    /// During high-load scenarios (scene loads, heavy instantiation), there can be 50-500ms
    /// between when Steam receives a packet and when we process it.
    /// </para>
    ///
    /// <para>
    /// By using Steam's timestamp for RTT calculations, we get accurate network latency
    /// measurements that don't include frame processing delay. This prevents false backpressure
    /// triggers and gives users accurate network stats.
    /// </para>
    ///
    /// <para><b>CLOCK DOMAINS:</b></para>
    /// <list type="bullet">
    ///   <item><description><b>Steam</b>: <c>SteamNetworkingUtils.GetLocalTimestamp()</c> - microseconds since process start</description></item>
    ///   <item><description><b>GONet</b>: <c>HighResolutionTimeUtils.UtcNowTicks</c> - 100-nanosecond ticks</description></item>
    /// </list>
    ///
    /// <para><b>CONVERSION:</b></para>
    /// <para>1 microsecond = 10 ticks (100ns each)</para>
    /// <para>GONet ticks = (Steam microseconds * 10) + offset</para>
    ///
    /// <para><b>USAGE:</b></para>
    /// <code>
    /// // Call once after SteamAPI.Init() succeeds
    /// SteamTimeSync.Initialize();
    ///
    /// // When processing a Steam message
    /// long steamUsec = message.m_usecTimeReceived;
    /// long gonetTicks = SteamTimeSync.ConvertSteamTimeToGONetTicks(steamUsec);
    /// </code>
    /// </summary>
    public static class SteamTimeSync
    {
        /// <summary>
        /// Number of 100-nanosecond ticks per microsecond.
        /// </summary>
        private const long TICKS_PER_MICROSECOND = 10; // 1 usec = 10 * 100ns

        /// <summary>
        /// Offset to add after scaling Steam microseconds to ticks.
        /// Calculated as: GONetTicks - (SteamUsec * 10)
        /// </summary>
        private static long _steamToGONetOffset = 0;

        /// <summary>
        /// Tracks the GONet timebase used when the offset was last calculated.
        /// </summary>
        private static long _timebaseInitialStopwatchTicks = 0;

        /// <summary>
        /// True if <see cref="Initialize"/> has been called successfully.
        /// </summary>
        private static bool _isInitialized = false;

        /// <summary>
        /// Number of samples taken during initialization for averaging.
        /// </summary>
        private const int INIT_SAMPLE_COUNT = 5;

        /// <summary>
        /// True if clock synchronization has been performed.
        /// Check this before using <see cref="ConvertSteamTimeToGONetTicks"/>.
        /// </summary>
        public static bool IsInitialized => _isInitialized;

        /// <summary>
        /// Initialize clock synchronization between Steam and GONet time domains.
        /// Call this ONCE after <c>SteamAPI.Init()</c> succeeds.
        ///
        /// <para>
        /// This method samples both clocks multiple times and averages the offset
        /// to reduce measurement jitter.
        /// </para>
        /// </summary>
        /// <returns>True if initialization succeeded, false if Steam is not available</returns>
        public static bool Initialize()
        {
            try
            {
                // Verify Steam is initialized
                if (!GONetSteamManager.IsInitialized)
                {
                    GONetLog.Warning("[SteamTimeSync] Cannot initialize - Steam not initialized yet");
                    return false;
                }

                // Ensure GONet timebase is initialized before sampling elapsed ticks
                _ = GONetMain.Time;

                if (!GONetMain.SecretaryOfTemporalAffairs.TryGetStaticInitialStopwatchTicks(out long initialStopwatchTicks))
                {
                    GONetLog.Warning("[SteamTimeSync] Cannot initialize - GONet timebase not ready");
                    return false;
                }

                if (_isInitialized && initialStopwatchTicks == _timebaseInitialStopwatchTicks)
                {
                    return true;
                }

                if (_isInitialized && initialStopwatchTicks != _timebaseInitialStopwatchTicks)
                {
                    GONetLog.Warning("[SteamTimeSync] GONet timebase changed - re-synchronizing clock");
                }

                // Take multiple samples and average to reduce jitter
                // CRITICAL: Use SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic() (ELAPSED time domain)
                // NOT HighResolutionTimeUtils.UtcNowTicks (ABSOLUTE time domain)
                // The time sync system uses elapsed ticks, so we must sync to that domain.
                long totalOffset = 0;
                for (int i = 0; i < INIT_SAMPLE_COUNT; i++)
                {
                    // Sample both clocks as close together as possible
                    long steamUsec = (long)SteamNetworkingUtils.GetLocalTimestamp();
                    long gonetElapsedTicks = GONetMain.SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic();

                    // Calculate offset: GONetElapsed = Steam * 10 + offset
                    // Therefore: offset = GONetElapsed - (Steam * 10)
                    long offset = gonetElapsedTicks - (steamUsec * TICKS_PER_MICROSECOND);
                    totalOffset += offset;
                }

                _steamToGONetOffset = totalOffset / INIT_SAMPLE_COUNT;
                _timebaseInitialStopwatchTicks = initialStopwatchTicks;
                _isInitialized = true;

                // Log for diagnostics (offset should be small, not millions of years!)
                double offsetMs = _steamToGONetOffset / (double)TimeSpan.TicksPerMillisecond;
                GONetLog.Info($"[SteamTimeSync] Initialized: Steam→GONet ELAPSED offset = {offsetMs:F3}ms ({_steamToGONetOffset} ticks)");

                return true;
            }
            catch (Exception ex)
            {
                GONetLog.Error($"[SteamTimeSync] Initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Convert a Steam timestamp (microseconds) to GONet ticks.
        ///
        /// <para>
        /// Use this to convert <c>SteamNetworkingMessage_t.m_usecTimeReceived</c>
        /// to GONet's time domain for accurate RTT calculations.
        /// </para>
        /// </summary>
        /// <param name="steamMicroseconds">Steam timestamp in microseconds (from m_usecTimeReceived)</param>
        /// <returns>Equivalent time in GONet ticks (100-nanosecond units)</returns>
        public static long ConvertSteamTimeToGONetTicks(long steamMicroseconds)
        {
            bool shouldInitialize = !_isInitialized;

            if (!shouldInitialize &&
                GONetMain.SecretaryOfTemporalAffairs.TryGetStaticInitialStopwatchTicks(out long currentInitial))
            {
                shouldInitialize = currentInitial != _timebaseInitialStopwatchTicks;
            }

            if (shouldInitialize)
            {
                if (!Initialize())
                {
                    return 0;
                }
            }

            return (steamMicroseconds * TICKS_PER_MICROSECOND) + _steamToGONetOffset;
        }

        /// <summary>
        /// Convert GONet ticks to Steam microseconds.
        /// Inverse of <see cref="ConvertSteamTimeToGONetTicks"/>.
        /// </summary>
        /// <param name="gonetTicks">GONet timestamp in ticks</param>
        /// <returns>Equivalent time in Steam microseconds</returns>
        public static long ConvertGONetTicksToSteamTime(long gonetTicks)
        {
            bool shouldInitialize = !_isInitialized;

            if (!shouldInitialize &&
                GONetMain.SecretaryOfTemporalAffairs.TryGetStaticInitialStopwatchTicks(out long currentInitial))
            {
                shouldInitialize = currentInitial != _timebaseInitialStopwatchTicks;
            }

            if (shouldInitialize)
            {
                if (!Initialize())
                {
                    return 0;
                }
            }

            return (gonetTicks - _steamToGONetOffset) / TICKS_PER_MICROSECOND;
        }

        /// <summary>
        /// Reset synchronization state. Call this if Steam is re-initialized.
        /// </summary>
        public static void Reset()
        {
            _isInitialized = false;
            _steamToGONetOffset = 0;
            _timebaseInitialStopwatchTicks = 0;
            GONetLog.Info("[SteamTimeSync] Reset - will re-synchronize on next use");
        }

        /// <summary>
        /// Re-synchronize clocks. Use this periodically if long-running sessions
        /// might experience clock drift (though drift should be minimal).
        /// </summary>
        public static void Resync()
        {
            _isInitialized = false;
            Initialize();
        }
    }
}
