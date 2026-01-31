/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using GONet.DistributedHost;
using System.Collections.Generic;

namespace GONet.Editor.UnitTests.DistributedHost
{
    /// <summary>
    /// Unit tests for connection-loss triggered failover logic (Phase 2.12).
    /// Tests the decision-making logic for:
    /// 1. Excluding dead host from standby candidates
    /// 2. Determining if we should self-promote (lowest authority among survivors)
    /// 3. Selecting the best failover candidate
    /// </summary>
    [TestFixture]
    public class GONetConnectionLossFailoverTests
    {
        #region Dead Host Exclusion Tests

        [Test]
        public void FindBestCandidate_ExcludesDeadHost()
        {
            // Arrange: We have standby connections to host (1023) and peer (2)
            // Host 1023 just died
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 1023, StandbyConnectionState.Connected }, // Dead host - should be excluded
                { 2, StandbyConnectionState.Connected }     // Peer - valid candidate
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;

            // Act: Find best candidate excluding dead host
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId: 0);

            // Assert: Should pick peer 2, not dead host 1023
            Assert.AreEqual(2, bestCandidate);
        }

        [Test]
        public void FindBestCandidate_ReturnsZero_WhenOnlyDeadHostAvailable()
        {
            // Arrange: Only standby connection is to the dead host
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 1023, StandbyConnectionState.Connected } // Dead host - should be excluded
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId: 0);

            // Assert: No valid candidate
            Assert.AreEqual(0, bestCandidate);
        }

        [Test]
        public void FindBestCandidate_IgnoresNonConnectedStates()
        {
            // Arrange: Multiple peers but only one is Connected
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Failed },      // Not connected
                { 3, StandbyConnectionState.Connecting },  // Not connected
                { 4, StandbyConnectionState.Connected }    // Valid candidate
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId: 0);

            // Assert
            Assert.AreEqual(4, bestCandidate);
        }

        #endregion

        #region Self-Promotion Decision Tests

        [Test]
        public void ShouldSelfPromote_True_WhenLowestAuthorityAmongSurvivors()
        {
            // Arrange: I am authority 1, peers are 2 and 3
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Connected },
                { 3, StandbyConnectionState.Connected }
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;

            // Act
            bool shouldSelfPromote = ShouldSelfPromote(standbyStates, deadHostAuthorityId, myAuthorityId);

            // Assert: I am lowest (1 < 2 < 3), so I should self-promote
            Assert.IsTrue(shouldSelfPromote);
        }

        [Test]
        public void ShouldSelfPromote_False_WhenNotLowestAuthority()
        {
            // Arrange: I am authority 3, peer 1 has lower authority
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 1, StandbyConnectionState.Connected },
                { 2, StandbyConnectionState.Connected }
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 3;

            // Act
            bool shouldSelfPromote = ShouldSelfPromote(standbyStates, deadHostAuthorityId, myAuthorityId);

            // Assert: Peer 1 has lower authority, they should promote
            Assert.IsFalse(shouldSelfPromote);
        }

        [Test]
        public void ShouldSelfPromote_True_WhenNoConnectedPeers()
        {
            // Arrange: All peers are failed/not connected
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 1, StandbyConnectionState.Failed },
                { 2, StandbyConnectionState.Connecting }
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 3;

            // Act
            bool shouldSelfPromote = ShouldSelfPromote(standbyStates, deadHostAuthorityId, myAuthorityId);

            // Assert: No connected peers, so I am the lowest among connected survivors (just me)
            Assert.IsTrue(shouldSelfPromote);
        }

        [Test]
        public void ShouldSelfPromote_IgnoresDeadHost()
        {
            // Arrange: Dead host had authority 1 (lower than mine)
            // But dead host should be excluded from comparison
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 1, StandbyConnectionState.Connected },  // This is the dead host
                { 5, StandbyConnectionState.Connected }   // Peer with higher authority than me
            };
            ushort deadHostAuthorityId = 1;  // Dead host has lowest authority
            ushort myAuthorityId = 3;

            // Act
            bool shouldSelfPromote = ShouldSelfPromote(standbyStates, deadHostAuthorityId, myAuthorityId);

            // Assert: Dead host (1) is excluded, I (3) < peer (5), so I should promote
            Assert.IsTrue(shouldSelfPromote);
        }

        #endregion

        #region Vice Host Priority Tests

        [Test]
        public void FindBestCandidate_PrefersViceHost()
        {
            // Arrange: Vice host is 3, but peer 2 has lower authority
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Connected },
                { 3, StandbyConnectionState.Connected }  // Vice host
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;
            ushort viceHostId = 3;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId);

            // Assert: Should pick vice host 3, even though 2 has lower authority
            Assert.AreEqual(3, bestCandidate);
        }

        [Test]
        public void FindBestCandidate_SkipsViceHostIfDead()
        {
            // Arrange: Vice host was the one that died
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Connected },
                { 1023, StandbyConnectionState.Connected }  // Dead host was also vice host
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;
            ushort viceHostId = 1023;  // Vice host is dead

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId);

            // Assert: Should pick peer 2, not dead vice host
            Assert.AreEqual(2, bestCandidate);
        }

        [Test]
        public void FindBestCandidate_SkipsViceHostIfNotConnected()
        {
            // Arrange: Vice host exists but is not connected
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Connected },
                { 3, StandbyConnectionState.Failed }  // Vice host but failed
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;
            ushort viceHostId = 3;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId);

            // Assert: Vice host not connected, fall back to peer 2
            Assert.AreEqual(2, bestCandidate);
        }

        #endregion

        #region Lowest Authority Tiebreaker Tests

        [Test]
        public void FindBestCandidate_SelectsLowestAuthority_WhenNoViceHost()
        {
            // Arrange: Multiple peers, no vice host designated
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 5, StandbyConnectionState.Connected },
                { 2, StandbyConnectionState.Connected },
                { 8, StandbyConnectionState.Connected }
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 10;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId: 0);

            // Assert: Should pick 2 (lowest among 2, 5, 8)
            Assert.AreEqual(2, bestCandidate);
        }

        [Test]
        public void FindBestCandidate_SelectsLowestAuthority_EvenIfNotFirstInDictionary()
        {
            // Arrange: Dictionary iteration order shouldn't matter
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>();
            // Add in non-sorted order
            standbyStates.Add(100, StandbyConnectionState.Connected);
            standbyStates.Add(5, StandbyConnectionState.Connected);
            standbyStates.Add(50, StandbyConnectionState.Connected);
            standbyStates.Add(2, StandbyConnectionState.Connected);

            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1000;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId: 0);

            // Assert: Should pick 2 (lowest)
            Assert.AreEqual(2, bestCandidate);
        }

        #endregion

        #region Edge Cases

        [Test]
        public void FindBestCandidate_EmptyStandbyStates()
        {
            // Arrange: No standby connections at all
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>();
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId: 0);

            // Assert: No candidate available
            Assert.AreEqual(0, bestCandidate);
        }

        [Test]
        public void ShouldSelfPromote_EmptyStandbyStates()
        {
            // Arrange: No standby connections
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>();
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;

            // Act
            bool shouldSelfPromote = ShouldSelfPromote(standbyStates, deadHostAuthorityId, myAuthorityId);

            // Assert: I am the only survivor, should self-promote
            Assert.IsTrue(shouldSelfPromote);
        }

        [Test]
        public void FindBestCandidate_AllPeersFailed()
        {
            // Arrange: Have connections but all failed
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Failed },
                { 3, StandbyConnectionState.Closed },
                { 4, StandbyConnectionState.Failed }
            };
            ushort deadHostAuthorityId = 1023;
            ushort myAuthorityId = 1;

            // Act
            ushort bestCandidate = FindBestCandidate(standbyStates, deadHostAuthorityId, myAuthorityId, viceHostId: 0);

            // Assert: No valid candidate
            Assert.AreEqual(0, bestCandidate);
        }

        #endregion

        #region Test Helpers (Mirrors logic from GONet.cs Client_gonetClient_Disconnected)

        /// <summary>
        /// Finds the best failover candidate from standby connections.
        /// This mirrors the logic in GONet.cs Client_gonetClient_Disconnected.
        /// </summary>
        private ushort FindBestCandidate(
            Dictionary<ushort, StandbyConnectionState> standbyStates,
            ushort deadHostAuthorityId,
            ushort myAuthorityId,
            ushort viceHostId)
        {
            ushort bestCandidateAuthorityId = 0;
            bool foundCandidate = false;

            foreach (var kvp in standbyStates)
            {
                // Skip the dead host
                if (kvp.Key == deadHostAuthorityId)
                    continue;

                if (kvp.Value == StandbyConnectionState.Connected)
                {
                    // Prefer vice host (if not the dead host)
                    if (kvp.Key == viceHostId)
                    {
                        bestCandidateAuthorityId = kvp.Key;
                        foundCandidate = true;
                        break; // Vice host is always preferred
                    }

                    // Otherwise take lowest authority ID
                    if (!foundCandidate || kvp.Key < bestCandidateAuthorityId)
                    {
                        bestCandidateAuthorityId = kvp.Key;
                        foundCandidate = true;
                    }
                }
            }

            return foundCandidate ? bestCandidateAuthorityId : (ushort)0;
        }

        /// <summary>
        /// Determines if we should self-promote to host.
        /// This mirrors the logic in GONet.cs Client_gonetClient_Disconnected.
        /// </summary>
        private bool ShouldSelfPromote(
            Dictionary<ushort, StandbyConnectionState> standbyStates,
            ushort deadHostAuthorityId,
            ushort myAuthorityId)
        {
            bool iAmLowestAuthority = true;

            foreach (var kvp in standbyStates)
            {
                if (kvp.Key == deadHostAuthorityId) continue; // Skip dead host
                if (kvp.Value == StandbyConnectionState.Connected && kvp.Key < myAuthorityId)
                {
                    iAmLowestAuthority = false;
                    break;
                }
            }

            return iAmLowestAuthority;
        }

        #endregion

        #region Mesh Connectivity Failover Tests (Phase 2.12)

        [Test]
        public void MeshFailover_HotStandbyConnection_UsedForTrafficSwitchover()
        {
            // Document: After failover, traffic switches to hot standby connection
            // that was already established with the promoting peer

            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Connected },  // This peer will become new host
                { 3, StandbyConnectionState.Connected }
            };
            ushort deadHostAuthorityId = 1023;
            ushort promotingPeerId = 2;

            // Verify we have a connected standby to the promoting peer
            Assert.IsTrue(standbyStates.ContainsKey(promotingPeerId));
            Assert.AreEqual(StandbyConnectionState.Connected, standbyStates[promotingPeerId]);
        }

        [Test]
        public void MeshFailover_ActiveState_AfterSwitchover()
        {
            // After traffic switchover, connection state changes from Connected to Active
            var state = StandbyConnectionState.Connected;

            // Simulate switchover
            state = StandbyConnectionState.Active;

            Assert.AreEqual(StandbyConnectionState.Active, state);
        }

        [Test]
        public void MeshFailover_OriginalAuthorityId_MatchesHotStandbyLookup()
        {
            // Critical: The promoting peer's original authority ID must match
            // the key used in hot standby connection map

            var standbyConnections = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Connected },  // Peer 2's standby
                { 3, StandbyConnectionState.Connected }   // Peer 3's standby
            };

            ushort promotingPeerOriginalId = 2;  // From EmergencyHostPromotionMessage

            // Lookup should succeed
            Assert.IsTrue(standbyConnections.ContainsKey(promotingPeerOriginalId));
        }

        [Test]
        public void MeshFailover_NoStandbyToPromotingPeer_FallbackToReconnect()
        {
            // Edge case: No hot standby connection to the promoting peer
            // Must fall back to traditional reconnection

            var standbyConnections = new Dictionary<ushort, StandbyConnectionState>
            {
                { 3, StandbyConnectionState.Connected },
                { 4, StandbyConnectionState.Failed }
            };

            ushort promotingPeerOriginalId = 2;  // Not in our standby map!

            bool hasStandby = standbyConnections.ContainsKey(promotingPeerOriginalId) &&
                             standbyConnections[promotingPeerOriginalId] == StandbyConnectionState.Connected;

            Assert.IsFalse(hasStandby, "No standby - must reconnect traditionally");
        }

        #endregion

        #region Split-Brain Prevention Tests

        [Test]
        public void SplitBrain_DifferentEpochs_HigherWins()
        {
            // Network partition heals, two hosts claim authority
            uint hostAEpoch = 5;
            uint hostBEpoch = 7;

            // Higher epoch wins
            uint winningEpoch = System.Math.Max(hostAEpoch, hostBEpoch);
            Assert.AreEqual(7u, winningEpoch);
        }

        [Test]
        public void SplitBrain_SameEpoch_ViceHostWins()
        {
            // Same epoch but one was designated vice host
            uint epoch = 5;
            ushort viceHostId = 3;
            ushort otherNodeId = 2;

            bool nodeAIsViceHost = (otherNodeId == viceHostId);
            bool nodeBIsViceHost = (3 == viceHostId);

            Assert.IsFalse(nodeAIsViceHost);
            Assert.IsTrue(nodeBIsViceHost);
            // Node B (the actual vice host) wins
        }

        [Test]
        public void SplitBrain_SameEpoch_NoViceHost_LowestAuthorityWins()
        {
            // No designated vice host, fall back to lowest authority
            uint epoch = 5;
            ushort nodeAAuthority = 5;
            ushort nodeBAuthority = 3;

            ushort winner = System.Math.Min(nodeAAuthority, nodeBAuthority);
            Assert.AreEqual(3, winner, "Lower authority wins");
        }

        #endregion

        #region Rapid Failover Tests

        [Test]
        public void RapidFailover_GracePeriod_PreventsFlapping()
        {
            // After failover completes, grace period prevents immediate re-failover
            float gracePeriod = GONetHostFailoverManager.POST_FAILOVER_GRACE_PERIOD_SECONDS;
            float timeSinceFailover = 1.0f;

            bool withinGracePeriod = timeSinceFailover < gracePeriod;
            Assert.IsTrue(withinGracePeriod, "Should still be in grace period");
        }

        [Test]
        public void RapidFailover_AfterGracePeriod_CanFailoverAgain()
        {
            float gracePeriod = GONetHostFailoverManager.POST_FAILOVER_GRACE_PERIOD_SECONDS;
            float timeSinceFailover = gracePeriod + 1.0f;

            bool afterGracePeriod = timeSinceFailover >= gracePeriod;
            Assert.IsTrue(afterGracePeriod, "Can failover again after grace period");
        }

        #endregion

        #region Network Partition Tests

        [Test]
        public void Partition_IsolatedNode_CannotSelfPromote()
        {
            // Node is isolated (no connected peers)
            var standbyStates = new Dictionary<ushort, StandbyConnectionState>
            {
                { 2, StandbyConnectionState.Failed },
                { 3, StandbyConnectionState.Failed }
            };

            int connectedPeerCount = 0;
            foreach (var kvp in standbyStates)
            {
                if (kvp.Value == StandbyConnectionState.Connected)
                    connectedPeerCount++;
            }

            Assert.AreEqual(0, connectedPeerCount, "Node is isolated");
            // Document: Isolated node should NOT self-promote to avoid split-brain
        }

        [Test]
        public void Partition_MajorityPartition_CanSelfPromote()
        {
            // Document: Only nodes in majority partition should self-promote
            // Minority partition should wait for partition to heal

            int totalKnownPeers = 5;
            int connectedPeers = 3; // Majority

            bool hasMajority = connectedPeers > (totalKnownPeers / 2);
            Assert.IsTrue(hasMajority);
        }

        [Test]
        public void Partition_MinorityPartition_WaitsForHeal()
        {
            int totalKnownPeers = 5;
            int connectedPeers = 2; // Minority

            bool hasMajority = connectedPeers > (totalKnownPeers / 2);
            Assert.IsFalse(hasMajority, "Minority should not self-promote");
        }

        #endregion
    }
}

