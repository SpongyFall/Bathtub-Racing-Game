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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.CompilerServices;
using UnityEngine;
using GONet.Utils;

namespace GONet
{
    /// <summary>
    /// <para>
    /// A high-performance, non-blocking logging utility optimized for minimal GC allocation.
    /// 
    /// Key Architecture:
    /// 1. Producer (Calling Thread): Captures context (ThreadID, Time, Caller Info) and Enqueues. Returns immediately.
    /// 2. Consumer (Background Thread): Dequeues, writes to disk, and pushes to Unity Console.
    /// 
    /// This ensures the gameplay thread is never blocked by File I/O or Console rendering.
    /// </para>
    /// <para>
    /// Advantages:
    ///     1. Log statements will all have a nice/complete date/time stamp (that includes millisecond and uses GONet's own <see cref="HighResolutionTimeUtils"/>)
    ///     2. Log statements will go to the console as usual as well as a gonet.log file in /logs folder
    ///     3. gonet.log file history will be maintained with automatic file rotation and cleanup
    ///     4. Thread-safe operation for multi-threaded environments
    ///     5. You can subscribe to logging events
    ///     6. You can conditionally exclude calls to any/all logging level methods using #DEFINE (i.e., remove everything from a production release/build if you like)
    ///     7. Memory-efficient with minimal allocations and background async file writing
    /// </para>
    /// </summary>
    public static class GONetLog
    {
        #region Constants and Enums

        // Restored keys to match legacy format "Log:Info"
        private const string KeyInfo = "Log:Info";
        private const string KeyDebug = "Log:Debug";
        private const string KeyWarning = "Log:Warning";
        private const string KeyError = "Log:Error";
        private const string KeyFatal = "Log:Fatal";
        private const string KeyVerbose = "Log:Verbose";

        // Separators
        private const string SepBracketOpen = "[";
        private const string SepBracketClose = "]";
        private const string SepSpace = " ";
        private const string SepColon = ":";
        private const string SepThread = " (Thread:";
        private const string SepParenClose = ")";
        private const string SepParenOpen = " (";
        private const string SepFrame = " (frame:";
        private const string SepSlash = "/";
        private const string SepSeconds = "s)";
        private const string TagServer = "[Server]";
        private const string TagClientPrefix = "[Client:";

        // Hyperlinks
        private const string LinkTagStart = "<a href=\"";
        private const string LinkTagMiddle = "\" line=\"";
        private const string LinkTagEnd = "\">";
        private const string LinkTagClose = "</a>";
        private const string BoldStart = "<b>";
        private const string BoldEnd = "</b>";

        public enum LogLevel
        {
            Verbose,
            Debug,
            Info,
            Warning,
            Error,
            Fatal
        }

        #endregion

        #region Configuration

        private static string LogDirectory;
        private static readonly int MaxLogFileDays = 5;
        private static readonly string LogFilePrefix = "gonet";
        private static readonly string LogFileExtension = ".log";
        private static readonly int MaxQueueSize = 25000;

        /// <summary>
        /// When true, prefixes log filename with process ID to prevent cross-process file corruption.
        ///
        /// USE CASE: This is primarily useful during LOCAL DEVELOPMENT when running multiple
        /// instances on the same machine (e.g., server + client builds writing to the same
        /// Application.persistentDataPath/logs directory). Without unique filenames, multiple
        /// processes would corrupt each other's log files.
        ///
        /// IN PRODUCTION: Typically unnecessary since server and clients run on different machines
        /// with separate file systems. Keeping this false produces cleaner log filenames.
        ///
        /// Example when true:  "12345-gonet-2025-12-02.log" (process ID prefix)
        /// Example when false: "gonet-2025-12-02.log" (cleaner, no prefix)
        ///
        /// NOTE: In standalone builds, this is auto-enabled at initialization to prevent log conflicts.
        /// In Editor, GONet will auto-enable this when connecting to localhost.
        /// </summary>
        public static bool UseProcessIdPrefix { get; set; } = false;

        private static readonly int CurrentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(0.5);
        private static readonly System.Threading.ThreadPriority BackgroundThreadPriority = System.Threading.ThreadPriority.BelowNormal;

        /// <summary>
        /// Runtime enable/disable for GONetLog. Set to false to completely suppress all GONet logging.
        /// Can be controlled via LogSettings.
        /// </summary>
        public static bool IsEnabled = true;

        /// <summary>
        /// Minimum log level for GONetLog. Messages below this level are filtered out.
        /// Can be controlled via LogSettings.
        /// </summary>
        public static LogLevel MinimumLogLevel = LogLevel.Verbose;

        public static LoggingProfile DefaultProfile { get; } = new LoggingProfile("Default", outputToSeparateFile: false, includeStackTraces: false);

        #endregion

        #region Fields

        private static string _currentLogFile;
        private static string _lastLog;
        private static Thread _loggerThread;
        private static readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
        private static readonly AutoResetEvent _queueEvent = new AutoResetEvent(false);

        private static volatile bool _shutdownRequested = false;
        private static int _isShuttingDown = 0;
        private static int _queuedItemsCount = 0;
        private static DateTime _lastFlushTime = DateTime.Now;

        private static readonly object _fileLock = new object();
        private static readonly object _flushLock = new object();
        private static bool _initialized;

        private static FileStream _fileStream;
        private static StreamWriter _streamWriter;
        private static readonly bool IsWebGL = Application.platform == RuntimePlatform.WebGLPlayer;

        private struct CachedPathInfo
        {
            public string FileName;
            public string SanitizedPath;
        }
        private static readonly ConcurrentDictionary<string, CachedPathInfo> _pathCache = new ConcurrentDictionary<string, CachedPathInfo>();

        private static readonly StringBuilder _logBuilder = new StringBuilder(4096);

        // Lookup for zero-alloc date formatting
        private static readonly string[] _monthNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        #endregion

        #region Internal Data Structure

        private struct LogEntry
        {
            public string Message;
            public LogLevel Level;
            public LogType UnityLogType;
            public string LogKey;

            // Context
            public int ThreadId;
            public DateTime Timestamp;

            // Vital Info (Restored)
            public long FrameCount;
            public double ElapsedSeconds;

            // Caller Info
            public string CallerFilePath;
            public string CallerMemberName;
            public int CallerLineNumber;

            // Optional Stack Trace
            public string ExplicitStackTrace;

            // Profile support
            public string ProfileName;
        }

        #endregion

        #region Logging Profiles

        public class LoggingProfile
        {
            public string ProfileName { get; set; }
            public bool OutputToSeparateFile { get; set; }
            public bool IncludeStackTraces { get; set; }
            public LogLevel MinimumLogLevel { get; set; }

            /// <summary>
            /// Enable synchronous logging (bypasses async queue, calls Unity Debug.Log immediately).
            /// Required for unit tests using LogAssert.Expect. Should be false for gameplay (performance).
            /// </summary>
            public bool UseSynchronousLogging { get; set; }

            internal string FilePath { get; set; }
            internal FileStream FileStream { get; set; }
            internal StreamWriter StreamWriter { get; set; }
            internal readonly object WriteLock = new object();

            public LoggingProfile(string profileName, bool outputToSeparateFile = true, bool includeStackTraces = false, LogLevel minimumLogLevel = LogLevel.Verbose, bool useSynchronousLogging = false)
            {
                ProfileName = profileName;
                OutputToSeparateFile = outputToSeparateFile;
                IncludeStackTraces = includeStackTraces;
                MinimumLogLevel = minimumLogLevel;
                UseSynchronousLogging = useSynchronousLogging;
            }
        }

        private static readonly ConcurrentDictionary<string, LoggingProfile> _loggingProfiles = new ConcurrentDictionary<string, LoggingProfile>();

        public static void RegisterLoggingProfile(LoggingProfile profile)
        {
            if (string.IsNullOrEmpty(profile.ProfileName)) return;
            if (_loggingProfiles.TryAdd(profile.ProfileName, profile))
            {
                if (profile.OutputToSeparateFile) InitializeProfileFileStream(profile);
            }
        }

        public static void UnregisterLoggingProfile(string profileName)
        {
            if (_loggingProfiles.TryRemove(profileName, out LoggingProfile profile))
            {
                CloseProfileFileStream(profile);
            }
        }

        private static void InitializeProfileFileStream(LoggingProfile profile)
        {
            try
            {
                // Use local builder for thread safety - avoid conflict with background logger
                var sb = new StringBuilder(256);

                // Optional process ID prefix to prevent cross-process file corruption
                if (UseProcessIdPrefix)
                {
                    sb.Append(CurrentProcessId).Append('-');
                }

                sb.Append(LogFilePrefix).Append('-').Append(profile.ProfileName).Append('-');
                AppendDateForFilename(sb, DateTime.Now);
                sb.Append(LogFileExtension);

                string filename = sb.ToString();
                profile.FilePath = Path.Combine(LogDirectory, filename);
                profile.FileStream = new FileStream(profile.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                profile.StreamWriter = new StreamWriter(profile.FileStream, Encoding.UTF8) { AutoFlush = false };
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"GONetLog: Failed profile stream '{profile.ProfileName}': {ex.Message}");
            }
        }

        private static void CloseProfileFileStream(LoggingProfile profile)
        {
            lock (profile.WriteLock)
            {
                try { profile.StreamWriter?.Flush(); profile.StreamWriter?.Close(); } catch { }
                try { profile.FileStream?.Close(); } catch { }
                profile.StreamWriter = null;
                profile.FileStream = null;
            }
        }

        #endregion

        #region Events and Properties

        public static string LastLog => _lastLog;
        public delegate void LogDelegate(string logStr);
        public static event LogDelegate OnLog;

        /// <summary>
        /// Force immediate processing of all queued logs (synchronous).
        /// Use in test TearDown to ensure background thread finishes before test completes.
        /// </summary>
        public static void FlushQueuedLogs()
        {
            if (IsWebGL) return; // No queue in WebGL

            // Give background thread a moment to enqueue any pending logs
            System.Threading.Thread.Sleep(50);

            // Process all queued logs synchronously
            FlushQueueToFileAndConsole();

            // Extra sleep to ensure Unity's Debug.Log calls are processed
            System.Threading.Thread.Sleep(50);
        }

        /// <summary>
        /// Gets the path to the log directory where all GONet log files are stored.
        /// Returns null if the log system has not been initialized yet.
        /// </summary>
        public static string GetLogDirectory()
        {
            return LogDirectory;
        }

        /// <summary>
        /// Gets information about the current log directory usage.
        /// Returns total file count, total size in bytes, and oldest file date.
        /// </summary>
        public static LogDirectoryInfo GetLogDirectoryInfo()
        {
            var info = new LogDirectoryInfo();

            if (string.IsNullOrEmpty(LogDirectory) || !Directory.Exists(LogDirectory))
            {
                return info;
            }

            try
            {
                var dirInfo = new DirectoryInfo(LogDirectory);
                var files = dirInfo.GetFiles();

                info.TotalFileCount = files.Length;
                info.TotalSizeBytes = files.Sum(f => f.Length);
                info.LogFileCount = files.Count(f => f.Extension == ".log");
                info.EventHistoryFileCount = files.Count(f => f.Name.StartsWith("gonet-events-") && f.Extension == ".txt");
                info.OldestFileDate = files.Length > 0 ? files.Min(f => f.LastWriteTime) : DateTime.MinValue;
                info.NewestFileDate = files.Length > 0 ? files.Max(f => f.LastWriteTime) : DateTime.MinValue;
            }
            catch { }

            return info;
        }

        /// <summary>
        /// Information about the GONet log directory.
        /// </summary>
        public struct LogDirectoryInfo
        {
            public int TotalFileCount;
            public int LogFileCount;
            public int EventHistoryFileCount;
            public long TotalSizeBytes;
            public DateTime OldestFileDate;
            public DateTime NewestFileDate;

            public float TotalSizeMB => TotalSizeBytes / (1024f * 1024f);
        }

        /// <summary>
        /// Manually trigger log file cleanup.
        /// This runs the same cleanup logic that runs automatically on initialization.
        /// Respects the settings in GONetConfig (MaxLogFileAgeDays, MaxEventHistoryFileAgeDays, etc.).
        ///
        /// Use this when you want to reclaim disk space on-demand, such as:
        /// - In a settings menu "Clear Old Logs" button
        /// - Before a long play session
        /// - When disk space is low
        /// </summary>
        public static void RunCleanup()
        {
            CleanupOldLogFiles();
        }

        /// <summary>
        /// Deletes ALL log files in the log directory.
        /// Use with caution - this cannot be undone.
        ///
        /// Returns the number of files deleted and bytes reclaimed.
        /// </summary>
        public static (int filesDeleted, long bytesReclaimed) DeleteAllLogs()
        {
            int filesDeleted = 0;
            long bytesReclaimed = 0;

            if (string.IsNullOrEmpty(LogDirectory) || !Directory.Exists(LogDirectory))
            {
                return (0, 0);
            }

            try
            {
                var dirInfo = new DirectoryInfo(LogDirectory);

                // Close our own file handles first
                CloseFileStream();
                foreach (var profile in _loggingProfiles.Values)
                {
                    CloseProfileFileStream(profile);
                }

                // Delete all files
                foreach (var file in dirInfo.GetFiles())
                {
                    try
                    {
                        long size = file.Length;
                        file.Delete();
                        filesDeleted++;
                        bytesReclaimed += size;
                    }
                    catch { }
                }

                // Re-initialize the main file stream
                _currentLogFile = GetLogFilePath(DateTime.Now);
                InitializeFileStream();

                // Re-initialize profile file streams
                foreach (var profile in _loggingProfiles.Values)
                {
                    if (profile.OutputToSeparateFile)
                    {
                        InitializeProfileFileStream(profile);
                    }
                }

                if (GONetConfig.LogCleanupOperations)
                {
                    UnityEngine.Debug.Log($"GONetLog: DeleteAllLogs - Deleted {filesDeleted} files, reclaimed {bytesReclaimed / 1024:N0} KB.");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"GONetLog: DeleteAllLogs warning: {ex.Message}");
            }

            return (filesDeleted, bytesReclaimed);
        }

        #endregion

        #region Log Suppression

        /// <summary>
        /// Thread-safe list of message prefixes to suppress from logging.
        /// Uses List for fast iteration when pattern count is small (typical case: 1-5 patterns).
        /// </summary>
        private static readonly System.Collections.Generic.List<string> _suppressedPatterns = new System.Collections.Generic.List<string>();
        private static readonly object _suppressLock = new object();

        /// <summary>
        /// Volatile flag for fast-path optimization.
        /// During normal gameplay (no suppression), this is false and the branch predictor
        /// optimizes the check to near-zero cost (~1-2 CPU cycles).
        /// </summary>
        private static volatile bool _hasSuppressedPatterns = false;

        /// <summary>
        /// Registers a message prefix to be ignored by the logger.
        /// Any log message starting with this pattern will be immediately discarded before
        /// any allocations occur (stack traces, StringBuilder, file I/O, Unity logging).
        ///
        /// <para><b>Primary Use Case: Unit Tests</b></para>
        /// Prevents log bloat from expected warnings during stress testing.
        /// Example: A test that validates float clamping may trigger 200,000+ warnings,
        /// creating multi-gigabyte log files. Suppressing these expected warnings keeps
        /// logs clean and tests fast.
        ///
        /// <para><b>Secondary Use Case: Production/Gameplay</b></para>
        /// Can be used to temporarily silence known non-critical warnings during specific
        /// gameplay scenarios (e.g., suppressing network timeout warnings during a
        /// deliberate connection test, or physics warnings during a cutscene with
        /// extreme camera movements).
        ///
        /// <para><b>Performance:</b></para>
        /// - When no patterns are suppressed: ~1-2 CPU cycles (volatile bool check only)
        /// - With 1 pattern: ~50 CPU cycles (lock + 1 string comparison)
        /// - With 5 patterns: ~200 CPU cycles (lock + 5 string comparisons worst case)
        /// - Compared to logging: 50x-200x faster than full log processing
        ///
        /// <para><b>Thread Safety:</b></para>
        /// Safe to call from any thread. The suppression list is protected by a lock.
        /// </summary>
        /// <param name="pattern">
        /// The exact prefix to match. Should be a const string from the producing class
        /// (e.g., <c>BitWriter.LogPattern_FloatBounds</c>) to ensure the pattern used
        /// for suppression is mathematically identical to the pattern used for logging.
        /// </param>
        /// <example>
        /// <code>
        /// // In test SetUp:
        /// GONetLog.SuppressPattern(BitWriter.LogPattern_FloatBounds);
        ///
        /// // In test TearDown:
        /// GONetLog.ClearSuppressedPatterns();
        /// </code>
        /// </example>
        public static void SuppressPattern(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return;
            lock (_suppressLock)
            {
                if (!_suppressedPatterns.Contains(pattern))
                {
                    _suppressedPatterns.Add(pattern);
                    _hasSuppressedPatterns = true;
                }
            }
        }

        /// <summary>
        /// Removes all suppressed patterns, restoring normal logging behavior.
        /// Should be called in test TearDown to ensure subsequent tests aren't affected.
        /// </summary>
        public static void ClearSuppressedPatterns()
        {
            lock (_suppressLock)
            {
                _suppressedPatterns.Clear();
                _hasSuppressedPatterns = false;
            }
        }

        #endregion

        #region Constructor/Initialization

        /// <summary>
        /// Ensures GONetLog is initialized. Safe to call multiple times.
        /// This uses lazy initialization to avoid calling Unity APIs from static constructors,
        /// which can cause issues when triggered from MonoBehaviour field initializers.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_fileLock)
            {
                if (_initialized) return; // Double-check after lock
                InitializeInternal();
            }
        }

        static GONetLog()
        {
            // NOTE: We intentionally do NOT initialize here anymore.
            // Static constructors in Unity can be triggered from MonoBehaviour field initializers,
            // which happens before the Unity runtime is fully ready. Calling Application.persistentDataPath
            // or other Unity APIs in this context throws an exception.
            //
            // Instead, we use lazy initialization via EnsureInitialized() which is called on first log.
            // The _initialized flag prevents double initialization.
        }

        private static void InitializeInternal()
        {
            if (_initialized) return;

            // SAFETY CHECK: Detect if we're being called too early (from static constructor context)
            // Unity APIs like Application.persistentDataPath throw exceptions when called from:
            // - Static constructors
            // - MonoBehaviour field initializers
            // - Before Unity runtime is fully ready
            // In these cases, skip initialization - it will be retried on next log call.
            try
            {
                // This is a safe probe - if it throws, we're too early
                _ = UnityEngine.Application.isPlaying;
            }
            catch (UnityException)
            {
                // Too early - Unity runtime not ready. Skip initialization, will retry later.
                return;
            }

            // Auto-enable process ID prefix for standalone builds to prevent log file conflicts
            // when multiple instances run on the same machine. This MUST happen before GetLogFilePath()
            // is called to ensure ALL logs (including early Awake logs) go to the correct file.
            // Editor stays with clean log names since it's typically a single process.
#if !UNITY_EDITOR
            UseProcessIdPrefix = true;
#endif

            try
            {
                string basePath = IsWebGL ? Application.temporaryCachePath : Application.persistentDataPath;
                LogDirectory = Path.Combine(basePath, "logs");
                if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);

                CleanupOldLogFiles();
                _currentLogFile = GetLogFilePath(DateTime.Now);
                InitializeFileStream();

                if (!IsWebGL)
                {
                    _loggerThread = new Thread(BackgroundLogLoop)
                    {
                        IsBackground = true,
                        Name = "GONet Writer",
                        Priority = BackgroundThreadPriority
                    };
                    _loggerThread.Start();
                }

                Application.quitting += OnApplicationQuitting;
                _initialized = true;
                UnityEngine.Debug.Log($"GONetLog: Initialized. Writing to {LogDirectory}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"GONetLog: Init failed: {ex.Message}");
            }
        }

        private static void InitializeFileStream()
        {
            lock (_fileLock)
            {
                try
                {
                    CloseFileStream();

                    // --- FIX START: Sanity Check for Zombie Files ---
                    // If the file exists and is strangely large (e.g. > 5MB) on startup,
                    // it's likely corrupted with null bytes from a previous crash. Rotate it.
                    if (File.Exists(_currentLogFile))
                    {
                        FileInfo fi = new FileInfo(_currentLogFile);
                        if (fi.Length > 5 * 1024 * 1024) // 5MB limit for a fresh session start
                        {
                            string timestamp = DateTime.Now.ToString("HHmmss");
                            string backupName = _currentLogFile.Replace(LogFileExtension, $"-backup-{timestamp}{LogFileExtension}");

                            try
                            {
                                File.Move(_currentLogFile, backupName);
                                UnityEngine.Debug.LogWarning($"GONetLog: Existing log was too large ({fi.Length} bytes). Rotated to {Path.GetFileName(backupName)} to prevent corruption.");
                            }
                            catch (Exception moveEx)
                            {
                                UnityEngine.Debug.LogError($"GONetLog: Could not rotate corrupted log: {moveEx.Message}");
                            }
                        }
                    }
                    // --- FIX END ---

                    _fileStream = new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _streamWriter = new StreamWriter(_fileStream, Encoding.UTF8) { AutoFlush = false };
                }
                catch (Exception ex) { UnityEngine.Debug.LogError($"GONetLog: File init error: {ex.Message}"); }
            }
        }

        private static void CloseFileStream()
        {
            lock (_fileLock)
            {
                try { _streamWriter?.Flush(); _streamWriter?.Close(); } catch { }
                try { _fileStream?.Close(); } catch { }
                _streamWriter = null;
                _fileStream = null;
            }
        }

        private static void OnApplicationQuitting()
        {
            if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0) return;

            _shutdownRequested = true;
            _queueEvent.Set();

            if (_loggerThread != null && _loggerThread.IsAlive)
            {
                _loggerThread.Join(1000);
            }

            FlushQueueToFileAndConsole();
            foreach (var p in _loggingProfiles.Values) CloseProfileFileStream(p);
            CloseFileStream();
        }

        /// <summary>
        /// Resets the logging infrastructure for a new session.
        /// Call this when entering play mode with domain reload disabled (Fast Iteration Mode)
        /// to ensure the logger is properly re-initialized after OnApplicationQuitting was called.
        ///
        /// Must be called BEFORE any logging happens in the new session to ensure
        /// ResetForNewSession() logs work correctly.
        /// </summary>
        public static void ResetForNewSession()
        {
            lock (_fileLock)
            {
                // Flush any pending logs and close resources from previous session
                if (_initialized)
                {
                    FlushQueueToFileAndConsole();
                    foreach (var p in _loggingProfiles.Values) CloseProfileFileStream(p);
                    CloseFileStream();
                }

                // Stop the logger thread if it's still alive
                if (_loggerThread != null && _loggerThread.IsAlive)
                {
                    _shutdownRequested = true;
                    _queueEvent.Set();
                    _loggerThread.Join(1000);
                }

                // Reset shutdown flags
                _shutdownRequested = false;
                Interlocked.Exchange(ref _isShuttingDown, 0);

                // Reset initialization flag so EnsureInitialized() will run again
                _initialized = false;

                // Null out the thread reference (will be recreated on next log)
                _loggerThread = null;

                // Clear thread buffers to prevent stale data
                _threadBuffers.Clear();

                // Clear the log queue
                while (_logQueue.TryDequeue(out _)) { }

                // Unregister the previous Application.quitting callback to prevent double registration
                Application.quitting -= OnApplicationQuitting;
            }

            // This will be logged after re-initialization happens on first log call
            UnityEngine.Debug.Log("[GONetLog] Reset for new session - logging infrastructure ready for re-initialization.");
        }

        #endregion

        #region Append Methods

        private static readonly ConcurrentDictionary<Thread, StringBuilder> _threadBuffers = new ConcurrentDictionary<Thread, StringBuilder>();

        public static void Append(string message) { AppendInternal(message, false); }
        public static void AppendLine(string message) { AppendInternal(message, true); }

        private static void AppendInternal(string message, bool eol)
        {
            if (!_threadBuffers.TryGetValue(Thread.CurrentThread, out StringBuilder sb))
            {
                sb = new StringBuilder(1024);
                _threadBuffers[Thread.CurrentThread] = sb;
            }
            sb.Append(message);
            if (eol) sb.Append(Environment.NewLine);
        }

        public static bool Append_FlushVerbose(string msg = null) => Append_Flush(LogLevel.Verbose, msg);
        public static bool Append_FlushDebug(string msg = null) => Append_Flush(LogLevel.Debug, msg);
        public static bool Append_FlushInfo(string msg = null) => Append_Flush(LogLevel.Info, msg);
        public static bool Append_FlushWarning(string msg = null) => Append_Flush(LogLevel.Warning, msg);
        public static bool Append_FlushError(string msg = null) => Append_Flush(LogLevel.Error, msg);
        public static bool Append_FlushFatal(string msg = null) => Append_Flush(LogLevel.Fatal, msg);

        private static bool Append_Flush(LogLevel level, string message)
        {
            if (message != null) AppendInternal(message, false);

            if (_threadBuffers.TryGetValue(Thread.CurrentThread, out StringBuilder sb) && sb.Length > 0)
            {
                string text = sb.ToString();
                sb.Clear();

                switch (level)
                {
                    case LogLevel.Verbose: Verbose(text); break;
                    case LogLevel.Debug: Debug(text); break;
                    case LogLevel.Info: Info(text); break;
                    case LogLevel.Warning: Warning(text); break;
                    case LogLevel.Error: Error(text); break;
                    case LogLevel.Fatal: Fatal(text); break;
                }
                return true;
            }
            return false;
        }

        #endregion

        #region Log Methods (Public API)

        [Conditional("LOG_INFO")]
        public static void Info(string message,
            [CallerLineNumber] int line = 0, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            EnqueueLog(message, KeyInfo, LogType.Log, LogLevel.Info, null, file, member, line);
        }

        [Conditional("LOG_DEBUG")]
        public static void Debug(string message,
            [CallerLineNumber] int line = 0, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            EnqueueLog(message, KeyDebug, LogType.Log, LogLevel.Debug, null, file, member, line);
        }

        [Conditional("LOG_WARNING")]
        public static void Warning(string message,
            [CallerLineNumber] int line = 0, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            EnqueueLog(message, KeyWarning, LogType.Warning, LogLevel.Warning, null, file, member, line);
        }

        [Conditional("LOG_ERROR")]
        public static void Error(string message,
            [CallerLineNumber] int line = 0, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            EnqueueLog(message, KeyError, LogType.Error, LogLevel.Error, null, file, member, line);
        }

        [Conditional("LOG_FATAL")]
        public static void Fatal(string message,
            [CallerLineNumber] int line = 0, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            EnqueueLog(message, KeyFatal, LogType.Error, LogLevel.Fatal, null, file, member, line);
        }

        [Conditional("LOG_VERBOSE")]
        public static void Verbose(string message,
            [CallerLineNumber] int line = 0, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            EnqueueLog(message, KeyVerbose, LogType.Log, LogLevel.Verbose, null, file, member, line);
        }

        #endregion

        #region Profile-Based Log Methods

        public static void Info(string message, string profileName,
            [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            EnqueueLog(message, KeyInfo, LogType.Log, LogLevel.Info, profileName, file, member, line);
        }

        public static void Debug(string message, string profileName,
            [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            EnqueueLog(message, KeyDebug, LogType.Log, LogLevel.Debug, profileName, file, member, line);
        }

        public static void Warning(string message, string profileName,
            [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            EnqueueLog(message, KeyWarning, LogType.Warning, LogLevel.Warning, profileName, file, member, line);
        }

        public static void Error(string message, string profileName,
            [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            EnqueueLog(message, KeyError, LogType.Error, LogLevel.Error, profileName, file, member, line);
        }

        public static void Verbose(string message, string profileName,
            [CallerFilePath] string file = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
        {
            EnqueueLog(message, KeyVerbose, LogType.Log, LogLevel.Verbose, profileName, file, member, line);
        }

        #endregion

        #region Queueing Logic

        private static void EnqueueLog(string message, string key, LogType type, LogLevel level, string profileName,
            string callerFile, string callerMember, int callerLine)
        {
            // Runtime enable/disable check - fastest possible exit
            if (!IsEnabled)
                return;

            // Minimum log level check - also very fast
            if (level < MinimumLogLevel)
                return;

            // Lazy initialization - ensures we don't call Unity APIs from static constructors
            // This check is fast (~1 CPU cycle) when already initialized
            if (!_initialized) EnsureInitialized();

            // --- FAST EXIT: Suppression Check (Before Any Allocation) ---
            // Check volatile bool first. If false (normal gameplay), we skip the lock entirely.
            // This optimization ensures near-zero cost (~1-2 CPU cycles) when no patterns are suppressed.
            if (_hasSuppressedPatterns)
            {
                lock (_suppressLock)
                {
                    // Iterate list to find a match. Using Ordinal comparison for maximum performance.
                    // StartsWith is faster than Contains and matches our use case (pattern as prefix).
                    for (int i = 0; i < _suppressedPatterns.Count; i++)
                    {
                        if (message.StartsWith(_suppressedPatterns[i], StringComparison.Ordinal))
                        {
                            return; // Squelch! No allocation, no queueing, no disk I/O.
                        }
                    }
                }
            }

            if (_queuedItemsCount >= MaxQueueSize) return;

            string stackTrace = null;
            LoggingProfile activeProfile = null;

            if (!string.IsNullOrEmpty(profileName))
            {
                _loggingProfiles.TryGetValue(profileName, out activeProfile);
            }
            else
            {
                activeProfile = DefaultProfile;
            }

            if (activeProfile != null)
            {
                if (level < activeProfile.MinimumLogLevel) return;
                if (activeProfile.IncludeStackTraces)
                {
                    stackTrace = new StackTrace(2, true).ToString();
                }
            }

            var entry = new LogEntry
            {
                Message = message,
                Level = level,
                UnityLogType = type,
                LogKey = key,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                Timestamp = DateTime.Now,
                // Capture Vital Context on Calling Thread
                FrameCount = GONetMain.Time?.FrameCount ?? 0,
                ElapsedSeconds = GONetMain.Time?.ElapsedSeconds ?? 0.0,

                CallerFilePath = callerFile,
                CallerMemberName = callerMember,
                CallerLineNumber = callerLine,
                ProfileName = profileName,
                ExplicitStackTrace = stackTrace
            };

            if (IsWebGL)
            {
                ProcessSingleLogEntryWebGL(entry);
            }
            else if (activeProfile != null && activeProfile.UseSynchronousLogging)
            {
                // CRITICAL: Unit tests require SYNCHRONOUS logging
                // Tests use LogAssert.Expect() which checks immediately - async queue won't work
                ProcessSingleLogEntryForTests(entry);
            }
            else
            {
                _logQueue.Enqueue(entry);
                Interlocked.Increment(ref _queuedItemsCount);
                _queueEvent.Set();
            }
        }

        #endregion

        #region Background Worker

        private static void BackgroundLogLoop()
        {
            while (!_shutdownRequested)
            {
                try
                {
                    _queueEvent.WaitOne(250);
                    CheckLogRotation();
                    FlushQueueToFileAndConsole();
                }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"GONetLog Loop Error: {ex.Message}");
                }
            }
        }

        private static void CheckLogRotation()
        {
            string expectedPath = GetLogFilePath(DateTime.Now);
            if (_currentLogFile != expectedPath)
            {
                _currentLogFile = expectedPath;
                InitializeFileStream();
                CleanupOldLogFiles();
            }
        }

        private static void FlushQueueToFileAndConsole()
        {
            // --- FIX START: Prevent Race Condition on _logBuilder ---
            // Previously, if FlushQueuedLogs() was called manually while BackgroundLogLoop was running,
            // both threads would access the static _logBuilder simultaneously, causing data corruption.
            if (!Monitor.TryEnter(_flushLock)) return;
            try
            {
                LogEntry entry;
                bool wroteToFile = false;

                while (_logQueue.TryDequeue(out entry))
                {
                    Interlocked.Decrement(ref _queuedItemsCount);

                    // --- OPTIMIZATION: Path Caching ---
                    if (!_pathCache.TryGetValue(entry.CallerFilePath, out CachedPathInfo pathInfo))
                    {
                        pathInfo = new CachedPathInfo
                        {
                            FileName = Path.GetFileName(entry.CallerFilePath),
                            SanitizedPath = entry.CallerFilePath.Replace("\\", "/")
                        };
                        _pathCache.TryAdd(entry.CallerFilePath, pathInfo);
                    }

                    // --- 1. Build Log String (Zero Alloc) ---
                    _logBuilder.Clear(); // Safe now due to _flushLock

                    // Target Format: [Log:Info] (Thread:1) (11 Nov 2025 11:16:56.421) (frame:0/131.3823083s) Message

                    _logBuilder.Append(SepBracketOpen).Append(entry.LogKey).Append(SepBracketClose);
                    AppendRole(_logBuilder);

                    _logBuilder.Append(SepThread).Append(entry.ThreadId).Append(SepParenClose);
                    _logBuilder.Append(SepSpace).Append(SepParenOpen);
                    AppendDateTime(_logBuilder, entry.Timestamp); // dd MMM yyyy HH:mm:ss.fff
                    _logBuilder.Append(SepParenClose);

                    _logBuilder.Append(SepFrame).Append(entry.FrameCount).Append(SepSlash).Append(entry.ElapsedSeconds).Append(SepSeconds);

                    // We add the file context to file logs as well, though the user didn't explicitly show it in legacy snippet,
                    // it is standard for debugging.
                    _logBuilder.Append(SepSpace).Append(SepParenOpen).Append(pathInfo.FileName).Append(SepColon).Append(entry.CallerLineNumber).Append(SepParenClose);

                    _logBuilder.Append(SepSpace).Append(entry.Message);

                    if (entry.ExplicitStackTrace != null)
                    {
                        _logBuilder.Append(Environment.NewLine).Append(entry.ExplicitStackTrace);
                    }

                    string logBody = _logBuilder.ToString();
                    string fileLog = logBody + Environment.NewLine;

                    // --- Write to Disk ---
                    bool handledByProfile = false;
                    if (!string.IsNullOrEmpty(entry.ProfileName) && _loggingProfiles.TryGetValue(entry.ProfileName, out var profile))
                    {
                        if (profile.OutputToSeparateFile)
                        {
                            WriteToProfile(profile, fileLog);
                            handledByProfile = true;
                        }
                    }

                    if (!handledByProfile)
                    {
                        WriteToMainFile(fileLog);
                        wroteToFile = true;
                    }

                    // --- 2. Build Unity Console String (With Link) ---
                    // Format: <b>[<a href="...">Script.cs:12</a>]</b> [Log:Info] (Thread:1)...
                    _logBuilder.Clear();

                    // Hyperlink
                    _logBuilder.Append(BoldStart).Append(SepBracketOpen)
                               .Append(LinkTagStart).Append(pathInfo.SanitizedPath)
                               .Append(LinkTagMiddle).Append(entry.CallerLineNumber).Append(LinkTagEnd)
                               .Append(pathInfo.FileName).Append(SepColon).Append(entry.CallerLineNumber)
                               .Append(LinkTagClose)
                               .Append(SepBracketClose).Append(BoldEnd);

                    _logBuilder.Append(SepSpace).Append(logBody);

                    string unityConsoleMsg = _logBuilder.ToString();

                    // --- Write to Unity ---
                    _lastLog = unityConsoleMsg;
                    try { OnLog?.Invoke(unityConsoleMsg); } catch { }

                    switch (entry.UnityLogType)
                    {
                        case LogType.Log: UnityEngine.Debug.Log(unityConsoleMsg); break;
                        case LogType.Warning: UnityEngine.Debug.LogWarning(unityConsoleMsg); break;
                        case LogType.Error:
                        case LogType.Exception:
                        case LogType.Assert: UnityEngine.Debug.LogError(unityConsoleMsg); break;
                    }
                }

                if (wroteToFile || (DateTime.Now - _lastFlushTime > FlushInterval))
                {
                    lock (_fileLock)
                    {
                        if (_streamWriter != null)
                        {
                            _streamWriter.Flush();
                            _lastFlushTime = DateTime.Now;
                        }
                    }
                }
            }
            finally
            {
                Monitor.Exit(_flushLock);
            }
            // --- FIX END ---
        }

        private static void ProcessSingleLogEntryWebGL(LogEntry entry)
        {
            string role = "";
            if (GONetMain.IsServer) role = TagServer;
            else if (GONetMain.IsClient) role = TagClientPrefix + GONetMain.MyAuthorityId + SepBracketClose;

            string msg = $"[{entry.LogKey}]{role} {entry.Message}";
            UnityEngine.Debug.Log(msg);
        }

        private static void ProcessSingleLogEntryForTests(LogEntry entry)
        {
            // Synchronous logging for unit tests (tests require immediate Debug.Log calls)
            // Format matches what tests expect - simple message without extra formatting
            _lastLog = entry.Message;
            try { OnLog?.Invoke(entry.Message); } catch { }

            switch (entry.UnityLogType)
            {
                case LogType.Log: UnityEngine.Debug.Log(entry.Message); break;
                case LogType.Warning: UnityEngine.Debug.LogWarning(entry.Message); break;
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert: UnityEngine.Debug.LogError(entry.Message); break;
            }
        }

        #endregion

        #region Helpers & Optimizations

        private static void AppendRole(StringBuilder sb)
        {
            if (GONetMain.IsServer)
            {
                sb.Append(TagServer);
            }
            else if (GONetMain.IsClient)
            {
                sb.Append(TagClientPrefix).Append(GONetMain.MyAuthorityId).Append(SepBracketClose);
            }
        }

        /// <summary>
        /// Formats: dd MMM yyyy HH:mm:ss.fff
        /// Zero allocation.
        /// </summary>
        private static void AppendDateTime(StringBuilder sb, DateTime time)
        {
            // Day
            int day = time.Day;
            if (day < 10) sb.Append('0');
            sb.Append(day).Append(SepSpace);

            // Month (MMM)
            sb.Append(_monthNames[time.Month - 1]).Append(SepSpace);

            // Year
            sb.Append(time.Year).Append(SepSpace);

            // Time (HH:mm:ss.fff)
            int h = time.Hour;
            if (h < 10) sb.Append('0');
            sb.Append(h).Append(SepColon);

            int m = time.Minute;
            if (m < 10) sb.Append('0');
            sb.Append(m).Append(SepColon);

            int s = time.Second;
            if (s < 10) sb.Append('0');
            sb.Append(s).Append('.');

            int ms = time.Millisecond;
            if (ms < 100) sb.Append('0');
            if (ms < 10) sb.Append('0');
            sb.Append(ms);
        }

        private static void WriteToMainFile(string text)
        {
            lock (_fileLock)
            {
                if (_streamWriter != null) try { _streamWriter.Write(text); } catch { }
            }
        }

        private static void WriteToProfile(LoggingProfile profile, string text)
        {
            lock (profile.WriteLock)
            {
                if (profile.StreamWriter != null)
                {
                    try
                    {
                        profile.StreamWriter.Write(text);
                        profile.StreamWriter.Flush();
                    }
                    catch { }
                }
            }
        }

        private static string GetLogFilePath(DateTime date)
        {
            // Use local builder for thread safety - avoid conflict with background logger
            var sb = new StringBuilder(256);
            sb.Append(LogDirectory).Append(Path.DirectorySeparatorChar);

            // Optional process ID prefix to prevent cross-process file corruption
            if (UseProcessIdPrefix)
            {
                sb.Append(CurrentProcessId).Append('-');
            }

            sb.Append(LogFilePrefix).Append('-');
            AppendDateForFilename(sb, date);
            sb.Append(LogFileExtension);
            return sb.ToString();
        }

        private static void AppendDateForFilename(StringBuilder sb, DateTime date)
        {
            // yyyy-MM-dd
            sb.Append(date.Year).Append('-');
            int month = date.Month;
            if (month < 10) sb.Append('0');
            sb.Append(month).Append('-');
            int day = date.Day;
            if (day < 10) sb.Append('0');
            sb.Append(day);
        }

        private static void CleanupOldLogFiles()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;

                // Use config values with fallback to legacy constant
                int maxLogAgeDays = GONetConfig.MaxLogFileAgeDays > 0 ? GONetConfig.MaxLogFileAgeDays : MaxLogFileDays;
                int maxEventHistoryAgeDays = GONetConfig.MaxEventHistoryFileAgeDays > 0 ? GONetConfig.MaxEventHistoryFileAgeDays : MaxLogFileDays;

                var logCutoff = DateTime.Now.AddDays(-maxLogAgeDays);
                var eventCutoff = DateTime.Now.AddDays(-maxEventHistoryAgeDays);

                int deletedLogFiles = 0;
                int deletedEventFiles = 0;
                long bytesReclaimed = 0;

                // 1. Clean up .log files (GONet logs and profile logs)
                // Match patterns: "gonet-*.log" and "*-gonet-*.log" (with process ID prefix)
                foreach (var file in new DirectoryInfo(LogDirectory).GetFiles($"*{LogFilePrefix}-*{LogFileExtension}"))
                {
                    if (file.LastWriteTime < logCutoff)
                    {
                        try
                        {
                            long fileSize = file.Length;
                            file.Delete();
                            deletedLogFiles++;
                            bytesReclaimed += fileSize;
                        }
                        catch { }
                    }
                }

                // 2. Clean up event history export files (.txt)
                // Pattern: "gonet-events-*.txt"
                var eventHistoryFiles = new DirectoryInfo(LogDirectory)
                    .GetFiles("gonet-events-*.txt")
                    .OrderBy(f => f.LastWriteTime)
                    .ToList();

                // Age-based cleanup
                foreach (var file in eventHistoryFiles.ToArray())
                {
                    if (file.LastWriteTime < eventCutoff)
                    {
                        try
                        {
                            long fileSize = file.Length;
                            file.Delete();
                            eventHistoryFiles.Remove(file);
                            deletedEventFiles++;
                            bytesReclaimed += fileSize;
                        }
                        catch { }
                    }
                }

                // Count-based cleanup (keep only MaxEventHistoryFileCount newest files)
                if (GONetConfig.MaxEventHistoryFileCount > 0 && eventHistoryFiles.Count > GONetConfig.MaxEventHistoryFileCount)
                {
                    int filesToDelete = eventHistoryFiles.Count - GONetConfig.MaxEventHistoryFileCount;
                    for (int i = 0; i < filesToDelete && i < eventHistoryFiles.Count; i++)
                    {
                        try
                        {
                            long fileSize = eventHistoryFiles[i].Length;
                            eventHistoryFiles[i].Delete();
                            deletedEventFiles++;
                            bytesReclaimed += fileSize;
                        }
                        catch { }
                    }
                }

                // 3. Size-based cleanup (if total directory size exceeds limit)
                if (GONetConfig.MaxLogDirectorySizeMB > 0)
                {
                    long maxSizeBytes = (long)GONetConfig.MaxLogDirectorySizeMB * 1024 * 1024;
                    var allLogFiles = new DirectoryInfo(LogDirectory)
                        .GetFiles()
                        .OrderBy(f => f.LastWriteTime)
                        .ToList();

                    long totalSize = allLogFiles.Sum(f => f.Length);

                    // Delete oldest files until under limit
                    while (totalSize > maxSizeBytes && allLogFiles.Count > 1)
                    {
                        var oldest = allLogFiles[0];
                        try
                        {
                            long fileSize = oldest.Length;
                            oldest.Delete();
                            allLogFiles.RemoveAt(0);
                            totalSize -= fileSize;
                            bytesReclaimed += fileSize;

                            if (oldest.Extension == ".log")
                                deletedLogFiles++;
                            else if (oldest.Extension == ".txt")
                                deletedEventFiles++;
                        }
                        catch
                        {
                            allLogFiles.RemoveAt(0); // Skip this file
                        }
                    }
                }

                // Log cleanup summary if enabled and anything was deleted
                if (GONetConfig.LogCleanupOperations && (deletedLogFiles > 0 || deletedEventFiles > 0))
                {
                    UnityEngine.Debug.Log($"GONetLog: Cleanup complete - Deleted {deletedLogFiles} log files, {deletedEventFiles} event history files. Reclaimed {bytesReclaimed / 1024:N0} KB.");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"GONetLog: Cleanup warning: {ex.Message}");
            }
        }

        #endregion
    }
}