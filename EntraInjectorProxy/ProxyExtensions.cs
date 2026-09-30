using Yarp.ReverseProxy.Transforms;

namespace EntraInjectorProxy;

public static class ProxyExtensions
{
    /// <summary>
    /// Registers the generated route, user-supplied YARP overlays, and bearer replacement
    /// on every route. The host must register a TokenProvider before accepting requests.
    /// An empty current token leaves the forwarded authorization header unchanged.
    /// </summary>
    public static void AddTokenInjectingProxy(this IServiceCollection services, IConfiguration configuration)
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
                builderContext.AddRequestTransform(transformContext =>
                {
                    var provider = transformContext.HttpContext.RequestServices.GetRequiredService<TokenProvider>();
                    var token = provider.GetToken();
                    if (!string.IsNullOrEmpty(token))
                    {
                        transformContext.ProxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }

                    return ValueTask.CompletedTask;
                });
            });
    }
}
