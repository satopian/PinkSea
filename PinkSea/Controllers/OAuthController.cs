using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using PinkSea.AtProto.Lexicons.Bluesky.Records;
using PinkSea.AtProto.Models.OAuth;
using PinkSea.AtProto.OAuth;
using PinkSea.AtProto.Providers.OAuth;
using PinkSea.AtProto.Xrpc;
using PinkSea.Services;

namespace PinkSea.Controllers;

/// <summary>
/// Controller for the OAuth methods.
/// </summary>
[Route("/oauth")]
public class OAuthController(
    SigningKeyService signingKeyService,
    IAtProtoOAuthClient oAuthClient,
    IXrpcClient xrpcClient) : Controller
{
    /// <summary>
    /// Begins the OAuth login flow.
    /// </summary>
    /// <param name="handle">The handle of the user wanting to log in.</param>
    /// <returns>A redirect.</returns>
    [Route("begin-login")]
    public async Task<IActionResult> BeginLogin(
        [FromQuery] string handle)
    {
        var metadata = GetMetadata();
        var authorizationServer = await oAuthClient.GetOAuthRequestUriForHandle(handle, new OAuthClientData()
        {
            ClientId = metadata.ClientId,
            RedirectUri = metadata.RedirectUris[0],
            Scope = metadata.Scope,
            Key = new JwtKey
            {
                SigningCredentials = new SigningCredentials(signingKeyService.SecurityKey, SecurityAlgorithms.EcdsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                },
                KeyId = signingKeyService.KeyId
            }
        });
        
        if (authorizationServer is null)
            return BadRequest();
        
        return Redirect(authorizationServer);
    }

    /// <summary>
    /// The OAuth callback.
    /// </summary>
    /// <param name="iss">The issuer.</param>
    /// <param name="state">The state.</param>
    /// <param name="code">The code.</param>
    [Route("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string iss,
        [FromQuery] string state,
        [FromQuery] string code)
    {
        var metadata = GetMetadata();
        var token = await oAuthClient.CompleteAuthorization(
            state,
            code,
            new OAuthClientData()
        {
            ClientId = metadata.ClientId,
            RedirectUri = metadata.RedirectUris[0],
            Scope = metadata.Scope,
            Key = new JwtKey
            {
                SigningCredentials =
                    new SigningCredentials(signingKeyService.SecurityKey, SecurityAlgorithms.EcdsaSha256)
                    {
                        CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                    },
                KeyId = signingKeyService.KeyId
            }
        });

        if (!token)
            return BadRequest();

        var profile = xrpcClient.Query<Profile>(
            "https://matsutake.us-west.host.bsky.network",
            "com.atproto.repo.getRecord",
            new
            {
                Repo = "did:plc:2edipcwcjiezjtanjs5vmrlw",
                Collection = "app.bsky.actor.profile",
                Rkey = "self",
            },
            state);
        return Ok(token);
    }
    
    /// <summary>
    /// Returns the client metadata.
    /// </summary>
    /// <returns>The client metadata.</returns>
    [Route("client-metadata.json")]
    public async Task<ActionResult<ClientMetadata>> ClientMetadata()
    {
        return Ok(GetMetadata());
    }

    /// <summary>
    /// Returns the possible JSON Web Keys.
    /// </summary>
    /// <returns>The JSON Web Keys.</returns>
    [Route("jwks.json")]
    public async Task<ActionResult<JsonWebKeySet>> Jwks()
    {
        var set = new JsonWebKeySet();
        var key = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(signingKeyService.SecurityKey);
        key.D = null;
        key.X = Base64UrlEncoder.Encode(signingKeyService.KeyParameters.Q.X!);
        key.Y = Base64UrlEncoder.Encode(signingKeyService.KeyParameters.Q.Y!);
        key.KeyOps.Add("verify");
        key.KeyId = signingKeyService.KeyId;
        set.Keys.Add(key);
        
        return Ok(set);
    }

    /// <summary>
    /// Generates the client metadata.
    /// </summary>
    /// <returns>The client metadata.</returns>
    private ClientMetadata GetMetadata()
    {
        const string baseUrl = "https://ed888befa98df1.lhr.life";
        return new ClientMetadata
        {
            ClientId = $"{baseUrl}/oauth/client-metadata.json",
            ClientName = "PinkSea",
            ApplicationType = "web",
            DpopBoundAccessTokens = true,
            GrantTypes =
            [
                "authorization_code"
            ],
            RedirectUris =
            [
                $"{baseUrl}/oauth/callback"
            ],
            Scope = "atproto transition:generic",
            TokenEndpointAuthMethod = "private_key_jwt",
            TokenEndpointAuthSigningAlgorithm = "ES256",
            JwksUri = $"{baseUrl}/oauth/jwks.json"
        };
    }
}