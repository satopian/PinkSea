using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.IdentityModel.Tokens;
using PinkSea.AtProto.Http;
using PinkSea.AtProto.Models.OAuth;
using PinkSea.AtProto.Providers.OAuth;
using PinkSea.AtProto.Providers.Storage;
using PinkSea.AtProto.Resolvers.Did;
using PinkSea.AtProto.Resolvers.Domain;

namespace PinkSea.AtProto.OAuth;

/// <summary>
/// The AT Protocol OAuth client.
/// </summary>
public class AtProtoOAuthClient(
    IHttpClientFactory httpClientFactory,
    IDomainDidResolver domainDidResolver,
    IDidResolver didResolver,
    IJwtSigningProvider jwtSigningProvider,
    IOAuthStateStorageProvider oAuthStateStorageProvider,
    IOAuthClientDataProvider clientDataProvider) : IAtProtoOAuthClient, IDisposable
{
    /// <summary>
    /// The JWT client assertion type.
    /// </summary>
    private const string JwtClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>
    /// The PKCE code challenge method.
    /// </summary>
    private const string CodeChallengeMethod = "S256";

    /// <summary>
    /// The code response type.
    /// </summary>
    private const string CodeResponseType = "code";

    /// <summary>
    /// The authorization code grant type.
    /// </summary>
    private const string AuthorizationCodeGrantType = "authorization_code";
    
    /// <summary>
    /// The HTTP client used for the OAuth client.
    /// </summary>
    private readonly DpopHttpClient _client = new(
        httpClientFactory.CreateClient("oauth-client"),
        jwtSigningProvider,
        clientDataProvider.ClientData);

    /// <inheritdoc />
    public async Task<string?> BeginOAuthFlow(string handle)
    {
        var did = handle;
        if (!did.StartsWith("did"))
            did = await domainDidResolver.GetDidForDomainHandle(handle);

        if (did is null)
            return null;
        
        var resolved = await didResolver.GetDidResponseForDid(did!);
        var pds = resolved?.GetPds();
        if (pds is null)
            return null;

        var authServer = await GetOAuthAuthorizationServerDataForPds(pds);
        var clientData = clientDataProvider.ClientData;
        
        var assertion = jwtSigningProvider.GenerateClientAssertion(new JwtSigningData()
        {
            ClientId = clientData.ClientId,
            Audience = authServer!.Issuer,
            Key = clientData.Key
        });

        var keyPair = GenerateDPopKeypair();
        var state = GenerateRandomState();
        var (codeVerifier, codeChallenge) = GetPkcePair();
        
        var body = new AuthorizationRequest()
        {
            ClientId = clientData.ClientId,
            ResponseType = CodeResponseType,
            Scope = clientData.Scope,
            RedirectUrl = clientData.RedirectUri,
            State = state,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = CodeChallengeMethod,
            ClientAssertionType = JwtClientAssertionType,
            ClientAssertion = assertion,
            LoginHint = handle
        };
        
        var resp = await _client.Post(authServer!.PushedAuthorizationRequestEndpoint!, body, keyPair);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to send a PAR: {await resp.Content.ReadAsStringAsync()}");
            return null;
        }

        var parResponse = await resp.Content.ReadFromJsonAsync<PushedAuthorizationRequestResponse>();

        var finalUrl = new UriBuilder(authServer!.AuthorizationEndpoint!);
        var query = HttpUtility.ParseQueryString(finalUrl.Query);
        query["client_id"] = body.ClientId;
        query["request_uri"] = parResponse!.RequestUri;
        finalUrl.Query = query.ToString();

        await oAuthStateStorageProvider.SetForStateId(state,
            new OAuthState
            {
                Did = did,
                PkceString = codeVerifier,
                Issuer = authServer.Issuer,
                KeyPair = keyPair,
                TokenEndpoint = authServer.TokenEndpoint,
                Pds = pds
            });
        
        return finalUrl.ToString();
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAuthorization(
        string stateId,
        string authCode)
    {
        var oauthState = await oAuthStateStorageProvider.GetForStateId(stateId);
        if (oauthState is null)
            return false;

        var clientData = clientDataProvider.ClientData;
        
        var assertion = jwtSigningProvider.GenerateClientAssertion(new JwtSigningData
        {
            ClientId = clientData.ClientId,
            Audience = oauthState.Issuer,
            Key = clientData.Key
        });
        
        var body = new TokenRequest()
        {
            ClientId = clientData.ClientId,
            GrantType = AuthorizationCodeGrantType,
            Code = authCode,
            RedirectUri = clientData.RedirectUri,
            CodeVerifier = oauthState.PkceString,
            ClientAssertionType = JwtClientAssertionType,
            ClientAssertion = assertion
        };
        
        var resp = await _client.Post(oauthState.TokenEndpoint, body, oauthState.KeyPair);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to retrieve the token: {await resp.Content.ReadAsStringAsync()}");
            return false;
        }

        var tokenResponse = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        if (tokenResponse is null)
            return false;
        
        oauthState.AuthorizationCode = tokenResponse.AccessToken;
        return true;
    }

    /// <inheritdoc />
    public async Task<ProtectedResource?> GetOAuthProtectedResourceForPds(string pds)
    {
        const string wellKnownUrl = "/.well-known/oauth-protected-resource";
        return await _client.RawClient.GetFromJsonAsync<ProtectedResource>($"{pds}{wellKnownUrl}");
    }

    /// <inheritdoc />
    public async Task<AuthorizationServer?> GetOAuthAuthorizationServerDataForAuthorizationServer(string authServer)
    {
        const string wellKnownUrl = "/.well-known/oauth-authorization-server";
        return await _client.RawClient.GetFromJsonAsync<AuthorizationServer>($"{authServer}{wellKnownUrl}");
    }

    /// <summary>
    /// Gets an OAuth Authorization Server data for a given PDS.
    /// </summary>
    /// <param name="pds">The PDS.</param>
    /// <returns>The authorization server data.</returns>
    private async Task<AuthorizationServer?> GetOAuthAuthorizationServerDataForPds(string pds)
    {
        var protectedResource = await GetOAuthProtectedResourceForPds(pds);
        var authServer = protectedResource?.GetAuthorizationServer();
        if (authServer is null)
            return null;

        return await GetOAuthAuthorizationServerDataForAuthorizationServer(authServer);
    }

    /// <summary>
    /// Generates a DPoP keypair.
    /// </summary>
    /// <returns>The DPoP keypair.</returns>
    private DpopKeyPair GenerateDPopKeypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.CreateFromFriendlyName("nistp256"));
        return new DpopKeyPair
        {
            PublicKey = ecdsa.ExportSubjectPublicKeyInfoPem(),
            PrivateKey = ecdsa.ExportECPrivateKeyPem()
        };
    }

    /// <summary>
    /// Generates a random state string.
    /// </summary>
    /// <returns>The state string.</returns>
    private static string GenerateRandomState()
    {
        return RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-", 64);
    }

    /// <summary>
    /// Gets the PKCE pair.
    /// </summary>
    /// <returns>The verifier and the hash.</returns>
    private static (string, string) GetPkcePair()
    {
        var str = RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-", 64);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(str));
        return (str, Base64UrlEncoder.Encode(hash));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
    }
}