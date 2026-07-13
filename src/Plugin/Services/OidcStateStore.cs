using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.OAuth2.Services;

/// <summary>
/// In-memory store for in-flight OAuth2 authorization requests. Maps the opaque <c>state</c> value
/// to the PKCE code verifier (and issue time) so the callback can complete the code exchange and
/// verify the request originated here (CSRF protection). Entries are single-use and time-boxed.
/// </summary>
public sealed class OidcStateStore
{
    private static readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a pending authorization request.
    /// </summary>
    /// <param name="state">The opaque state value sent to the identity provider.</param>
    /// <param name="codeVerifier">The PKCE code verifier for this request.</param>
    /// <param name="redirectUri">The redirect_uri used, which must be replayed on token exchange.</param>
    public void Add(string state, string codeVerifier, string redirectUri)
    {
        Prune();
        _entries[state] = new Entry(codeVerifier, redirectUri, DateTime.UtcNow);
    }

    /// <summary>
    /// Consumes (removes and returns) a pending request. Returns <c>false</c> if the state is
    /// unknown or expired.
    /// </summary>
    /// <param name="state">The state value returned by the identity provider.</param>
    /// <param name="codeVerifier">The stored PKCE code verifier.</param>
    /// <param name="redirectUri">The stored redirect_uri.</param>
    /// <returns>Whether a valid, unexpired entry was found.</returns>
    public bool TryConsume(string state, out string codeVerifier, out string redirectUri)
    {
        codeVerifier = string.Empty;
        redirectUri = string.Empty;
        if (string.IsNullOrEmpty(state) || !_entries.TryRemove(state, out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow - entry.CreatedUtc > _ttl)
        {
            return false;
        }

        codeVerifier = entry.CodeVerifier;
        redirectUri = entry.RedirectUri;
        return true;
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _entries)
        {
            if (now - kvp.Value.CreatedUtc > _ttl)
            {
                _entries.TryRemove(kvp.Key, out _);
            }
        }
    }

    private readonly record struct Entry(string CodeVerifier, string RedirectUri, DateTime CreatedUtc);
}
