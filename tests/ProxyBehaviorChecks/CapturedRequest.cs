namespace ProxyBehaviorChecks;

internal sealed record CapturedRequest(
    string Method,
    string Path,
    string Query,
    string Body,
    IReadOnlyDictionary<string, string?[]> Headers);
