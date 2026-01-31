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
using System;

namespace GONet.Tests
{
    /// <summary>
    /// Regression tests for the async RPC pooled event lifecycle bug (January 2026).
    ///
    /// ROOT CAUSE: RpcEvent implements ISelfReturnEvent, and GONetEventBus calls Return()
    /// immediately after Handle() returns. For async handlers, Handle() returns at the first
    /// await, but the handler hasn't finished yet. By the time it resumes after the await,
    /// the RpcEvent has been returned to the pool and its fields (CorrelationId, etc.) reset to 0.
    ///
    /// FIX: The code generator now caches CorrelationId and SourceAuthorityId from the event/envelope
    /// BEFORE the await, so the cached values survive the pool return.
    ///
    /// WHAT THESE TESTS VALIDATE:
    /// 1. RpcEvent.Return() resets CorrelationId to 0 (proves values cannot be read after Return)
    /// 2. RpcEvent.Return() resets other critical fields
    /// 3. Cached values survive Return() (the fix pattern)
    /// 4. ISelfReturnEvent contract: Return clears all mutable state
    /// </summary>
    [TestFixture]
    [Category("RPC")]
    [Category("Pooling")]
    [Category("AsyncLifecycle")]
    public class GONetRpcAsyncPoolLifecycleTests
    {
        #region RpcEvent Return Resets Fields

        [Test]
        public void RpcEvent_Return_ResetsCorrelationId()
        {
            // SCENARIO: An RpcEvent has a non-zero CorrelationId (indicating an async RPC with return value).
            // After Return(), CorrelationId must be 0 (pool-safe state).
            // THIS IS THE BUG: If the async handler reads CorrelationId after Return(), it gets 0.

            var rpcEvent = RpcEvent.Borrow();
            rpcEvent.CorrelationId = 12345L;

            Assert.AreEqual(12345L, rpcEvent.CorrelationId, "Setup: CorrelationId should be set");

            rpcEvent.Return();

            Assert.AreEqual(0L, rpcEvent.CorrelationId,
                "After Return(), CorrelationId must be 0. " +
                "This proves async handlers MUST cache CorrelationId before await.");
        }

        [Test]
        public void RpcEvent_Return_ResetsRpcId()
        {
            var rpcEvent = RpcEvent.Borrow();
            rpcEvent.RpcId = 0xDEADBEEF;

            rpcEvent.Return();

            Assert.AreEqual(0u, rpcEvent.RpcId,
                "After Return(), RpcId must be 0");
        }

        [Test]
        public void RpcEvent_Return_ResetsData()
        {
            var rpcEvent = RpcEvent.Borrow();
            rpcEvent.Data = new byte[] { 1, 2, 3 };

            rpcEvent.Return();

            Assert.IsNull(rpcEvent.Data,
                "After Return(), Data must be null");
        }

        [Test]
        public void RpcEvent_Return_ResetsOriginatorAuthorityId()
        {
            var rpcEvent = RpcEvent.Borrow();
            rpcEvent.OriginatorAuthorityId = 42;

            rpcEvent.Return();

            Assert.AreEqual(0, rpcEvent.OriginatorAuthorityId,
                "After Return(), OriginatorAuthorityId must be 0");
        }

        #endregion

        #region Cached Value Pattern (The Fix)

        [Test]
        public void CachedValuesPattern_SurvivesReturn()
        {
            // SCENARIO: Demonstrates the correct pattern used by generated async RPC handlers.
            // Values are cached into local variables BEFORE Return() is called (or before await).
            // After Return(), the cached locals still hold the original values.

            var rpcEvent = RpcEvent.Borrow();
            rpcEvent.CorrelationId = 99999L;
            rpcEvent.OriginatorAuthorityId = 7;

            // Cache BEFORE return (this is what the generated code does before await)
            long cachedCorrelationId = rpcEvent.CorrelationId;
            ushort cachedOriginatorId = rpcEvent.OriginatorAuthorityId;

            // Simulate pool return (happens after Handle() returns at the first await)
            rpcEvent.Return();

            // After Return(), the event fields are gone...
            Assert.AreEqual(0L, rpcEvent.CorrelationId, "Event field is reset after Return");

            // ...but cached values are intact
            Assert.AreEqual(99999L, cachedCorrelationId,
                "Cached CorrelationId survives pool return");
            Assert.AreEqual(7, cachedOriginatorId,
                "Cached OriginatorAuthorityId survives pool return");
        }

        #endregion

        #region ISelfReturnEvent Contract

        [Test]
        public void RpcEvent_ImplementsISelfReturnEvent()
        {
            // SCENARIO: RpcEvent must implement ISelfReturnEvent - this is what triggers
            // the automatic Return() call in GONetEventBus after Handle() completes.

            var rpcEvent = RpcEvent.Borrow();
            Assert.IsTrue(rpcEvent is ISelfReturnEvent,
                "RpcEvent must implement ISelfReturnEvent for automatic pool return");
            rpcEvent.Return();
        }

        [Test]
        public void PersistentRpcEvent_DoesNotImplementISelfReturnEvent()
        {
            // SCENARIO: PersistentRpcEvent must NOT implement ISelfReturnEvent.
            // Persistent events are stored by reference for late-joiner delivery
            // and must never be returned to a pool.

            var persistentEvent = new PersistentRpcEvent();
            Assert.IsFalse(persistentEvent is ISelfReturnEvent,
                "PersistentRpcEvent must NOT implement ISelfReturnEvent - " +
                "persistent events are stored for late-joiners and must not be pooled");
        }

        [Test]
        public void RpcResponseEvent_ImplementsISelfReturnEvent()
        {
            // RpcResponseEvent carries async return values and is also pooled.
            var responseEvent = RpcResponseEvent.Borrow();
            Assert.IsTrue(responseEvent is ISelfReturnEvent,
                "RpcResponseEvent must implement ISelfReturnEvent");
            responseEvent.Return();
        }

        [Test]
        public void RpcResponseEvent_Return_ResetsCorrelationId()
        {
            var responseEvent = RpcResponseEvent.Borrow();
            responseEvent.CorrelationId = 77777L;

            responseEvent.Return();

            Assert.AreEqual(0L, responseEvent.CorrelationId,
                "RpcResponseEvent.CorrelationId must be 0 after Return()");
        }

        #endregion

        #region Borrow-Return Cycle

        [Test]
        public void RpcEvent_BorrowReturnCycle_ProducesCleanState()
        {
            // SCENARIO: After Return() and re-Borrow(), the event should have clean defaults.
            // This validates that the pool doesn't leak state between uses.

            var first = RpcEvent.Borrow();
            first.CorrelationId = 11111L;
            first.RpcId = 0xCAFE;
            first.OriginatorAuthorityId = 5;
            first.Return();

            var second = RpcEvent.Borrow();

            // The second borrow might or might not be the same object (pool implementation detail)
            // but its fields must be clean regardless
            Assert.AreEqual(0L, second.CorrelationId, "Borrowed event must have CorrelationId=0");
            Assert.AreEqual(0u, second.RpcId, "Borrowed event must have RpcId=0");
            Assert.IsNull(second.Data, "Borrowed event must have null Data");

            second.Return();
        }

        #endregion
    }
}
