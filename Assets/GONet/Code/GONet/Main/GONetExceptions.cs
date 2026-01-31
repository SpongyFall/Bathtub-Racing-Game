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
using System.Runtime.Serialization;

namespace GONet
{
    internal class GONetOutOfOrderHorseDickoryException : Exception
    {
        public GONetOutOfOrderHorseDickoryException()
        {
        }

        public GONetOutOfOrderHorseDickoryException(string message) : base(message)
        {
        }

        public GONetOutOfOrderHorseDickoryException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected GONetOutOfOrderHorseDickoryException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// Thrown when attempting to deserialize sync data for a GONetParticipant that exists
    /// but hasn't completed Awake()/OnGONetReady initialization yet.
    /// Distinct from KeyNotFoundException (participant missing entirely) or GONetOutOfOrderHorseDickoryException (participant fully missing).
    ///
    /// This exception indicates a RACE CONDITION where:
    /// - The participant GameObject exists in the scene/hierarchy
    /// - GONetId lookup succeeds (participant is in dictionaries)
    /// - BUT: didAwakeComplete=false OR syncCompanion=null (async Awake() coroutine still running)
    ///
    /// Solution: Defer bundle processing until OnGONetReady fires (configurable) or drop (default).
    /// </summary>
    [Serializable]
    public class GONetParticipantNotReadyException : Exception
    {
        /// <summary>
        /// The GONetId (instantiation) of the participant that wasn't ready.
        /// Useful for logging/diagnostics and retry logic.
        /// </summary>
        public uint GONetId { get; }

        public GONetParticipantNotReadyException()
        {
        }

        public GONetParticipantNotReadyException(string message) : base(message)
        {
        }

        public GONetParticipantNotReadyException(string message, uint gonetId) : base(message)
        {
            GONetId = gonetId;
        }

        public GONetParticipantNotReadyException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected GONetParticipantNotReadyException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
