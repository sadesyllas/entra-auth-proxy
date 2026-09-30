using System.Net;
using System.Net.Http.Headers;
using EntraAuthProxy;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ProxyBehaviorChecks;

// Negative inputs intentionally name the removed integration. All routing and
// authorization behavior comes from the same registration used by production.
var upstreamBuilder = CreateBuilder();
await using var upstream = upstreamBuilder.Build();
var captures = new System.Collections.Concurrent.ConcurrentQueue<CapturedRequest>();
upstream.Run(async context =>
{
    using var reader = new StreamReader(context.Request.Body);
    captures.Enqueue(new CapturedRequest(
        context.Request.Method, context.Request.Path, context.Request.QueryString.Value ?? "",
        await reader.ReadToEndAsync(),
        context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
    context.Response.StatusCode = 202;
    await context.Response.WriteAsync("upstream-response");
});
await upstream.StartAsync();
var cases = 0;
foreach (var topValue in new string?[] { null, "", "old-top-value" })
foreach (var routeValue in new string?[] { null, "", "old-route-value" })
foreach (var headerSource in new[] { "absent", "caller", "generic-transform" })
{
    var builder = CreateBuilder();
    var settings = new Dictionary<string, string?>
    {
        ["TargetAddress"] = Address(upstream),
        ["ReverseProxy:Routes:premium:ClusterId"] = "target-cluster",
        ["ReverseProxy:Routes:premium:Order"] = "-1",
        ["ReverseProxy:Routes:premium:Match:Path"] = "/premium/{**catch-all}",
        ["ReverseProxy:Routes:premium:Transforms:0:PathRemovePrefix"] = "/premium"
    };
    if (topValue != null) settings["VirtualKey"] = topValue;
    foreach (var route in new[] { "catch-all", "premium" })
    {
        if (routeValue != null) settings[$"ReverseProxy:Routes:{route}:Metadata:VirtualKey"] = routeValue;
        var index = route == "premium" ? 1 : 0;
        settings[$"ReverseProxy:Routes:{route}:Transforms:{index}:RequestHeader"] = "X-Generic-Transform";
        settings[$"ReverseProxy:Routes:{route}:Transforms:{index}:Set"] = "configured-value";
        if (headerSource == "generic-transform")
        {
            settings[$"ReverseProxy:Routes:{route}:Transforms:{index + 1}:RequestHeader"] = "x-bf-vk";
            settings[$"ReverseProxy:Routes:{route}:Transforms:{index + 1}:Set"] = "generic-value";
        }
    }
    builder.Configuration.AddInMemoryCollection(settings);
    var tokens = new TokenProvider();
    builder.Services.AddSingleton(tokens);
    builder.Services.AddTokenInjectingProxy(builder.Configuration);
    await using var proxy = builder.Build();
    proxy.MapReverseProxy();
    await proxy.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(Address(proxy)), Timeout = TimeSpan.FromSeconds(10) };
    foreach (var token in new[] { "deterministic-token", "" })
    foreach (var path in new[] { "/v1/models", "/premium/v1/models" })
    foreach (var method in new[] { HttpMethod.Post, HttpMethod.Patch })
    {
        tokens.SetToken(token);
        using var request = new HttpRequestMessage(method, path + "?model=a%2Fb&repeat=1&repeat=2")
        {
            Content = new StringContent("{\"payload\":\"unchanged\"}")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-token");
        request.Headers.Add("X-Caller-Header", "forwarded-value");
        if (headerSource == "caller") request.Headers.Add("x-bf-vk", "caller-value");
        using var response = await client.SendAsync(request);
        Require(response.StatusCode == HttpStatusCode.Accepted, "upstream status");
        Require(await response.Content.ReadAsStringAsync() == "upstream-response", "upstream response body");
        Require(captures.TryDequeue(out var capture) && capture != null, "upstream capture");
        Require(capture!.Method == method.Method, "method preservation");
        Require(capture.Path == "/v1/models", "generated path / custom prefix removal");
        Require(capture.Query == "?model=a%2Fb&repeat=1&repeat=2", "query preservation");
        Require(capture.Body == "{\"payload\":\"unchanged\"}", "request body preservation");
        Require(capture.Headers["Authorization"].SequenceEqual(new[] { "Bearer " + (token == "" ? "client-token" : token) }), "bearer replacement / empty-token behavior");
        Require(capture.Headers["X-Caller-Header"].SequenceEqual(new[] { "forwarded-value" }), "ordinary header forwarding");
        Require(capture.Headers["X-Generic-Transform"].SequenceEqual(new[] { "configured-value" }), "generic transform");
        var forwarded = capture.Headers.GetValueOrDefault("x-bf-vk") ?? [];
        var expected = headerSource switch
        {
            "caller" => new[] { "caller-value" },
            "generic-transform" => new[] { "generic-value" },
            _ => Array.Empty<string>()
        };
        Require(forwarded.SequenceEqual(expected), "no built-in header; caller and generic values remain exact");
        cases++;
    }
    await proxy.StopAsync();
}
await upstream.StopAsync();
Console.WriteLine($"PASS: {cases} production proxy behavior cases.");

static WebApplicationBuilder CreateBuilder()
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    return builder;
}

static string Address(WebApplication app) =>
    app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

static void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"Failed: {description}");
}
