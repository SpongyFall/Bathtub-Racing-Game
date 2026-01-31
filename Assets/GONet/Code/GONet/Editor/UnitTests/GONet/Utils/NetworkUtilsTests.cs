// Assets/Tests/EditMode/NetworkUtilsTests.cs
// Assembly Definition recommended: Tests.EditMode (reference GONet.Runtime)

using NUnit.Framework;
using GONet;                        // adjust if your namespace differs
using System.Net;
using GONet.Utils;
using System.Net.Sockets;

public class NetworkUtilsTests
{
    const ushort TestPort = 43210;

    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    [TestCase("ipv4.google.com")]   // v4‑only DNS
    [TestCase("ipv6.google.com")]   // v6‑only DNS
    public void Parses_Hostname_And_Returns_Endpoint(string host)
    {
        var ep = NetworkUtils.GetIPEndPointFromHostName(host, TestPort);

        Assert.AreEqual(TestPort, ep.Port);

        // Verify address family matches expectation
        if (host.Contains(":") || host.StartsWith("ipv6"))
            Assert.AreEqual(AddressFamily.InterNetworkV6, ep.AddressFamily);
        else
            Assert.AreEqual(AddressFamily.InterNetwork, ep.AddressFamily);
    }

    #region IsLoopbackAddress Tests

    [Test]
    public void IsLoopbackAddress_IPv4Loopback_ReturnsTrue()
    {
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("127.0.0.1"));
    }

    [Test]
    public void IsLoopbackAddress_IPv6Loopback_ReturnsTrue()
    {
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("::1"));
    }

    [Test]
    public void IsLoopbackAddress_Localhost_ReturnsTrue()
    {
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("localhost"));
    }

    [Test]
    public void IsLoopbackAddress_LocalhostMixedCase_ReturnsTrue()
    {
        // Case-insensitive check
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("LocalHost"));
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("LOCALHOST"));
    }

    [Test]
    public void IsLoopbackAddress_LoopbackRange_ReturnsTrue()
    {
        // RFC 3330: Entire 127.0.0.0/8 block is reserved for loopback
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("127.0.0.2"));
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("127.1.2.3"));
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("127.255.255.255"));
    }

    [Test]
    public void IsLoopbackAddress_WithWhitespace_ReturnsTrue()
    {
        // Should trim whitespace
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress(" 127.0.0.1 "));
        Assert.IsTrue(NetworkUtils.IsLoopbackAddress("\t127.0.0.1\n"));
    }

    [Test]
    public void IsLoopbackAddress_ExternalIP_ReturnsFalse()
    {
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress("192.168.1.1"));
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress("10.0.0.1"));
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress("8.8.8.8"));
    }

    [Test]
    public void IsLoopbackAddress_ExternalHostname_ReturnsFalse()
    {
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress("google.com"));
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress("example.org"));
    }

    [Test]
    public void IsLoopbackAddress_NullOrEmpty_ReturnsFalse()
    {
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress(null));
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress(""));
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress("   "));
    }

    [Test]
    public void IsLoopbackAddress_Similar128_ReturnsFalse()
    {
        // 128.x.x.x is NOT loopback (only 127.x.x.x is)
        Assert.IsFalse(NetworkUtils.IsLoopbackAddress("128.0.0.1"));
    }

    #endregion

    #region IsIPAddressOnLocalMachine Tests (Loopback cases only - avoids DNS lookups)

    [Test]
    public void IsIPAddressOnLocalMachine_IPv4Loopback_ReturnsTrue()
    {
        Assert.IsTrue(NetworkUtils.IsIPAddressOnLocalMachine("127.0.0.1"));
    }

    [Test]
    public void IsIPAddressOnLocalMachine_IPv6Loopback_ReturnsTrue()
    {
        Assert.IsTrue(NetworkUtils.IsIPAddressOnLocalMachine("::1"));
    }

    [Test]
    public void IsIPAddressOnLocalMachine_Localhost_ReturnsTrue()
    {
        Assert.IsTrue(NetworkUtils.IsIPAddressOnLocalMachine("localhost"));
    }

    [Test]
    public void IsIPAddressOnLocalMachine_AnyAddress_ReturnsTrue()
    {
        // IPv6 any address (used for server binding)
        Assert.IsTrue(NetworkUtils.IsIPAddressOnLocalMachine("[::]"));
    }

    [Test]
    public void IsIPAddressOnLocalMachine_LoopbackRange_ReturnsTrue()
    {
        // Entire 127.x.x.x range should be recognized as local
        Assert.IsTrue(NetworkUtils.IsIPAddressOnLocalMachine("127.0.0.2"));
        Assert.IsTrue(NetworkUtils.IsIPAddressOnLocalMachine("127.1.2.3"));
    }

    #endregion
}
