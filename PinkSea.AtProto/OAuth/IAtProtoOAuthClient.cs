using PinkSea.AtProto.Models.OAuth;

namespace PinkSea.AtProto.OAuth;

/// <summary>
/// The AT protocol OAuth client.
/// </summary>
public interface IAtProtoOAuthClient
{
    /// <summary>
    /// Gets the OAuth redirect URI for a given handle.
    /// </summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The redirect URI.</returns>
    Task<string?> BeginOAuthFlow(
        string handle);

    /// <summary>
    /// Completes authorization for a user.
    /// </summary>
    /// <param name="stateId">The state id.</param>
    /// <param name="authCode">The authorization code.</param>
    /// <returns>Whether the state is now bound to a properly working authorization code.</returns>
    Task<bool> CompleteAuthorization(
        string stateId,
        string authCode);
    
    /// <summary>
    /// Gets the OAuth protected resource for a given PDS.
    /// </summary>
    /// <param name="pds">The PDS.</param>
    /// <returns>The OAuth protected resource.</returns>
    Task<ProtectedResource?> GetOAuthProtectedResourceForPds(string pds);
    
    /// <summary>
    /// Gets the OAuth authorization server data for a given authorization server.
    /// </summary>
    /// <param name="authServer">The authorization server.</param>
    /// <returns>The authorization server data.</returns>
    Task<AuthorizationServer?> GetOAuthAuthorizationServerDataForAuthorizationServer(string authServer);
}