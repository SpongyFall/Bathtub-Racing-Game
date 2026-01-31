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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace GONet.Utils
{
    public static class NetworkUtils
    {
        const string ANY_IP = "[::]"; // IPv6 any address, also works for IPv4 in dual-stack contexts
        const string LOOPBACK_IP = "127.0.0.1";
        const string LOCALHOST = "localhost";
        const string LOOPBACK_IPV6 = "::1";

        public static bool IsIPAddressOnLocalMachine(string ipAddressToCheck)
        {
            if (IsLoopbackAddress(ipAddressToCheck) || ANY_IP == ipAddressToCheck)
            {
                return true;
            }
            else
            {
                IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                return host.AddressList.Any(ipAddress =>
                    (ipAddress.AddressFamily == AddressFamily.InterNetwork || ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    && ipAddress.ToString() == ipAddressToCheck);
            }
        }

        /// <summary>
        /// Fast check if the given IP address is a loopback/localhost address.
        /// This is useful for detecting when client and server are on the same machine.
        ///
        /// Unlike <see cref="IsIPAddressOnLocalMachine"/>, this does NOT perform DNS lookups
        /// or check against the machine's network interfaces - it only checks for standard
        /// loopback addresses (127.x.x.x, localhost, ::1).
        /// </summary>
        /// <param name="ipAddress">IP address or hostname to check</param>
        /// <returns>True if the address is a loopback/localhost address</returns>
        public static bool IsLoopbackAddress(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) return false;

            string ip = ipAddress.Trim().ToLowerInvariant();
            return ip == LOOPBACK_IP ||
                   ip == LOCALHOST ||
                   ip == LOOPBACK_IPV6 ||
                   ip.StartsWith("127.");  // Any 127.x.x.x is loopback (RFC 3330)
        }

        /// <summary>
        /// Transport-agnostic check for local server instance on specified port.
        ///
        /// IMPORTANT: This uses a lock file mechanism to detect servers across ALL transport types
        /// (NetcodeIO, Steamworks, etc.) since Steam sockets aren't visible to standard .NET socket API.
        ///
        /// Lock file location: Application.persistentDataPath/GONet_Server_{port}.lock
        ///
        /// Server lifecycle:
        /// - CreateServerLockFile() when server starts → creates lock file with PID
        /// - RemoveServerLockFile() when server stops → deletes lock file
        /// - UpdateServerLockFile() every frame → keeps timestamp fresh
        /// - IsLocalPortListening() checks if lock file exists AND process is alive
        ///
        /// Edge case handling:
        /// - Process crash → stale lock file detected and removed (no updates for 30s)
        /// - PID reuse → validates process name matches Unity
        /// - Corrupted lock file → treated as invalid, deleted
        /// - File system errors → falls back to socket check
        /// </summary>
        public static bool IsLocalPortListening(int port)
        {
            // NEW PATH: Check lock file (works for ALL transports including Steamworks)
            string lockFilePath = GetServerLockFilePath(port);

            if (System.IO.File.Exists(lockFilePath))
            {
                try
                {
                    // Read and validate lock file contents
                    string lockFileContent = System.IO.File.ReadAllText(lockFilePath);

                    if (IsLockFileValid(lockFileContent, lockFilePath))
                    {
                        // Valid lock file - server is running
                        return true;
                    }
                    else
                    {
                        // Invalid/stale lock file - clean it up
                        try
                        {
                            System.IO.File.Delete(lockFilePath);
                            GONetLog.Info($"[NetworkUtils] Removed stale/invalid server lock file for port {port}");
                        }
                        catch { }
                    }
                }
                catch
                {
                    // Error reading/parsing lock file - treat as invalid and try to clean up
                    try { System.IO.File.Delete(lockFilePath); } catch { }
                }
            }

            // FALLBACK: Original .NET socket check (works for NetcodeIO, fails for Steamworks)
            var endpoint = new IPEndPoint(IPAddress.IPv6Any, port);
            Socket socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // allow dual-stack
                // TODO does this make sense in this contect? socket.DualMode = true;
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                socket.Bind(endpoint);
            }
            catch (SocketException socketException)
            {
                const string IN_USE = "Address already in use";
                if (socketException.ErrorCode == (int)SocketError.AddressAlreadyInUse || socketException.Message == IN_USE)
                {
                    return true;
                }
            }
            finally
            {
                socket.Close();
            }

            return false;
        }

        /// <summary>
        /// Create lock file to indicate server is running on specified port.
        /// Call this when server starts successfully.
        ///
        /// Lock file format (JSON-like for easy parsing):
        /// PID:{processId}
        /// Port:{port}
        /// Started:{timestamp}
        /// ProcessName:{processName}
        /// </summary>
        public static void CreateServerLockFile(int port)
        {
            try
            {
                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                string lockFilePath = GetServerLockFilePath(port);
                string processInfo = $"PID:{currentProcess.Id}\nPort:{port}\nStarted:{System.DateTime.Now:o}\nProcessName:{currentProcess.ProcessName}";
                System.IO.File.WriteAllText(lockFilePath, processInfo);
                GONetLog.Info($"[NetworkUtils] Created server lock file for port {port} (PID: {currentProcess.Id})");
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[NetworkUtils] Failed to create server lock file for port {port}: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove lock file when server stops.
        /// Call this in server shutdown/cleanup.
        /// </summary>
        public static void RemoveServerLockFile(int port)
        {
            try
            {
                string lockFilePath = GetServerLockFilePath(port);
                if (System.IO.File.Exists(lockFilePath))
                {
                    System.IO.File.Delete(lockFilePath);
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Failed to remove server lock file for port {port}: {ex.Message}");
            }
        }

        /// <summary>
        /// Update lock file timestamp to keep it fresh (prevents stale detection).
        /// Server should call this periodically (e.g., every 10 seconds).
        /// </summary>
        public static void UpdateServerLockFile(int port)
        {
            try
            {
                string lockFilePath = GetServerLockFilePath(port);
                if (System.IO.File.Exists(lockFilePath))
                {
                    System.IO.File.SetLastWriteTime(lockFilePath, System.DateTime.Now);
                }
            }
            catch
            {
                // Ignore errors - lock file will age out naturally
            }
        }

        /// <summary>
        /// Validate lock file is current and belongs to a running Unity process.
        ///
        /// Validation checks:
        /// 1. File modified within last 30 seconds (heartbeat check)
        /// 2. PID can be parsed
        /// 3. Process with that PID exists
        /// 4. Process name matches Unity (prevents PID reuse false positives)
        /// </summary>
        private static bool IsLockFileValid(string lockFileContent, string lockFilePath)
        {
            try
            {
                // Check 1: File age (heartbeat check - updated every frame by server)
                // This is the MOST RELIABLE cross-platform check
                var lastWrite = System.IO.File.GetLastWriteTime(lockFilePath);
                var age = System.DateTime.Now - lastWrite;

                if (age.TotalSeconds > 5)
                {
                    // Lock file not updated in 5 seconds - process likely crashed/frozen
                    // (Server updates every frame, so 5 seconds at 60fps = 300 missed updates)
                    GONetLog.Info($"[NetworkUtils] Lock file age: {age.TotalSeconds:F1}s (stale - process likely crashed)");
                    return false;
                }

                // Check 2: Parse PID from lock file (OPTIONAL - for additional validation)
                // Format: "PID:12345\nPort:40000\n..."
                int pid = -1;
                string processName = null;

                foreach (string line in lockFileContent.Split('\n'))
                {
                    if (line.StartsWith("PID:"))
                    {
                        int.TryParse(line.Substring(4), out pid);
                    }
                    else if (line.StartsWith("ProcessName:"))
                    {
                        processName = line.Substring(12).Trim();
                    }
                }

                // Check 3: Process validation (OPTIONAL - only if platform supports it)
                if (pid > 0)
                {
                    try
                    {
                        // Try to validate PID exists and matches Unity process
                        // This may throw NotSupportedException on some platforms (WebGL, some .NET versions)
                        System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);

                        // Check process name if we can (may also throw on some platforms)
                        try
                        {
                            string currentProcessName = process.ProcessName;
                            bool isUnityProcess = currentProcessName.IndexOf("Unity", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  currentProcessName.IndexOf("GONet", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  (processName != null && currentProcessName.Equals(processName, System.StringComparison.OrdinalIgnoreCase));

                            if (!isUnityProcess)
                            {
                                // PID reused by different application
                                GONetLog.Info($"[NetworkUtils] PID {pid} is running but process name '{currentProcessName}' doesn't match Unity/GONet (PID reuse detected)");
                                return false;
                            }
                        }
                        catch (System.NotSupportedException)
                        {
                            // Platform doesn't support process name - skip this check
                            // (PlatformNotSupportedException inherits from NotSupportedException, so this catches both)
                            GONetLog.Debug($"[NetworkUtils] Process name check not supported on this platform (PID validation skipped)");
                        }
                    }
                    catch (System.ArgumentException)
                    {
                        // Process doesn't exist (ArgumentException thrown by GetProcessById)
                        GONetLog.Info($"[NetworkUtils] Process PID {pid} not found (process exited)");
                        return false;
                    }
                    catch (System.NotSupportedException)
                    {
                        // Platform doesn't support GetProcessById - rely on heartbeat only
                        // (PlatformNotSupportedException inherits from NotSupportedException, so this catches both)
                        GONetLog.Debug($"[NetworkUtils] Process validation not supported on this platform (relying on heartbeat check only)");
                    }
                }

                // All checks passed (or were skipped due to platform limitations)
                // File age check is sufficient for cross-platform detection
                return true;
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[NetworkUtils] Error validating lock file: {ex.Message}");
                return false;
            }
        }

        private static string GetServerLockFilePath(int port)
        {
            // Use Application.persistentDataPath for cross-platform compatibility
            // Works on: Windows, macOS, Linux, iOS, Android, WebGL, etc.
            string basePath;

            if (UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WebGLPlayer)
            {
                // WebGL has limited file system access
                basePath = UnityEngine.Application.temporaryCachePath;
            }
            else
            {
                // Default for all other platforms
                basePath = UnityEngine.Application.persistentDataPath;
            }

            return System.IO.Path.Combine(basePath, $"GONet_Server_{port}.lock");
        }

        public static IPEndPoint GetIPEndPointFromHostName(string hostName, int port, bool throwIfMoreThanOneIP = false)
        {
            var addresses = System.Net.Dns.GetHostAddresses(hostName);
            if (addresses.Length == 0)
            {
                throw new ArgumentException("Unable to retrieve address from specified host name.", nameof(hostName));
            }
            else if (throwIfMoreThanOneIP && addresses.Length > 1)
            {
                throw new ArgumentException("There is more than one IP address for the specified host.", nameof(hostName));
            }

            // Prefer IPv6 if available, otherwise use the first address
            var preferredAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6) ?? addresses[0];
            return new IPEndPoint(preferredAddress, port);
        }

        public static string GetEndpointDebugString(EndPoint endpoint)
        {
            if (endpoint == null)
                return "Endpoint is null";

            if (endpoint is IPEndPoint ipEndPoint)
            {
                return $"Endpoint Details:\n" +
                       $"  - Type: IPEndPoint\n" +
                       $"  - Address: {ipEndPoint.Address}\n" +
                       $"  - Port: {ipEndPoint.Port}\n" +
                       $"  - Address Family: {ipEndPoint.AddressFamily}\n" +
                       $"  - Is IPv4: {ipEndPoint.AddressFamily == AddressFamily.InterNetwork}\n" +
                       $"  - Is IPv6: {ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6}\n" +
                       $"  - Is IPv4 Mapped to IPv6: {ipEndPoint.Address.IsIPv4MappedToIPv6}\n" +
                       $"  - Full Address: {ipEndPoint}";
            }
            else
            {
                return $"Endpoint Details:\n" +
                       $"  - Type: {endpoint.GetType().Name}\n" +
                       $"  - Address: {endpoint}";
            }
        }

        public static bool AreSameAddressFamilyOrMapped(IPAddress a, IPAddress b) =>
            a.Equals(b) ||
                (a.AddressFamily != b.AddressFamily &&
                    (a.MapToIPv4().Equals(b) || a.MapToIPv6().Equals(b)));

        /// <summary>
        /// Returns <c>true</c> when the two <see cref="EndPoint"/>s refer to the same
        /// IP *and* both ports match, treating IPv4‑mapped IPv6 addresses
        /// (<c>::ffff:x.x.x.x</c>) as equivalent to their raw‑IPv4 form.
        /// <para/>
        /// If either <see cref="EndPoint"/> is not an <see cref="IPEndPoint"/>,
        /// the method returns <c>false</c>.
        /// </summary>
        public static bool AreSameAddressFamilyOrMapped(EndPoint aEP, EndPoint bEP)
        {
            // must be IPEndPoint instances
            if (aEP is not IPEndPoint a || bEP is not IPEndPoint b)
                return false;

            // ports must match first
            if (a.Port != b.Port)
                return false;

            // identical addresses → early‑out
            if (a.Address.Equals(b.Address))
                return true;

            // cross‑family: treat v4‑mapped‑v6 as the same host
            if (a.AddressFamily != b.AddressFamily)
            {
                if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.Address.IsIPv4MappedToIPv6 &&
                    a.Address.MapToIPv4().Equals(b.Address))
                    return true;

                if (b.AddressFamily == AddressFamily.InterNetworkV6 && b.Address.IsIPv4MappedToIPv6 &&
                    b.Address.MapToIPv4().Equals(a.Address))
                    return true;
            }

            return false;
        }

        public static bool DoEndpointsMatch(IPEndPoint listen4, IPEndPoint listen6, IPEndPoint tokenEP)
        {
            // 1. Port must match exactly
            if (tokenEP.Port != listen4.Port && tokenEP.Port != listen6.Port)
            {
                return false;
            }

            // 2. wildcard bind → accept any address
            if (listen4.Address.Equals(IPAddress.Any) || listen4.Address.Equals(IPAddress.IPv6Any) ||
                listen6.Address.Equals(IPAddress.Any) || listen6.Address.Equals(IPAddress.IPv6Any))
            {
                return true;   // port already matched above
            }

            // 3. Compare addresses with v4‑mapped equivalence
            bool addrMatches =
                AreSameAddressFamilyOrMapped(tokenEP.Address, listen4.Address) ||
                AreSameAddressFamilyOrMapped(tokenEP.Address, listen6.Address);

            return addrMatches;
        }

        public static bool DoEndpointsMatch(IPEndPoint listen, IPEndPoint tokenEP)
        {
            // 1. Port must match exactly
            if (tokenEP.Port != listen.Port)
            {
                return false;
            }

            // 2. wildcard bind → accept any address
            if (listen.Address.Equals(IPAddress.Any) || listen.Address.Equals(IPAddress.IPv6Any))
            {
                return true;   // port already matched above
            }

            return AreSameAddressFamilyOrMapped(tokenEP.Address, listen.Address);
        }

        public static bool AreSameIP(IPAddress a, IPAddress b)
        {
            if (a.Equals(b)) return true;

            // treat v4‑mapped‑v6 as equal to raw v4
            if (a.AddressFamily != b.AddressFamily)
            {
                if (a.IsIPv4MappedToIPv6 && a.MapToIPv4().Equals(b)) return true;
                if (b.IsIPv4MappedToIPv6 && b.MapToIPv4().Equals(a)) return true;
            }
            return false;
        }

        public static List<IPEndPoint> BuildDualStackEndpointList(string host, int port)
        {
            // 1. Resolve whatever the user typed.
            IPAddress[] resolved;
            if (!IPAddress.TryParse(host, out var literal))
                resolved = Dns.GetHostAddresses(host);
            else
                resolved = new[] { literal };

            bool hasV4 = resolved.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            bool hasV6 = resolved.Any(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);

            var list = new List<IPEndPoint>(
                resolved.Select(ip => new IPEndPoint(ip, port)));

            // 2a. If we only got IPv4 but we KNOW the server is dual‑stack,
            //     inject the mapped‑v6 form *or* ::1 for loop‑back.
            if (!hasV6)
            {
                if (IPAddress.IsLoopback(resolved[0]))
                    list.Insert(0, new IPEndPoint(IPAddress.IPv6Loopback, port));
                else
                    list.Insert(0, new IPEndPoint(resolved[0].MapToIPv6(), port));
            }

            // 2b. If we only got IPv6, inject IPv4.
            if (!hasV4)
            {
                if (resolved[0].IsIPv4MappedToIPv6)
                    list.Add(new IPEndPoint(resolved[0].MapToIPv4(), port));
                else if (IPAddress.IsLoopback(resolved[0]))
                    list.Add(new IPEndPoint(IPAddress.Loopback, port));
                // else: we can’t infer a public v4; leave list unchanged
            }

            /* Optional: put IPv6 first so the client tries it before v4 */
            list.Sort((a, b) =>
            {
                int aKey = a.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1;
                int bKey = b.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1;
                return aKey.CompareTo(bKey);
            });
            return list;
        }

        /// <summary>
        /// Gets the best local IPv4 address for LAN communication.
        /// Prefers LAN addresses (192.168.x.x, 10.x.x.x, 172.16-31.x.x) over loopback.
        /// Returns loopback (127.0.0.1) if no LAN address is found.
        /// </summary>
        /// <returns>The best local IPv4 address as a 32-bit integer, or 0 if none found</returns>
        public static uint GetLocalIPv4ForLAN()
        {
            try
            {
                IPAddress bestAddress = null;
                int bestScore = -1;

                foreach (var netInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    // Skip non-operational interfaces
                    if (netInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;

                    // Skip loopback and tunnel interfaces
                    if (netInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                        netInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                        continue;

                    foreach (var unicastAddr in netInterface.GetIPProperties().UnicastAddresses)
                    {
                        var addr = unicastAddr.Address;

                        // Only IPv4
                        if (addr.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        int score = ScoreIPv4Address(addr);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestAddress = addr;
                        }
                    }
                }

                // Fallback to loopback if nothing better found
                if (bestAddress == null)
                {
                    bestAddress = IPAddress.Loopback;
                }

                // Convert to uint (network byte order)
                byte[] bytes = bestAddress.GetAddressBytes();
                return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[NetworkUtils] Failed to get local IPv4: {ex.Message}");
                return 0x7F000001; // 127.0.0.1 as fallback
            }
        }

        /// <summary>
        /// Scores an IPv4 address for LAN suitability.
        /// Higher score = better for LAN communication.
        /// </summary>
        private static int ScoreIPv4Address(IPAddress addr)
        {
            byte[] bytes = addr.GetAddressBytes();
            byte first = bytes[0];
            byte second = bytes[1];

            // Loopback (127.x.x.x) - lowest priority
            if (first == 127)
                return 0;

            // Link-local (169.254.x.x) - APIPA, not great
            if (first == 169 && second == 254)
                return 1;

            // Class A private (10.x.x.x) - good for LAN
            if (first == 10)
                return 10;

            // Class C private (192.168.x.x) - most common LAN
            if (first == 192 && second == 168)
                return 10;

            // Class B private (172.16-31.x.x)
            if (first == 172 && second >= 16 && second <= 31)
                return 10;

            // Public IP - works for internet but may not be reachable from LAN
            return 5;
        }

        /// <summary>
        /// Checks if an IPv4 address (as uint) is a private/LAN address.
        /// </summary>
        public static bool IsPrivateIPv4(uint ipv4)
        {
            byte first = (byte)((ipv4 >> 24) & 0xFF);
            byte second = (byte)((ipv4 >> 16) & 0xFF);

            // 10.x.x.x
            if (first == 10) return true;

            // 172.16-31.x.x
            if (first == 172 && second >= 16 && second <= 31) return true;

            // 192.168.x.x
            if (first == 192 && second == 168) return true;

            // 127.x.x.x (loopback)
            if (first == 127) return true;

            return false;
        }

        /// <summary>
        /// Checks if an IPv4 address (as uint) is a loopback address (127.x.x.x).
        /// </summary>
        public static bool IsLoopbackIPv4(uint ipv4)
        {
            byte first = (byte)((ipv4 >> 24) & 0xFF);
            return first == 127;
        }

        /// <summary>
        /// Gets the best local IPv6 address for LAN communication.
        /// Prefers link-local or site-local addresses over global.
        /// Returns IPv6Loopback (::1) if no LAN address is found.
        /// </summary>
        public static IPAddress GetLocalIPv6ForLAN()
        {
            try
            {
                IPAddress bestAddress = null;
                int bestScore = -1;

                foreach (var netInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (netInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;

                    if (netInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                        netInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                        continue;

                    foreach (var unicastAddr in netInterface.GetIPProperties().UnicastAddresses)
                    {
                        var addr = unicastAddr.Address;

                        if (addr.AddressFamily != AddressFamily.InterNetworkV6)
                            continue;

                        int score = ScoreIPv6Address(addr);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestAddress = addr;
                        }
                    }
                }

                return bestAddress ?? IPAddress.IPv6Loopback;
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[NetworkUtils] Failed to get local IPv6: {ex.Message}");
                return IPAddress.IPv6Loopback;
            }
        }

        /// <summary>
        /// Scores an IPv6 address for LAN suitability.
        /// </summary>
        private static int ScoreIPv6Address(IPAddress addr)
        {
            // Loopback (::1) - lowest priority
            if (IPAddress.IsLoopback(addr))
                return 0;

            // Link-local (fe80::) - best for LAN
            if (addr.IsIPv6LinkLocal)
                return 10;

            // Site-local (fec0::) - deprecated but still good for LAN
            if (addr.IsIPv6SiteLocal)
                return 9;

            // Unique local (fc00::/7) - similar to private IPv4
            byte[] bytes = addr.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) // fc00::/7
                return 8;

            // Global unicast - works but may not be reachable from LAN
            return 5;
        }

        /// <summary>
        /// Gets both IPv4 and IPv6 addresses for this machine (dual-stack).
        /// Returns the best address for each protocol.
        /// </summary>
        public static (IPAddress IPv4, IPAddress IPv6) GetLocalDualStackAddresses()
        {
            uint ipv4Int = GetLocalIPv4ForLAN();
            IPAddress ipv4 = ipv4Int != 0
                ? new IPAddress(new byte[]
                {
                    (byte)((ipv4Int >> 24) & 0xFF),
                    (byte)((ipv4Int >> 16) & 0xFF),
                    (byte)((ipv4Int >> 8) & 0xFF),
                    (byte)(ipv4Int & 0xFF)
                })
                : IPAddress.Loopback;

            IPAddress ipv6 = GetLocalIPv6ForLAN();

            return (ipv4, ipv6);
        }

        /// <summary>
        /// Creates a GONetConnectionEndpoint with both IPv4 and IPv6 addresses.
        /// </summary>
        public static GONet.DistributedHost.GONetConnectionEndpoint CreateLocalDualStackEndpoint(ushort port)
        {
            var (ipv4, ipv6) = GetLocalDualStackAddresses();
            return GONet.DistributedHost.GONetConnectionEndpoint.CreateDualStack(ipv4, ipv6, port);
        }
    }
}
