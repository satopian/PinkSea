using PinkSea.AtProto.Models.Did;

namespace PinkSea.AtProto.Resolvers.Did;

/// <summary>
/// Resolves a DID to a DID document.
/// </summary>
public interface IDidResolver
{
    /// <summary>
    /// Gets the DID document for a given DID.
    /// </summary>
    /// <param name="did">The DID.</param>
    /// <returns>The document, if it was possible to fetch.</returns>
    Task<DidResponse?> GetDidResponseForDid(string did);
}