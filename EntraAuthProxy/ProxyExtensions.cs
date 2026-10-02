using Yarp.ReverseProxy.Transforms;

namespace EntraAuthProxy;

public static class ProxyExtensions
{
    /// <summary>
    /// Registers the generated route, user-supplied YARP overlays, bearer replacement,
    /// and static Headers on every route. Configured headers replace caller and route
    /// values using normal YARP request/content header handling, including empty values.
    /// The host must register a TokenProvider before accepting requests.
    /// An empty current token leaves the forwarded authorization header unchanged.
    /// Captures Headers at registration and rejects Authorization before registering
    /// the proxy. Hosts using startup authentication must validate before sign-in.
    /// </summary>
    public static void AddTokenInjectingProxy(this IServiceCollection services, IConfiguration configuration) =>
        services.AddTokenInjectingProxy(configuration, CustomRequestHeaders.Capture(configuration));

    // Startup passes its pre-authentication snapshot; direct callers capture at
    // registration. Neither boundary reloads Headers when YARP rebuilds routes.
    internal static void AddTokenInjectingProxy(
        this IServiceCollection services, IConfiguration configuration, CustomRequestHeaders customHeaders)
    {
        var yarpConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                {"Routes:catch-all:ClusterId", "target-cluster"},
                {"Routes:catch-all:Match:Path", "{**catch-all}"},
                {"Clusters:target-cluster:Destinations:destination1:Address", configuration["TargetAddress"] ?? string.Empty},
                {"Clusters:target-cluster:HttpClient:DangerousAcceptAnyServerCertificate", configuration["DangerousAcceptAnyServerCertificate"] ?? "false"}
            })
            .AddConfiguration(configuration.GetSection("ReverseProxy"))
            .Build();

        services.AddReverseProxy()
            .LoadFromConfig(yarpConfig)
            .AddTransforms(builderContext =>
            {
                if (customHeaders.Values.Count > 0)
                {
                    // YARP normally appends these defaults after provider transforms.
                    // Add them here so even Host and forwarding-header collisions are
                    // replaced by the static map, while respecting explicit route policy.
                    if (!builderContext.RequestTransforms.Any(transform => transform is RequestHeaderOriginalHostTransform))
                        builderContext.AddOriginalHost(false);
                    if (builderContext.UseDefaultForwarders.GetValueOrDefault(true))
                        builderContext.AddXForwarded();
                }

                builderContext.AddRequestTransform(transformContext =>
                {
                    var provider = transformContext.HttpContext.RequestServices.GetRequiredService<TokenProvider>();
                    var token = provider.GetToken();
                    if (!string.IsNullOrEmpty(token))
                    {
                        transformContext.ProxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }

                    foreach (var header in customHeaders.Values)
                    {
                        RequestTransform.RemoveHeader(transformContext, header.Key);
                        RequestTransform.AddHeader(transformContext, header.Key, header.Value);
                    }

                    return ValueTask.CompletedTask;
                });
            });
    }
}
