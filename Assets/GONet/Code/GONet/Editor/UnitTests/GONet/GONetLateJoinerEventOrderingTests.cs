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

using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for OrderPersistentEventsForLateJoinerInit (January 2026).
    ///
    /// ROOT CAUSE: Late-joining clients received persistent events in arbitrary order, causing:
    /// - Spawn events processed before scene load (deferred indefinitely)
    /// - Reparent events processed before spawn (object not found)
    ///
    /// FIX: OrderPersistentEventsForLateJoinerInit ensures deterministic ordering:
    /// SceneLoad → Spawn → Reparent → Other (relative order preserved within groups)
    ///
    /// Test scenarios:
    /// 1. SceneLoad events ordered before spawn events
    /// 2. Spawn events ordered before reparent events
    /// 3. Relative order preserved within each event type
    /// 4. Empty/null input handling
    /// </summary>
    [TestFixture]
    [Category("LateJoiner")]
    [Category("EventOrdering")]
    public class GONetLateJoinerEventOrderingTests
    {
        private MethodInfo orderPersistentEventsMethod;

        [SetUp]
        public void Setup()
        {
            // Access the private method via reflection
            var gonetNetworkType = typeof(GONetMain).Assembly.GetType("GONet.GONetMain");
            orderPersistentEventsMethod = gonetNetworkType?.GetMethod(
                "OrderPersistentEventsForLateJoinerInit",
                BindingFlags.NonPublic | BindingFlags.Static);

            // If not found on GONetMain, check GONet.Network partial class area
            if (orderPersistentEventsMethod == null)
            {
                // Try to find via the partial class file's methods
                var allMethods = typeof(GONetMain).GetMethods(BindingFlags.NonPublic | BindingFlags.Static);
                foreach (var method in allMethods)
                {
                    if (method.Name == "OrderPersistentEventsForLateJoinerInit")
                    {
                        orderPersistentEventsMethod = method;
                        break;
                    }
                }
            }

            Assert.IsNotNull(orderPersistentEventsMethod,
                "OrderPersistentEventsForLateJoinerInit not found - verify late-joiner ordering fix is present");
        }

        [Test]
        public void OrderEvents_SceneLoadsBeforeSpawns()
        {
            // SCENARIO: Mixed SceneLoad and Spawn events arrive in wrong order
            // EXPECTED: SceneLoad events ordered before Spawn events
            // IMPACT: Prevents spawns being deferred indefinitely waiting for scene

            var inputEvents = new LinkedList<IPersistentEvent>();

            // Add in wrong order: Spawn first, then SceneLoad
            var spawn1 = CreateSpawnEvent(1001);
            var spawn2 = CreateSpawnEvent(1002);
            var sceneLoad = CreateSceneLoadEvent("TestScene");

            inputEvents.AddLast(spawn1);
            inputEvents.AddLast(spawn2);
            inputEvents.AddLast(sceneLoad);

            var orderedEvents = InvokeOrderEvents(inputEvents);
            var orderedList = new List<IPersistentEvent>(orderedEvents);

            Assert.AreEqual(3, orderedList.Count, "All events should be preserved");

            // SceneLoad should be first
            Assert.IsInstanceOf<SceneLoadEvent>(orderedList[0], "SceneLoad should come before spawns");
            Assert.IsInstanceOf<InstantiateGONetParticipantEvent>(orderedList[1], "Spawns should come after SceneLoad");
            Assert.IsInstanceOf<InstantiateGONetParticipantEvent>(orderedList[2], "Spawns should come after SceneLoad");
        }

        [Test]
        public void OrderEvents_SpawnsBeforeReparents()
        {
            // SCENARIO: Reparent events arrive before corresponding spawn events
            // EXPECTED: Spawn events ordered before Reparent events
            // IMPACT: Prevents "object not found" errors during reparent processing

            var inputEvents = new LinkedList<IPersistentEvent>();

            // Add in wrong order: Reparent first, then Spawn
            var reparent = CreateReparentEvent(2001, 3001);
            var spawn = CreateSpawnEvent(2001);

            inputEvents.AddLast(reparent);
            inputEvents.AddLast(spawn);

            var orderedEvents = InvokeOrderEvents(inputEvents);
            var orderedList = new List<IPersistentEvent>(orderedEvents);

            Assert.AreEqual(2, orderedList.Count, "All events should be preserved");

            // Spawn should be first
            Assert.IsInstanceOf<InstantiateGONetParticipantEvent>(orderedList[0], "Spawn should come before reparent");
            Assert.IsInstanceOf<ReparentGONetParticipantEvent>(orderedList[1], "Reparent should come after spawn");
        }

        [Test]
        public void OrderEvents_FullOrderingChain_SceneLoad_Spawn_Reparent_Other()
        {
            // SCENARIO: All event types mixed in wrong order
            // EXPECTED: Deterministic ordering: SceneLoad → Spawn → Reparent → Other

            var inputEvents = new LinkedList<IPersistentEvent>();

            // Add in completely wrong order
            var reparent = CreateReparentEvent(4001, 5001);
            var other = CreateOtherPersistentEvent();
            var spawn = CreateSpawnEvent(4001);
            var sceneLoad = CreateSceneLoadEvent("GameScene");

            inputEvents.AddLast(reparent);
            inputEvents.AddLast(other);
            inputEvents.AddLast(spawn);
            inputEvents.AddLast(sceneLoad);

            var orderedEvents = InvokeOrderEvents(inputEvents);
            var orderedList = new List<IPersistentEvent>(orderedEvents);

            Assert.AreEqual(4, orderedList.Count, "All events should be preserved");

            // Verify correct order
            Assert.IsInstanceOf<SceneLoadEvent>(orderedList[0], "SceneLoad should be first");
            Assert.IsInstanceOf<InstantiateGONetParticipantEvent>(orderedList[1], "Spawn should be second");
            Assert.IsInstanceOf<ReparentGONetParticipantEvent>(orderedList[2], "Reparent should be third");
            // Other events come last - type varies
            Assert.IsFalse(orderedList[3] is SceneLoadEvent || orderedList[3] is InstantiateGONetParticipantEvent || orderedList[3] is ReparentGONetParticipantEvent,
                "Other events should come last");
        }

        [Test]
        public void OrderEvents_PreservesRelativeOrderWithinGroups()
        {
            // SCENARIO: Multiple events of same type should maintain relative order
            // EXPECTED: Within each type group, original order is preserved

            var inputEvents = new LinkedList<IPersistentEvent>();

            // Add multiple spawns in specific order
            var spawn1 = CreateSpawnEvent(6001);
            var spawn2 = CreateSpawnEvent(6002);
            var spawn3 = CreateSpawnEvent(6003);

            inputEvents.AddLast(spawn1);
            inputEvents.AddLast(spawn2);
            inputEvents.AddLast(spawn3);

            var orderedEvents = InvokeOrderEvents(inputEvents);
            var orderedList = new List<IPersistentEvent>(orderedEvents);

            Assert.AreEqual(3, orderedList.Count, "All events should be preserved");

            // Verify relative order preserved
            Assert.AreEqual(6001, ((InstantiateGONetParticipantEvent)orderedList[0]).GONetId, "First spawn should remain first");
            Assert.AreEqual(6002, ((InstantiateGONetParticipantEvent)orderedList[1]).GONetId, "Second spawn should remain second");
            Assert.AreEqual(6003, ((InstantiateGONetParticipantEvent)orderedList[2]).GONetId, "Third spawn should remain third");
        }

        [Test]
        public void OrderEvents_EmptyInput_ReturnsEmptyList()
        {
            // SCENARIO: Empty input collection
            // EXPECTED: Empty output collection, no exceptions

            var inputEvents = new LinkedList<IPersistentEvent>();
            var orderedEvents = InvokeOrderEvents(inputEvents);

            Assert.IsNotNull(orderedEvents, "Should return non-null collection");
            Assert.AreEqual(0, orderedEvents.Count, "Should return empty collection for empty input");
        }

        [Test]
        public void OrderEvents_NullInput_ReturnsEmptyList()
        {
            // SCENARIO: Null input
            // EXPECTED: Empty output collection, no exceptions

            var orderedEvents = InvokeOrderEvents(null);

            Assert.IsNotNull(orderedEvents, "Should return non-null collection for null input");
            Assert.AreEqual(0, orderedEvents.Count, "Should return empty collection for null input");
        }

        #region Helper Methods

        private LinkedList<IPersistentEvent> InvokeOrderEvents(LinkedList<IPersistentEvent> events)
        {
            return (LinkedList<IPersistentEvent>)orderPersistentEventsMethod.Invoke(null, new object[] { events });
        }

        private InstantiateGONetParticipantEvent CreateSpawnEvent(uint gonetId)
        {
            return new InstantiateGONetParticipantEvent
            {
                GONetId = gonetId,
                GONetIdAtInstantiation = gonetId,
                SceneIdentifier = "TestScene",
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };
        }

        private SceneLoadEvent CreateSceneLoadEvent(string sceneName)
        {
            return new SceneLoadEvent
            {
                SceneName = sceneName,
                LoadType = SceneLoadType.BuildSettings,
                Mode = UnityEngine.SceneManagement.LoadSceneMode.Additive,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };
        }

        private ReparentGONetParticipantEvent CreateReparentEvent(uint gonetId, uint newParentGONetId)
        {
            return new ReparentGONetParticipantEvent
            {
                GONetId = gonetId,
                NewParentGONetId = newParentGONetId,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };
        }

        private IPersistentEvent CreateOtherPersistentEvent()
        {
            // Use ValueMonitoringSupport_NewBaselineEvent_System_Single as "other" event type
            // (base class is abstract, so we use a concrete derived type)
            return new ValueMonitoringSupport_NewBaselineEvent_System_Single
            {
                GONetId = 9999,
                NewBaselineValue = 0.0f,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };
        }

        #endregion
    }
}
