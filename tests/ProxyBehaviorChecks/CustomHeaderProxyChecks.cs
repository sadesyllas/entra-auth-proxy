using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EntraAuthProxy;
using Yarp.ReverseProxy.Configuration;
using static ProxyBehaviorChecks.ProxyTestHost;

namespace ProxyBehaviorChecks;

internal static class CustomHeaderProxyChecks
{
    private const string Payload = "{\"payload\":\"unchanged\"}";

    public static async Task RunAsync(string upstreamAddress, ConcurrentQueue<CapturedRequest> captures)
    {
        var cases = 0;
        foreach (var mode in new[] { "absent", "empty", "one", "multiple" })
        {
            var builder = CreateBuilder();
            var settings = Settings(upstreamAddress);
            var expected = new Dictionary<string, string>();
            if (mode == "empty") settings["Headers"] = "";
            if (mode is "one" or "multiple") expected["X-Static"] = "Bearer ${token} {value}";
            if (mode == "multiple")
            {
                expected["X-Empty"] = "";
                expected["Content-Type"] = "application/custom+json";
                expected["Content-Language"] = "en";
                expected["Host"] = "static.example";
                expected["X-Forwarded-Proto"] = "static-protocol";
                expected["Forwarded"] = "proto=static";
            }
            foreach (var header in expected) settings["Headers:" + header.Key] = header.Value;
            builder.Configuration.AddInMemoryCollection(settings);
            var tokens = new TokenProvider();
            builder.Services.AddSingleton(tokens);
            builder.Services.AddTokenInjectingProxy(builder.Configuration);
            await using var proxy = builder.Build();
            proxy.MapReverseProxy();
            await proxy.StartAsync();
            using var client = Client(proxy);
            foreach (var token in new[] { "deterministic-token", "" })
            foreach (var path in new[] { "/v1/models", "/premium/v1/models" })
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Patch })
            foreach (var repeat in Enumerable.Range(0, 2))
            {
                tokens.SetToken(token);
                await SendAndCheckAsync(client, captures, path, method, token, expected, "route-original");
                cases++;
            }

            tokens.SetToken("concurrent-token");
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
                SendAndCheckAsync(client, captures, "/premium/v1/models", HttpMethod.Get, "concurrent-token", expected, "route-original")));
            cases += 32;
            await proxy.StopAsync();
        }

        cases += await CheckReloadAsync(upstreamAddress, captures);
        Require(captures.IsEmpty, "every custom-header request checked upstream");
        Console.WriteLine($"PASS: {cases} custom-header forwarding, collision, content, concurrency, JSON/YARP reload, and restart cases.");
    }

    private static async Task<int> CheckReloadAsync(string upstreamAddress, ConcurrentQueue<CapturedRequest> captures)
    {
        var directory = Path.Combine(Path.GetTempPath(), "entra-proxy-headers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "explicit.json");
            var initial = Settings(upstreamAddress);
            initial["Headers:X-Static"] = "startup";
            initial["Headers:X-Removed"] = "retained-until-restart";
            WriteConfiguration(path, initial);
            var builder = CreateBuilder();
            var startup = StartupConfiguration.Load(builder.Configuration,
                ConfigurationLocations.Resolve(path, null, directory, directory, directory))!;

            var edited = Settings(upstreamAddress);
            edited["Headers:X-Static"] = "edited";
            edited["Headers:X-Added"] = "new";
            SetRouteHeader(edited, "X-Route-Only", "route-rebuilt");
            // Model JSON changing during startup authentication. Registration must
            // use the pre-authentication snapshot, even if sources already reloaded.
            WriteConfiguration(path, edited);
            ((IConfigurationRoot)builder.Configuration).Reload();
            var tokens = new TokenProvider();
            tokens.SetToken("reload-token");
            builder.Services.AddSingleton(tokens);
            builder.Services.AddTokenInjectingProxy(builder.Configuration, startup.Headers);
            await using (var proxy = builder.Build())
            {
                proxy.MapReverseProxy();
                await proxy.StartAsync();
                using var client = Client(proxy);
                var expected = new Dictionary<string, string> { ["X-Static"] = "startup", ["X-Removed"] = "retained-until-restart" };
                foreach (var route in new[] { "/v1/models", "/premium/v1/models" })
                    await SendAndCheckAsync(client, captures, route, HttpMethod.Get, "reload-token", expected, "route-rebuilt", ["X-Added"]);

                // A second real JSON reload triggers YARP's production config provider
                // and transform rebuilding while custom additions/edits/removals stay frozen.
                SetRouteHeader(edited, "X-Route-Only", "route-rebuilt-again");
                edited["Headers:X-Static"] = "edited-again";
                WriteConfiguration(path, edited);
                var provider = proxy.Services.GetRequiredService<IProxyConfigProvider>();
                var oldRoute = provider.GetConfig();
                var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = oldRoute.ChangeToken.RegisterChangeCallback(_ => rebuilt.TrySetResult(), null);
                ((IConfigurationRoot)builder.Configuration).Reload();
                await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var current = provider.GetConfig();
                Require(!ReferenceEquals(oldRoute, current), "YARP receives rebuilt configuration");

                // The provider's change signal precedes endpoint publication. Wait for
                // the changed route-only header to prove requests use rebuilt transforms.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var capture = await SendAsync(client, captures, "/premium/v1/models", HttpMethod.Get, deadline.Token);
                    CheckCapture(capture, HttpMethod.Get, "reload-token", expected, ["X-Added"]);
                    if (capture.Headers["X-Route-Only"].SequenceEqual(new[] { "route-rebuilt-again" })) break;
                    await Task.Delay(20, deadline.Token);
                }
                await SendAndCheckAsync(client, captures, "/v1/models", HttpMethod.Post, "reload-token", expected, "route-rebuilt-again", ["X-Added"]);
                await proxy.StopAsync();
            }

            // A new host re-registers through the same production startup boundary.
            var restartedBuilder = CreateBuilder();
            var restarted = StartupConfiguration.Load(restartedBuilder.Configuration,
                ConfigurationLocations.Resolve(path, null, directory, directory, directory))!;
            restartedBuilder.Services.AddSingleton(tokens);
            restartedBuilder.Services.AddTokenInjectingProxy(restartedBuilder.Configuration, restarted.Headers);
            await using (var proxy = restartedBuilder.Build())
            {
                proxy.MapReverseProxy();
                await proxy.StartAsync();
                using var client = Client(proxy);
                var expected = new Dictionary<string, string> { ["X-Static"] = "edited-again", ["X-Added"] = "new" };
                foreach (var route in new[] { "/v1/models", "/premium/v1/models" })
                    await SendAndCheckAsync(client, captures, route, HttpMethod.Post, "reload-token", expected, "route-rebuilt-again", ["X-Removed"]);
                await proxy.StopAsync();
            }
            return 6;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Dictionary<string, string?> Settings(string upstreamAddress)
    {
        var settings = new Dictionary<string, string?>
        {
            ["TargetAddress"] = upstreamAddress,
            ["ReverseProxy:Routes:premium:ClusterId"] = "target-cluster",
            ["ReverseProxy:Routes:premium:Order"] = "-1",
            ["ReverseProxy:Routes:premium:Match:Path"] = "/premium/{**catch-all}",
            ["ReverseProxy:Routes:premium:Transforms:0:PathRemovePrefix"] = "/premium"
        };
        SetRouteHeader(settings, "x-sTaTiC", "route-first", append: true);
        SetRouteHeader(settings, "X-Static", "route-second", append: true);
        SetRouteHeader(settings, "X-Empty", "route-nonempty");
        SetRouteHeader(settings, "X-Route-Only", "route-original");
        SetRouteHeader(settings, "content-TYPE", "application/route+json");
        return settings;
    }

    private static void SetRouteHeader(Dictionary<string, string?> settings, string name, string value, bool append = false)
    {
        foreach (var route in new[] { "catch-all", "premium" })
        {
            var prefix = $"ReverseProxy:Routes:{route}:Transforms:";
            var existing = settings.Keys.FirstOrDefault(key => key.StartsWith(prefix) && key.EndsWith(":RequestHeader") && settings[key] == name);
            var index = existing == null
                ? settings.Keys.Where(key => key.StartsWith(prefix)).Select(key => int.Parse(key[prefix.Length..].Split(':')[0])).DefaultIfEmpty(-1).Max() + 1
                : int.Parse(existing[prefix.Length..].Split(':')[0]);
            settings[$"{prefix}{index}:RequestHeader"] = name;
            settings[$"{prefix}{index}:{(append ? "Append" : "Set")}"] = value;
        }
    }

    private static void WriteConfiguration(string path, Dictionary<string, string?> settings)
    {
        // IConfiguration's normal JSON flattening also accepts colon-delimited keys.
        // The Headers section stays a public flat string object in these fixtures.
        var json = settings.Where(setting => !setting.Key.StartsWith("Headers:")).ToDictionary(setting => setting.Key, setting => (object?)setting.Value);
        json["Headers"] = settings.Where(setting => setting.Key.StartsWith("Headers:")).ToDictionary(setting => setting.Key["Headers:".Length..], setting => setting.Value);
        File.WriteAllText(path, JsonSerializer.Serialize(json));
    }

    private static HttpClient Client(WebApplication proxy) => new() { BaseAddress = new Uri(Address(proxy)), Timeout = TimeSpan.FromSeconds(10) };

    private static async Task SendAndCheckAsync(HttpClient client, ConcurrentQueue<CapturedRequest> captures, string path,
        HttpMethod method, string token, Dictionary<string, string> expected, string routeValue, string[]? absent = null)
    {
        var capture = await SendAsync(client, captures, path, method);
        CheckCapture(capture, method, token, expected, absent);
        Require(capture.Headers["X-Route-Only"].SequenceEqual(new[] { routeValue }), "unrelated route transform remains effective");
    }

    private static async Task<CapturedRequest> SendAsync(HttpClient client, ConcurrentQueue<CapturedRequest> captures,
        string path, HttpMethod method, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path + "?model=a%2Fb&repeat=1&repeat=2");
        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(Payload);
            request.Content.Headers.Add("Content-Language", "caller-language");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-token");
        request.Headers.Add("X-Caller-Header", "forwarded-value");
        request.Headers.Add("x-static", new[] { "caller-first", "caller-second" });
        request.Headers.Add("x-empty", "caller-nonempty");
        using var response = await client.SendAsync(request, cancellationToken);
        Require(response.StatusCode == HttpStatusCode.Accepted, "upstream status with static headers");
        Require(await response.Content.ReadAsStringAsync(cancellationToken) == "upstream-response", "upstream response body with static headers");
        Require(captures.TryDequeue(out var capture) && capture != null, "custom-header upstream capture");
        return capture!;
    }

    private static void CheckCapture(CapturedRequest capture, HttpMethod method, string token,
        Dictionary<string, string> expected, string[]? absent)
    {
        Require(capture.Method == method.Method && capture.Path == "/v1/models", "method and generated/custom route preservation");
        Require(capture.Query == "?model=a%2Fb&repeat=1&repeat=2", "query with static headers");
        Require(capture.Body == (method == HttpMethod.Get ? "" : Payload), "requests with / without content preserve payload");
        Require(capture.Headers["Authorization"].SequenceEqual(new[] { "Bearer " + (token == "" ? "client-token" : token) }), "custom headers independent of bearer replacement / empty-token forwarding");
        Require(capture.Headers["X-Caller-Header"].SequenceEqual(new[] { "forwarded-value" }), "unrelated caller header preserved");
        foreach (var header in expected)
            Require(capture.Headers.TryGetValue(header.Key, out var values) && values.SequenceEqual(new[] { header.Value }), $"single exact static value for {header.Key}");
        if (!expected.ContainsKey("X-Static"))
            Require(string.Join(",", capture.Headers["X-Static"]).Replace(" ", "") == "caller-first,caller-second,route-first,route-second", "omitted/empty map retains caller and route multiple values");
        if (!expected.ContainsKey("X-Empty")) Require(capture.Headers["X-Empty"].SequenceEqual(new[] { "route-nonempty" }), "unconfigured empty-value collision follows route transform");
        if (!expected.ContainsKey("Host"))
        {
            Require(capture.Headers["Host"].Single()!.StartsWith("127.0.0.1:"), "normal destination Host preserved");
            Require(capture.Headers["X-Forwarded-Proto"].SequenceEqual(new[] { "http" }), "default forwarding headers preserved");
        }
        foreach (var header in absent ?? []) Require(!capture.Headers.ContainsKey(header), "startup snapshot / restart absence of " + header);
    }
}
