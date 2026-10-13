namespace EntraAuthProxy;

/// <summary>Publishes MSAL access tokens for the proxy and optional explicit console output.</summary>
/// <param name="printAccessToken">Explicitly enables full token output on stdout; defaults to false and remains fixed for this provider.</param>
public class TokenProvider(bool printAccessToken = false)
{
    private string _token = string.Empty;
    private readonly object publicationLock = new();

    /// <summary>Reads the latest published token without producing output.</summary>
    public string GetToken() => Volatile.Read(ref _token);

    /// <summary>
    /// Publishes a token atomically. Null is ignored; an empty token clears storage.
    /// When enabled at construction, writes each changed non-empty value in full
    /// to stdout, independently of logging. Startup and refresh share this boundary.
    /// Concurrent publications serialize comparison, storage, and console output.
    /// </summary>
    public void SetToken(string? token)
    {
        if (token == null) return;

        lock (publicationLock)
        {
            var changed = !string.Equals(_token, token, StringComparison.Ordinal);
            Volatile.Write(ref _token, token);
            if (printAccessToken && changed && token.Length != 0)
                Console.WriteLine("Access token: " + token);
        }
    }
}
