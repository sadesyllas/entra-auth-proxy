using System.Collections.Frozen;

namespace EntraAuthProxy;

/// <summary>A validated, immutable snapshot of the effective Headers setting.</summary>
internal sealed class CustomRequestHeaders
{
    private CustomRequestHeaders(FrozenDictionary<string, string?> values) => Values = values;

    public FrozenDictionary<string, string?> Values { get; }

    /// <summary>
    /// Captures static values once, after configuration overlays. Authorization is
    /// reserved for bearer injection; other inputs use normal configuration and
    /// HTTP header handling. Changes to the source require a new startup snapshot.
    /// </summary>
    public static CustomRequestHeaders Capture(IConfiguration configuration)
    {
        var headers = configuration.GetSection("Headers").GetChildren().ToArray();
        if (headers.Any(header => string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Headers cannot configure Authorization because the proxy manages that header.");

        return new CustomRequestHeaders(headers.ToFrozenDictionary(
            header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase));
    }
}
