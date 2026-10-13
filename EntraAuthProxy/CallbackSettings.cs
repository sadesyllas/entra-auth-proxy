using Microsoft.Identity.Client;

namespace EntraAuthProxy;

/// <summary>A validated callback snapshot; source edits require a new startup.</summary>
internal sealed class CallbackSettings
{
    private readonly string? selectedPort;

    private CallbackSettings(string path, string? selectedPort)
    {
        Path = path;
        this.selectedPort = selectedPort;
    }

    public string Path { get; }

    /// <summary>
    /// Captures the effective path and the existing null-only RedirectPort fallback.
    /// Invalid paths and unusable custom-path ports fail before client
    /// construction or persistent cache access. Root paths retain MSAL defaults.
    /// </summary>
    public static CallbackSettings Capture(IConfiguration configuration)
    {
        var section = configuration.GetSection("RedirectPath");
        var path = section.Value ?? "/";
        if (StartupJsonConfigurationSource.HasNonScalarValue(configuration, "RedirectPath") ||
            !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '?' or '#') ||
            !Uri.TryCreate("http://localhost" + path, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.AbsolutePath, path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RedirectPath must be a URI path starting with a single '/' and contain no query, fragment, backslash, whitespace, or URI-normalized characters.");
        }

        var redirectPort = configuration["RedirectPort"];
        var selectedPort = redirectPort ?? configuration["Port"];
        if (path != "/" &&
            (!int.TryParse(selectedPort, out var port) || port is < 1 or > 65535 or 80))
        {
            var setting = redirectPort != null ? "RedirectPort" : selectedPort != null ? "Port" : "RedirectPort or Port";
            throw new InvalidOperationException($"RedirectPath requires a fixed callback port from {setting}: an integer from 1 through 65535, excluding 80. Missing, empty, unparseable, and automatic ports (0 or 80) are not supported for custom paths.");
        }

        return new CallbackSettings(path, selectedPort);
    }

    /// <summary>
    /// Applies the captured callback to MSAL. Custom paths have a validated fixed
    /// port, so MSAL preserves them during interactive authentication. Root paths
    /// retain existing port parsing and MSAL's default/automatic-port behavior.
    /// </summary>
    public PublicClientApplicationBuilder Configure(PublicClientApplicationBuilder builder)
    {
        if (!string.IsNullOrEmpty(selectedPort) && int.TryParse(selectedPort, out var port))
            return builder.WithRedirectUri($"http://localhost:{port}" + (Path == "/" ? "" : Path));

        return builder.WithDefaultRedirectUri();
    }
}
