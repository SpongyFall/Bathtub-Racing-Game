using System;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using NetcodeIO.NET;
using NetcodeIO.NET.Utils.IO;

namespace GONet.Tests.Netcode_IO
{
    [TestFixture]
    public class ServerMissingEncryptionMappingDisconnectTests
    {
        private const ulong TEST_PROTOCOL_ID = 0x1122334455667788L;
        private const int TEST_SERVER_PORT = 40123;

        private static readonly byte[] PrivateKey = new byte[]
        {
            0x60, 0x6a, 0xbe, 0x6e, 0xc9, 0x19, 0x10, 0xea,
            0x9a, 0x65, 0x62, 0xf6, 0x6f, 0x2b, 0x30, 0xe4,
            0x43, 0x71, 0xd6, 0x2c, 0xd1, 0x99, 0x27, 0x26,
            0x6b, 0x3c, 0x60, 0xf4, 0xb7, 0x15, 0xab, 0xa1,
        };

        [Test]
        public void SendPayload_WhenEncryptionMappingMissing_DisconnectsClientOnNextTick()
        {
            var socketMgr = new NetworkSimulatorSocketManager();
            var serverEndpoint = new IPEndPoint(IPAddress.Loopback, TEST_SERVER_PORT);
            var serverSocket = socketMgr.CreateContext(serverEndpoint);
            serverSocket.Bind(serverEndpoint);

            // Use internal ctor + manual init (same approach as NetcodeIOIntegrationTests).
            Server server = new Server(serverSocket, 4, TEST_SERVER_PORT, TEST_PROTOCOL_ID, PrivateKey);

            typeof(Server).GetMethod("resetConnectTokenHistory", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(server, null);

            typeof(Server).GetField("isRunning", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(server, true);

            double time = 0.0;
            server.totalSeconds = time;

            // Insert a client into a server slot WITHOUT adding any encryption mapping for it.
            var remoteClient = new RemoteClient(server)
            {
                ClientID = 123,
                ClientIndex = 0,
                RemoteEndpoint = new IPEndPoint(IPAddress.Loopback, 50000),
            };
            remoteClient.Connected = true;
            remoteClient.Confirmed = true;

            var clientSlotsField = typeof(Server).GetField("clientSlots", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(clientSlotsField, Is.Not.Null);

            var slots = (RemoteClient[])clientSlotsField.GetValue(server);
            slots[0] = remoteClient;

            int disconnectCount = 0;
            server.OnClientDisconnected += _ => disconnectCount++;

            // Attempt to send - this should schedule a disconnect because cryptIdx == -1.
            server.SendPayload(remoteClient, new byte[] { 1, 2, 3 }, 3);

            Assert.That(remoteClient.disconnectRequested, Is.EqualTo(1));

            time += 0.01;
            server.Tick(time);

            Assert.That(disconnectCount, Is.EqualTo(1));
            Assert.That(slots[0], Is.Null);
            Assert.That(remoteClient.Connected, Is.False);
        }
    }
}

