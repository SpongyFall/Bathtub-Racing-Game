using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GONet.Sample
{
    /// <summary>
    /// Custom logger for test execution results.
    /// Writes test logs to Resources/Tests/Results/{testname}_{timestamp}.log
    ///
    /// CLEANUP: Call GONetTestLogger.CleanupOldLogs() periodically to prevent
    /// test logs from accumulating indefinitely.
    /// </summary>
    public class GONetTestLogger : IDisposable
    {
        private StreamWriter writer;
        private string logFilePath;
        private StringBuilder buffer = new StringBuilder();

        /// <summary>
        /// Maximum age in days for test log files before cleanup.
        /// Default: 7 days
        /// </summary>
        public static int MaxTestLogAgeDays = 7;

        /// <summary>
        /// Maximum number of test log files to keep.
        /// Default: 50 files
        /// </summary>
        public static int MaxTestLogFileCount = 50;

        public GONetTestLogger(string testName)
        {
            // Create log file in Resources/Tests/Results (outside of Resources processing)
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeTestName = SanitizeFileName(testName);
            string fileName = $"{safeTestName}_{timestamp}.log";

            // Get the Assets folder path (works in both Editor and builds)
            string projectPath = Application.dataPath;
            string resultsFolder = Path.Combine(projectPath, "GONet", "Sample", "Resources", "Tests", "Results");

            // Create directory if it doesn't exist
            Directory.CreateDirectory(resultsFolder);

            logFilePath = Path.Combine(resultsFolder, fileName);

            try
            {
                writer = new StreamWriter(logFilePath, false, Encoding.UTF8);
                writer.AutoFlush = true;

                // Write header
                WriteHeader(testName);

                GONetLog.Info($"[TestLogger] Created log file: {logFilePath}");
            }
            catch (Exception ex)
            {
                GONetLog.Error($"[TestLogger] Failed to create log file: {ex.Message}");
                writer = null;
            }
        }

        private void WriteHeader(string testName)
        {
            if (writer == null)
                return;

            writer.WriteLine("================================================================================");
            writer.WriteLine($"GONet Test Execution Log");
            writer.WriteLine($"Test: {testName}");
            writer.WriteLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"Unity Version: {Application.unityVersion}");
            writer.WriteLine($"Platform: {Application.platform}");
            writer.WriteLine("================================================================================");
            writer.WriteLine();
        }

        public void Log(string message)
        {
            if (writer == null)
                return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logLine = $"[{timestamp}] {message}";

            writer.WriteLine(logLine);

            // Also log to GONet's log system
            GONetLog.Info($"[TEST] {message}");
        }

        public void LogStep(int stepIndex, int totalSteps, string stepType, string details = "")
        {
            string stepInfo = $"Step {stepIndex + 1}/{totalSteps}: {stepType}";
            if (!string.IsNullOrEmpty(details))
                stepInfo += $" - {details}";

            Log(stepInfo);
        }

        public void LogPass(string testName)
        {
            Log($"✓ PASS | {testName}");
        }

        public void LogFail(string testName, string reason)
        {
            Log($"❌ FAIL | {testName}");
            if (!string.IsNullOrEmpty(reason))
                Log($"  Reason: {reason}");
        }

        public void LogSummary(int passed, int failed)
        {
            writer.WriteLine();
            writer.WriteLine("================================================================================");
            writer.WriteLine("TEST SUMMARY");
            writer.WriteLine("================================================================================");
            writer.WriteLine($"Total Passed:  {passed}");
            writer.WriteLine($"Total Failed:  {failed}");
            writer.WriteLine($"Total Tests:   {passed + failed}");
            writer.WriteLine($"Success Rate:  {(passed + failed > 0 ? (passed * 100f / (passed + failed)) : 0f):F1}%");
            writer.WriteLine("================================================================================");
        }

        private string SanitizeFileName(string fileName)
        {
            // Remove invalid file name characters
            char[] invalids = Path.GetInvalidFileNameChars();
            string safe = fileName;

            foreach (char c in invalids)
            {
                safe = safe.Replace(c, '_');
            }

            // Replace spaces with underscores
            safe = safe.Replace(' ', '_');

            return safe;
        }

        public void Dispose()
        {
            if (writer != null)
            {
                writer.Flush();
                writer.Close();
                writer.Dispose();
                writer = null;

                GONetLog.Info($"[TestLogger] Closed log file: {logFilePath}");
            }
        }

        public string GetLogFilePath()
        {
            return logFilePath;
        }

        /// <summary>
        /// Gets the path to the test results folder.
        /// </summary>
        public static string GetResultsFolder()
        {
            return Path.Combine(Application.dataPath, "GONet", "Sample", "Resources", "Tests", "Results");
        }

        /// <summary>
        /// Cleans up old test log files based on MaxTestLogAgeDays and MaxTestLogFileCount.
        /// Call this periodically (e.g., in test setup) to prevent log accumulation.
        /// </summary>
        /// <returns>Number of files deleted</returns>
        public static int CleanupOldLogs()
        {
            int filesDeleted = 0;
            string resultsFolder = GetResultsFolder();

            if (!Directory.Exists(resultsFolder))
            {
                return 0;
            }

            try
            {
                var logFiles = new DirectoryInfo(resultsFolder)
                    .GetFiles("*.log")
                    .OrderBy(f => f.LastWriteTime)
                    .ToList();

                // Age-based cleanup
                var cutoff = DateTime.Now.AddDays(-MaxTestLogAgeDays);
                foreach (var file in logFiles.ToArray())
                {
                    if (file.LastWriteTime < cutoff)
                    {
                        try
                        {
                            file.Delete();
                            logFiles.Remove(file);
                            filesDeleted++;
                        }
                        catch { }
                    }
                }

                // Count-based cleanup (keep only MaxTestLogFileCount newest files)
                if (MaxTestLogFileCount > 0 && logFiles.Count > MaxTestLogFileCount)
                {
                    int filesToDelete = logFiles.Count - MaxTestLogFileCount;
                    for (int i = 0; i < filesToDelete && i < logFiles.Count; i++)
                    {
                        try
                        {
                            logFiles[i].Delete();
                            filesDeleted++;
                        }
                        catch { }
                    }
                }

                if (filesDeleted > 0)
                {
                    GONetLog.Info($"[TestLogger] Cleanup: Deleted {filesDeleted} old test log files.");
                }
            }
            catch (Exception ex)
            {
                GONetLog.Warning($"[TestLogger] Cleanup warning: {ex.Message}");
            }

            return filesDeleted;
        }

        /// <summary>
        /// Deletes ALL test log files.
        /// </summary>
        /// <returns>Number of files deleted</returns>
        public static int DeleteAllLogs()
        {
            int filesDeleted = 0;
            string resultsFolder = GetResultsFolder();

            if (!Directory.Exists(resultsFolder))
            {
                return 0;
            }

            try
            {
                foreach (var file in new DirectoryInfo(resultsFolder).GetFiles("*.log"))
                {
                    try
                    {
                        file.Delete();
                        filesDeleted++;
                    }
                    catch { }
                }

                GONetLog.Info($"[TestLogger] Deleted all {filesDeleted} test log files.");
            }
            catch (Exception ex)
            {
                GONetLog.Warning($"[TestLogger] DeleteAllLogs warning: {ex.Message}");
            }

            return filesDeleted;
        }

        /// <summary>
        /// Gets the total size in bytes of all test log files.
        /// </summary>
        public static long GetTotalLogSize()
        {
            string resultsFolder = GetResultsFolder();

            if (!Directory.Exists(resultsFolder))
            {
                return 0;
            }

            try
            {
                return new DirectoryInfo(resultsFolder)
                    .GetFiles("*.log")
                    .Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }
    }
}
