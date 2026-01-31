using System;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using NetcodeIO.NET;

namespace GONet.Tests.Netcode_IO
{
    [TestFixture]
    public class EncryptionManagerCompactionTests
    {
        private static Type EncryptionManagerType =>
            typeof(Server).Assembly.GetType("NetcodeIO.NET.EncryptionManager", throwOnError: true);

        [Test]
        public void RemoveAllEncryptionMappings_RemovesAllMatches_AndCompactsDeterministically()
        {
            object manager = Activator.CreateInstance(EncryptionManagerType, new object[] { 2 });

            MethodInfo add = EncryptionManagerType.GetMethod("AddEncryptionMapping", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo remove = EncryptionManagerType.GetMethod("RemoveAllEncryptionMappings", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo getIndex = EncryptionManagerType.GetMethod("GetEncryptionMappingIndexForTime", BindingFlags.Instance | BindingFlags.Public);

            FieldInfo usedCountField = EncryptionManagerType.GetField("encyrptionMappings_usedCount", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(add, Is.Not.Null);
            Assert.That(remove, Is.Not.Null);
            Assert.That(getIndex, Is.Not.Null);
            Assert.That(usedCountField, Is.Not.Null);

            double now = 100.0;
            byte[] sendKey1 = new byte[32];
            byte[] recvKey1 = new byte[32];
            byte[] sendKey2 = new byte[32];
            byte[] recvKey2 = new byte[32];

            // Create two mappings that should be treated as the same endpoint (v4 and v4-mapped-v6),
            // then add a third unrelated mapping and ensure compaction preserves it.
            var ep4 = new IPEndPoint(IPAddress.Loopback, 12345);
            var ep6Mapped = new IPEndPoint(ep4.Address.MapToIPv6(), ep4.Port);
            var other = new IPEndPoint(IPAddress.Loopback, 54321);

            // TimeoutAfterSeconds=0 prevents AddEncryptionMapping from reusing an existing mapping in the first pass,
            // allowing us to intentionally create duplicate entries for the same endpoint.
            bool added1 = (bool)add.Invoke(manager, new object[] { ep4, sendKey1, recvKey1, now, now + 100, 0, (uint)1 });
            bool added2 = (bool)add.Invoke(manager, new object[] { ep6Mapped, sendKey2, recvKey2, now, now + 100, 0, (uint)2 });
            bool added3 = (bool)add.Invoke(manager, new object[] { other, sendKey1, recvKey1, now, now + 100, 0, (uint)3 });

            Assert.That(added1, Is.True);
            Assert.That(added2, Is.True);
            Assert.That(added3, Is.True);

            int usedBefore = (int)usedCountField.GetValue(manager);
            Assert.That(usedBefore, Is.GreaterThanOrEqualTo(3));

            int removedCount = (int)remove.Invoke(manager, new object[] { ep4 });
            Assert.That(removedCount, Is.EqualTo(2));

            int usedAfter = (int)usedCountField.GetValue(manager);
            Assert.That(usedAfter, Is.EqualTo(1));

            int otherIndex = (int)getIndex.Invoke(manager, new object[] { other, now });
            Assert.That(otherIndex, Is.EqualTo(0));

            int removedIndex = (int)getIndex.Invoke(manager, new object[] { ep4, now });
            Assert.That(removedIndex, Is.EqualTo(-1));
        }
    }
}

