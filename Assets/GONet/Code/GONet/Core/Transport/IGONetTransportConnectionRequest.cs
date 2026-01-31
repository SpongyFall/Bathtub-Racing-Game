/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original sources in binary form only (compiled code)
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified sources in binary form only (compiled code)
 */

namespace GONet.Transport
{
    /// <summary>
    /// Represents an incoming connection request from a remote client (server-side only).
    /// Allows server to approve or reject connections before they are established.
    ///
    /// <para>
    /// SECURITY: This is critical for P2P transports (Steam, EOS) where auto-accepting
    /// unknown peers can expose IP addresses or enable session hijacking.
    /// </para>
    ///
    /// <para>
    /// USAGE: Invoked via <see cref="IGONetTransport.OnServerConnectionRequested"/> event.
    /// Return true to accept, false to reject.
    /// </para>
    /// </summary>
    public interface IGONetTransportConnectionRequest
    {
        /// <summary>
        /// Remote endpoint address requesting connection.
        /// Format depends on transport:
        /// - Steam: CSteamID as string (ulong)
        /// - EOS: ProductUserId as string
        /// - NetcodeIO: IP:port
        /// - Unity Relay: Opaque connection ID
        /// </summary>
        string RemoteAddress { get; }

        /// <summary>
        /// Transport-specific request metadata (optional, may be null).
        ///
        /// <para>
        /// Examples:
        /// - Steam: P2PSessionRequest_t data
        /// - EOS: SocketId + channel info
        /// - NetcodeIO: Connect token data
        /// - Unity Relay: Join code validation data
        /// </para>
        ///
        /// <para>
        /// Use <see cref="GetNativeRequest{T}"/> for type-safe access.
        /// </para>
        /// </summary>
        byte[] RequestData { get; }

        /// <summary>
        /// Accept connection request.
        /// Client will be added to server and <see cref="IGONetTransport.OnServerClientConnected"/> will be invoked.
        ///
        /// <para>
        /// NOTE: This method may be called from background thread.
        /// Transport implementations should handle thread-safety.
        /// </para>
        /// </summary>
        void Accept();

        /// <summary>
        /// Reject connection request.
        /// Client will not be added to server and will receive disconnect notification with specified reason.
        ///
        /// <para>
        /// NOTE: This method may be called from background thread.
        /// Transport implementations should handle thread-safety.
        /// </para>
        /// </summary>
        /// <param name="reason">Reason for rejection (e.g., ServerFull, AuthenticationFailed, Kicked)</param>
        void Reject(GONetTransportDisconnectReason reason);

        /// <summary>
        /// Access native transport-specific request object for advanced validation.
        ///
        /// <para>
        /// Example usage:
        /// <code>
        /// if (request.GetNativeRequest&lt;CSteamID&gt;() is CSteamID steamId)
        /// {
        ///     // Only accept Steam friends
        ///     bool isFriend = SteamFriends.GetFriendRelationship(steamId) == k_EFriendRelationshipFriend;
        ///     if (isFriend)
        ///         request.Accept();
        ///     else
        ///         request.Reject(GONetTransportDisconnectReason.AuthenticationFailed);
        /// }
        /// </code>
        /// </para>
        /// </summary>
        /// <typeparam name="T">Native request type (e.g., CSteamID, EOS_ProductUserId)</typeparam>
        /// <returns>Native request object, or null if type doesn't match</returns>
        T GetNativeRequest<T>() where T : class;
    }
}
