using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GONet.Tests.Network
{
    /// <summary>
    /// Standalone test to measure actual packet latency on loopback with Clumsy.
    /// This isolates network behavior from all GONet logic to understand what
    /// Clumsy is actually doing to packets.
    ///
    /// HOW TO USE:
    /// 1. Start Clumsy with 50ms inbound + 50ms outbound lag on loopback
    /// 2. Run this test
    /// 3. Compare results with Clumsy disabled
    ///
    /// EXPECTED RESULTS:
    /// - Without Clumsy: RTT ~0-1ms (loopback is essentially instant)
    /// - With Clumsy 50ms each way: RTT ~100-110ms (50ms out + 50ms back + processing)
    /// - If RTT is 1000ms+, Clumsy is doing something unexpected (buffering, batching, etc.)
    /// </summary>
    [TestFixture]
    public class ClumsyLoopbackLatencyTest
    {
        private const int TEST_PORT = 17777; // Avoid conflict with GONet's 7777
        private const int NUM_PACKETS = 20;
        private const int PACKET_INTERVAL_MS = 100; // Send one packet every 100ms

        [Test]
        [Category("Network")]
        [Explicit("Run manually with/without Clumsy to compare")]
        public void Measure_UDP_Loopback_RTT()
        {
            var results = new List<double>();
            var errors = new List<string>();

            using (var serverSocket = new UdpClient(TEST_PORT))
            using (var clientSocket = new UdpClient())
            {
                serverSocket.Client.ReceiveTimeout = 5000; // 5 second timeout
                clientSocket.Client.ReceiveTimeout = 5000;

                var serverEndpoint = new IPEndPoint(IPAddress.Loopback, TEST_PORT);
                IPEndPoint remoteEndpoint = null;

                // Start server thread
                var serverThread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < NUM_PACKETS; i++)
                        {
                            // Receive packet from client
                            byte[] received = serverSocket.Receive(ref remoteEndpoint);

                            // Echo it back immediately
                            serverSocket.Send(received, received.Length, remoteEndpoint);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors)
                        {
                            errors.Add($"Server error: {ex.Message}");
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "UDP-Echo-Server"
                };

                serverThread.Start();
                Thread.Sleep(100); // Give server time to start

                var stopwatch = new Stopwatch();

                // Client sends packets and measures RTT
                for (int i = 0; i < NUM_PACKETS; i++)
                {
                    try
                    {
                        // Create packet with sequence number and timestamp
                        long sendTimeTicks = Stopwatch.GetTimestamp();
                        byte[] packet = new byte[16];
                        BitConverter.GetBytes(i).CopyTo(packet, 0);
                        BitConverter.GetBytes(sendTimeTicks).CopyTo(packet, 4);

                        stopwatch.Restart();

                        // Send to server
                        clientSocket.Send(packet, packet.Length, serverEndpoint);

                        // Wait for echo response
                        IPEndPoint responseEndpoint = null;
                        byte[] response = clientSocket.Receive(ref responseEndpoint);

                        stopwatch.Stop();

                        double rttMs = stopwatch.Elapsed.TotalMilliseconds;
                        results.Add(rttMs);

                        int seq = BitConverter.ToInt32(response, 0);
                        UnityEngine.Debug.Log($"[UDP-RTT] Packet {seq}: RTT = {rttMs:F2}ms");

                        // Wait before sending next packet
                        Thread.Sleep(PACKET_INTERVAL_MS);
                    }
                    catch (SocketException ex)
                    {
                        errors.Add($"Packet {i} timeout/error: {ex.Message}");
                        UnityEngine.Debug.LogWarning($"[UDP-RTT] Packet {i} FAILED: {ex.Message}");
                    }
                }

                serverThread.Join(2000);
            }

            // Report results
            if (results.Count > 0)
            {
                double min = double.MaxValue, max = 0, sum = 0;
                foreach (var rtt in results)
                {
                    if (rtt < min) min = rtt;
                    if (rtt > max) max = rtt;
                    sum += rtt;
                }
                double avg = sum / results.Count;

                UnityEngine.Debug.Log($"\n========== UDP LOOPBACK RTT RESULTS ==========");
                UnityEngine.Debug.Log($"Packets sent: {NUM_PACKETS}");
                UnityEngine.Debug.Log($"Packets received: {results.Count}");
                UnityEngine.Debug.Log($"Packets lost: {NUM_PACKETS - results.Count}");
                UnityEngine.Debug.Log($"Min RTT: {min:F2}ms");
                UnityEngine.Debug.Log($"Max RTT: {max:F2}ms");
                UnityEngine.Debug.Log($"Avg RTT: {avg:F2}ms");
                UnityEngine.Debug.Log($"===============================================\n");

                // Assertions
                Assert.That(results.Count, Is.GreaterThan(0), "Should receive at least some packets");

                // With 50ms Clumsy each way, we expect ~100-150ms RTT, not 1000ms+
                Assert.That(avg, Is.LessThan(500),
                    $"Average RTT should be under 500ms even with Clumsy. Got {avg:F2}ms - " +
                    "if this fails with Clumsy enabled, Clumsy is doing something unexpected.");
            }
            else
            {
                Assert.Fail($"No packets received! Errors: {string.Join(", ", errors)}");
            }
        }

        [Test]
        [Category("Network")]
        [Explicit("Run manually with/without Clumsy to compare")]
        public void Measure_UDP_Loopback_RTT_Burst()
        {
            // This test sends multiple packets quickly to see if Clumsy batches them
            var results = new List<(int seq, double rttMs, long sendTime, long recvTime)>();
            var errors = new List<string>();
            const int BURST_SIZE = 10;

            using (var serverSocket = new UdpClient(TEST_PORT + 1))
            using (var clientSocket = new UdpClient())
            {
                serverSocket.Client.ReceiveTimeout = 10000;
                clientSocket.Client.ReceiveTimeout = 10000;

                var serverEndpoint = new IPEndPoint(IPAddress.Loopback, TEST_PORT + 1);
                IPEndPoint remoteEndpoint = null;

                var receivedPackets = new List<byte[]>();
                var serverDone = new ManualResetEventSlim(false);

                // Server echoes all packets
                var serverThread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < BURST_SIZE; i++)
                        {
                            byte[] received = serverSocket.Receive(ref remoteEndpoint);
                            // Add server timestamp
                            byte[] response = new byte[24];
                            Array.Copy(received, 0, response, 0, 16);
                            BitConverter.GetBytes(Stopwatch.GetTimestamp()).CopyTo(response, 16);
                            serverSocket.Send(response, response.Length, remoteEndpoint);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors)
                        {
                            errors.Add($"Server: {ex.Message}");
                        }
                    }
                    finally
                    {
                        serverDone.Set();
                    }
                })
                {
                    IsBackground = true
                };

                serverThread.Start();
                Thread.Sleep(100);

                // Send burst of packets with minimal delay
                long[] sendTimes = new long[BURST_SIZE];
                UnityEngine.Debug.Log($"[BURST] Sending {BURST_SIZE} packets with minimal delay...");

                for (int i = 0; i < BURST_SIZE; i++)
                {
                    byte[] packet = new byte[16];
                    BitConverter.GetBytes(i).CopyTo(packet, 0);
                    sendTimes[i] = Stopwatch.GetTimestamp();
                    BitConverter.GetBytes(sendTimes[i]).CopyTo(packet, 8);
                    clientSocket.Send(packet, packet.Length, serverEndpoint);
                    // Tiny delay to prevent OS from coalescing
                    Thread.SpinWait(1000);
                }

                UnityEngine.Debug.Log($"[BURST] All packets sent. Waiting for responses...");

                // Receive responses
                for (int i = 0; i < BURST_SIZE; i++)
                {
                    try
                    {
                        IPEndPoint responseEndpoint = null;
                        byte[] response = clientSocket.Receive(ref responseEndpoint);
                        long recvTime = Stopwatch.GetTimestamp();

                        int seq = BitConverter.ToInt32(response, 0);
                        long origSendTime = BitConverter.ToInt64(response, 8);
                        long serverTime = BitConverter.ToInt64(response, 16);

                        double rttMs = (recvTime - origSendTime) * 1000.0 / Stopwatch.Frequency;
                        results.Add((seq, rttMs, origSendTime, recvTime));

                        UnityEngine.Debug.Log($"[BURST] Packet {seq}: RTT = {rttMs:F2}ms");
                    }
                    catch (SocketException ex)
                    {
                        errors.Add($"Receive {i}: {ex.Message}");
                    }
                }

                serverDone.Wait(5000);
            }

            // Analyze burst timing
            if (results.Count > 0)
            {
                results.Sort((a, b) => a.seq.CompareTo(b.seq));

                double minRtt = double.MaxValue, maxRtt = 0, sumRtt = 0;
                foreach (var r in results)
                {
                    if (r.rttMs < minRtt) minRtt = r.rttMs;
                    if (r.rttMs > maxRtt) maxRtt = r.rttMs;
                    sumRtt += r.rttMs;
                }

                UnityEngine.Debug.Log($"\n========== BURST RTT RESULTS ==========");
                UnityEngine.Debug.Log($"Sent: {BURST_SIZE}, Received: {results.Count}");
                UnityEngine.Debug.Log($"Min RTT: {minRtt:F2}ms");
                UnityEngine.Debug.Log($"Max RTT: {maxRtt:F2}ms");
                UnityEngine.Debug.Log($"Avg RTT: {sumRtt/results.Count:F2}ms");
                UnityEngine.Debug.Log($"Spread (max-min): {maxRtt-minRtt:F2}ms");
                UnityEngine.Debug.Log($"========================================\n");

                // If packets sent together have wildly different RTTs, Clumsy is batching
                double spread = maxRtt - minRtt;
                if (spread > 200)
                {
                    UnityEngine.Debug.LogWarning($"[BURST] High RTT spread ({spread:F0}ms) suggests Clumsy may be batching/reordering packets!");
                }
            }
        }

        [Test]
        [Category("Network")]
        [Explicit("Run manually with/without Clumsy to compare - simulates GONet threading model")]
        public void Measure_UDP_RTT_With_Queue_Delay()
        {
            // This test simulates GONet's threading model:
            // - Main thread captures timestamp and enqueues
            // - Send thread dequeues and transmits
            // - Network thread receives and captures timestamp
            // This reveals if queuing adds significant delay under load

            var results = new List<(int seq, double totalRttMs, double queueDelayMs, double networkRttMs)>();
            var errors = new List<string>();
            var sendQueue = new System.Collections.Concurrent.ConcurrentQueue<(int seq, long t0, byte[] data)>();
            var stopSending = new ManualResetEventSlim(false);

            using (var serverSocket = new UdpClient(TEST_PORT + 3))
            using (var clientSocket = new UdpClient())
            {
                serverSocket.Client.ReceiveTimeout = 10000;
                clientSocket.Client.ReceiveTimeout = 10000;

                var serverEndpoint = new IPEndPoint(IPAddress.Loopback, TEST_PORT + 3);
                IPEndPoint remoteEndpoint = null;

                // Server echoes packets immediately
                var serverThread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < NUM_PACKETS && !stopSending.IsSet; i++)
                        {
                            byte[] received = serverSocket.Receive(ref remoteEndpoint);
                            serverSocket.Send(received, received.Length, remoteEndpoint);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors) { errors.Add($"Server: {ex.Message}"); }
                    }
                }) { IsBackground = true };

                // Send thread - simulates GONet's separate send thread
                var sendThread = new Thread(() =>
                {
                    while (!stopSending.IsSet)
                    {
                        if (sendQueue.TryDequeue(out var item))
                        {
                            // Record when we actually send (for measuring queue delay)
                            long actualSendTime = Stopwatch.GetTimestamp();

                            // Embed actual send time in packet for queue delay calculation
                            byte[] packet = new byte[32];
                            BitConverter.GetBytes(item.seq).CopyTo(packet, 0);
                            BitConverter.GetBytes(item.t0).CopyTo(packet, 4);
                            BitConverter.GetBytes(actualSendTime).CopyTo(packet, 12);
                            Array.Copy(item.data, 0, packet, 20, 12);

                            clientSocket.Send(packet, packet.Length, serverEndpoint);
                        }
                        else
                        {
                            Thread.Sleep(1); // Don't spin
                        }
                    }
                }) { IsBackground = true };

                serverThread.Start();
                sendThread.Start();
                Thread.Sleep(100);

                UnityEngine.Debug.Log($"[QUEUE-RTT] Testing GONet-style queue delay...");

                // Main thread enqueues packets (simulates GONet main thread)
                for (int i = 0; i < NUM_PACKETS; i++)
                {
                    long t0 = Stopwatch.GetTimestamp(); // Capture t0 like GONet does
                    byte[] data = new byte[12];
                    BitConverter.GetBytes(i).CopyTo(data, 0);

                    sendQueue.Enqueue((i, t0, data));

                    // Simulate GONet's ~200ms gap-closing interval
                    Thread.Sleep(200);
                }

                // Receive responses
                for (int i = 0; i < NUM_PACKETS; i++)
                {
                    try
                    {
                        IPEndPoint responseEndpoint = null;
                        byte[] response = clientSocket.Receive(ref responseEndpoint);
                        long t3 = Stopwatch.GetTimestamp(); // Capture t3 like GONet does

                        int seq = BitConverter.ToInt32(response, 0);
                        long t0 = BitConverter.ToInt64(response, 4);
                        long actualSendTime = BitConverter.ToInt64(response, 12);

                        double totalRttMs = (t3 - t0) * 1000.0 / Stopwatch.Frequency;
                        double queueDelayMs = (actualSendTime - t0) * 1000.0 / Stopwatch.Frequency;
                        double networkRttMs = (t3 - actualSendTime) * 1000.0 / Stopwatch.Frequency;

                        results.Add((seq, totalRttMs, queueDelayMs, networkRttMs));

                        UnityEngine.Debug.Log($"[QUEUE-RTT] Packet {seq}: Total={totalRttMs:F2}ms, QueueDelay={queueDelayMs:F2}ms, NetworkRTT={networkRttMs:F2}ms");
                    }
                    catch (SocketException ex)
                    {
                        errors.Add($"Receive {i}: {ex.Message}");
                    }
                }

                stopSending.Set();
                sendThread.Join(1000);
                serverThread.Join(1000);
            }

            if (results.Count > 0)
            {
                double avgTotal = 0, avgQueue = 0, avgNetwork = 0;
                foreach (var r in results)
                {
                    avgTotal += r.totalRttMs;
                    avgQueue += r.queueDelayMs;
                    avgNetwork += r.networkRttMs;
                }
                avgTotal /= results.Count;
                avgQueue /= results.Count;
                avgNetwork /= results.Count;

                UnityEngine.Debug.Log($"\n========== QUEUE DELAY RTT RESULTS ==========");
                UnityEngine.Debug.Log($"Packets: {results.Count}/{NUM_PACKETS}");
                UnityEngine.Debug.Log($"Avg Total RTT: {avgTotal:F2}ms");
                UnityEngine.Debug.Log($"Avg Queue Delay: {avgQueue:F2}ms");
                UnityEngine.Debug.Log($"Avg Network RTT: {avgNetwork:F2}ms");
                UnityEngine.Debug.Log($"==============================================\n");

                // If queue delay is significant, that explains the RTT inflation
                if (avgQueue > 50)
                {
                    UnityEngine.Debug.LogWarning($"[QUEUE-RTT] ⚠️ High queue delay ({avgQueue:F0}ms) may explain GONet RTT inflation!");
                }
            }
        }

        /// <summary>
        /// Tests if spacing packets by 500ms avoids Clumsy's buffering behavior.
        /// If RTT is still high, the issue is Clumsy's internal buffering mechanism.
        /// If RTT drops to ~100ms, the issue is burst traffic triggering Clumsy's buffer.
        /// </summary>
        [Test]
        [Category("Network")]
        [Explicit("Run manually - tests if wider spacing avoids Clumsy buffering")]
        public void Measure_UDP_RTT_Widely_Spaced()
        {
            var results = new List<(int seq, double rttMs)>();
            var errors = new List<string>();
            const int WIDE_PACKET_COUNT = 10;
            const int WIDE_SPACING_MS = 500; // Half second between packets

            using (var serverSocket = new UdpClient(TEST_PORT + 4))
            using (var clientSocket = new UdpClient())
            {
                serverSocket.Client.ReceiveTimeout = 5000;
                clientSocket.Client.ReceiveTimeout = 5000;

                var serverEndpoint = new IPEndPoint(IPAddress.Loopback, TEST_PORT + 4);
                IPEndPoint remoteEndpoint = null;

                var serverThread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < WIDE_PACKET_COUNT; i++)
                        {
                            byte[] received = serverSocket.Receive(ref remoteEndpoint);
                            serverSocket.Send(received, received.Length, remoteEndpoint);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors) { errors.Add($"Server: {ex.Message}"); }
                    }
                }) { IsBackground = true };

                serverThread.Start();
                Thread.Sleep(100);

                UnityEngine.Debug.Log($"[WIDE-SPACED] Testing with {WIDE_SPACING_MS}ms between packets...");

                var stopwatch = new Stopwatch();

                for (int i = 0; i < WIDE_PACKET_COUNT; i++)
                {
                    try
                    {
                        byte[] packet = new byte[16];
                        BitConverter.GetBytes(i).CopyTo(packet, 0);
                        BitConverter.GetBytes(Stopwatch.GetTimestamp()).CopyTo(packet, 4);

                        stopwatch.Restart();
                        clientSocket.Send(packet, packet.Length, serverEndpoint);

                        IPEndPoint responseEndpoint = null;
                        byte[] response = clientSocket.Receive(ref responseEndpoint);
                        stopwatch.Stop();

                        double rttMs = stopwatch.Elapsed.TotalMilliseconds;
                        int seq = BitConverter.ToInt32(response, 0);
                        results.Add((seq, rttMs));

                        UnityEngine.Debug.Log($"[WIDE-SPACED] Packet {seq}: RTT = {rttMs:F2}ms");

                        // Wide spacing to let Clumsy's buffer flush
                        Thread.Sleep(WIDE_SPACING_MS);
                    }
                    catch (SocketException ex)
                    {
                        errors.Add($"Packet {i}: {ex.Message}");
                        UnityEngine.Debug.LogWarning($"[WIDE-SPACED] Packet {i} FAILED: {ex.Message}");
                    }
                }

                serverThread.Join(2000);
            }

            if (results.Count > 0)
            {
                double min = double.MaxValue, max = 0, sum = 0;
                foreach (var r in results)
                {
                    if (r.rttMs < min) min = r.rttMs;
                    if (r.rttMs > max) max = r.rttMs;
                    sum += r.rttMs;
                }
                double avg = sum / results.Count;

                UnityEngine.Debug.Log($"\n========== WIDELY SPACED RTT RESULTS ==========");
                UnityEngine.Debug.Log($"Packets: {results.Count}/{WIDE_PACKET_COUNT}");
                UnityEngine.Debug.Log($"Spacing: {WIDE_SPACING_MS}ms between packets");
                UnityEngine.Debug.Log($"Min RTT: {min:F2}ms");
                UnityEngine.Debug.Log($"Max RTT: {max:F2}ms");
                UnityEngine.Debug.Log($"Avg RTT: {avg:F2}ms");
                UnityEngine.Debug.Log($"===============================================\n");

                // With proper spacing, we should see ~100ms RTT with 50ms Clumsy each way
                if (avg < 200)
                {
                    UnityEngine.Debug.Log($"[WIDE-SPACED] ✓ RTT is reasonable - Clumsy works correctly with spaced packets");
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"[WIDE-SPACED] ⚠️ RTT still high ({avg:F0}ms) - Clumsy buffers even spaced packets");
                }
            }
        }

        [Test]
        [Category("Network")]
        [Explicit("Run manually with/without Clumsy to compare")]
        public void Measure_TCP_Loopback_RTT()
        {
            // Compare TCP (reliable) vs UDP behavior
            var results = new List<double>();

            var listener = new TcpListener(IPAddress.Loopback, TEST_PORT + 2);
            listener.Start();

            try
            {
                var serverTask = Task.Run(() =>
                {
                    using (var serverClient = listener.AcceptTcpClient())
                    using (var stream = serverClient.GetStream())
                    {
                        byte[] buffer = new byte[16];
                        for (int i = 0; i < NUM_PACKETS; i++)
                        {
                            int bytesRead = stream.Read(buffer, 0, buffer.Length);
                            if (bytesRead > 0)
                            {
                                stream.Write(buffer, 0, bytesRead);
                            }
                        }
                    }
                });

                Thread.Sleep(100);

                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, TEST_PORT + 2);
                    using (var stream = client.GetStream())
                    {
                        var stopwatch = new Stopwatch();
                        byte[] packet = new byte[16];
                        byte[] response = new byte[16];

                        for (int i = 0; i < NUM_PACKETS; i++)
                        {
                            BitConverter.GetBytes(i).CopyTo(packet, 0);

                            stopwatch.Restart();
                            stream.Write(packet, 0, packet.Length);
                            stream.Flush();
                            int bytesRead = stream.Read(response, 0, response.Length);
                            stopwatch.Stop();

                            if (bytesRead > 0)
                            {
                                double rttMs = stopwatch.Elapsed.TotalMilliseconds;
                                results.Add(rttMs);
                                UnityEngine.Debug.Log($"[TCP-RTT] Packet {i}: RTT = {rttMs:F2}ms");
                            }

                            Thread.Sleep(PACKET_INTERVAL_MS);
                        }
                    }
                }

                serverTask.Wait(5000);
            }
            finally
            {
                listener.Stop();
            }

            if (results.Count > 0)
            {
                double min = double.MaxValue, max = 0, sum = 0;
                foreach (var rtt in results)
                {
                    if (rtt < min) min = rtt;
                    if (rtt > max) max = rtt;
                    sum += rtt;
                }

                UnityEngine.Debug.Log($"\n========== TCP LOOPBACK RTT RESULTS ==========");
                UnityEngine.Debug.Log($"Packets: {results.Count}/{NUM_PACKETS}");
                UnityEngine.Debug.Log($"Min RTT: {min:F2}ms");
                UnityEngine.Debug.Log($"Max RTT: {max:F2}ms");
                UnityEngine.Debug.Log($"Avg RTT: {sum/results.Count:F2}ms");
                UnityEngine.Debug.Log($"==============================================\n");
            }
        }
    }
}
