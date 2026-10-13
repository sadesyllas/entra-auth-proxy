using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using EntraAuthProxy;
using static ProxyBehaviorChecks.ProxyTestHost;

namespace ProxyBehaviorChecks;

internal static class AccessTokenProxyChecks
{
    public static async Task RunAsync(string upstreamAddress, ConcurrentQueue<CapturedRequest> captures)
    {
        var cases = 0;
        foreach (var enabled in new[] { false, true })
        {
            var builder = CreateBuilder();
            builder.Configuration["TargetAddress"] = upstreamAddress;
            var tokens = new TokenProvider(enabled);
            builder.Services.AddSingleton(tokens);
            builder.Services.AddTokenInjectingProxy(builder.Configuration);
            await using var proxy = builder.Build();
            proxy.MapReverseProxy();
            await proxy.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(Address(proxy)), Timeout = TimeSpan.FromSeconds(10) };
            var firstToken = "synthetic-first-" + new string('a', 1024);
            var secondToken = "synthetic-second-" + new string('b', 1024);
            var original = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                tokens.SetToken(firstToken);
                // Equal publications must not add output; all request reads are silent.
                tokens.SetToken(firstToken);
                await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => SendAsync(client)));
                for (var index = 0; index < 16; index++)
                    AssertAuthorization(captures, firstToken);
                Require(output.ToString() == (enabled ? "Access token: " + firstToken + Environment.NewLine : ""),
                    "enabled/disabled publication output and silent proxy reads");

                var writing = Task.Run(() =>
                {
                    for (var index = 0; index < 300; index++)
                        tokens.SetToken(index % 2 == 0 ? secondToken : firstToken);
                });
                var reading = Task.Run(() =>
                {
                    for (var index = 0; index < 10000; index++)
                    {
                        var token = tokens.GetToken();
                        Require(token == firstToken || token == secondToken, "concurrent read sees a complete published token");
                    }
                });
                var forwarding = Task.WhenAll(Enumerable.Range(0, 32).Select(_ => SendAsync(client)));
                await Task.WhenAll(writing, reading, forwarding);
                for (var index = 0; index < 32; index++)
                    AssertAuthorization(captures, firstToken, secondToken);
                var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                Require(lines.Length == (enabled ? 301 : 0), "only changed publications print under concurrent reads and forwarding");
                if (enabled)
                {
                    Require(lines.All(line => line == "Access token: " + firstToken || line == "Access token: " + secondToken),
                        "concurrent output contains complete labeled synthetic tokens");
                }
            }
            finally
            {
                Console.SetOut(original);
            }
            await proxy.StopAsync();
            cases += 48;
        }
        Console.WriteLine($"PASS: {cases} enabled/disabled token-output proxy cases with concurrent publication, reads, and bearer replacement.");
    }

    private static async Task SendAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/token-check");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        using var response = await client.SendAsync(request);
        Require(response.StatusCode == HttpStatusCode.Accepted, "concurrent proxy upstream status");
    }

    private static void AssertAuthorization(ConcurrentQueue<CapturedRequest> captures, params string[] allowed)
    {
        Require(captures.TryDequeue(out var capture) && capture != null, "concurrent upstream request capture");
        var authorization = capture!.Headers["Authorization"];
        Require(authorization.Length == 1 && allowed.Any(token => authorization[0] == "Bearer " + token),
            "proxy replaces caller authorization with a complete current token");
    }
}
