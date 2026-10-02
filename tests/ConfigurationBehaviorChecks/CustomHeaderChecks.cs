using EntraAuthProxy;
using Microsoft.Identity.Client;
using static ConfigurationBehaviorChecks.TestAssertions;

namespace ConfigurationBehaviorChecks;

internal static class CustomHeaderChecks
{
    public static async Task RunAsync()
    {
        var cases = 0;
        var authenticationResult = PublicClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111").Build();

        foreach (var emptyObject in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var path = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            TestWorkspace.WriteJson(path, emptyObject ? new { Headers = new Dictionary<string, string>() } : (object)new { Port = 5000 });
            using var configuration = new ConfigurationManager();
            var startup = StartupConfiguration.Load(configuration, workspace.Resolve(explicitFile: path));
            Require(startup!.Headers.Values.Count == 0, "absent / empty Headers snapshot");
            cases++;
        }

        foreach (var environment in new string?[] { null, "", " \t\r\n", "environment" })
        foreach (var profile in new string?[] { null, "work" })
        foreach (var emptyOverlay in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var effectiveEnvironment = environment == "environment" ? workspace.EnvironmentDirectory : environment;
            var locations = workspace.Resolve(profile, effectiveEnvironment);
            WriteHeaders(locations.GlobalConfigurationPath, new()
            {
                ["X-Shared"] = "global", ["X-Global"] = "inherited", ["X-Empty"] = "",
                ["X-Literal"] = "Bearer ${token} {value}"
            });
            WriteHeaders(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), emptyOverlay ? new() : new()
            {
                ["x-shared"] = "local", ["X-Local"] = "added"
            });
            // The unprofiled file must not contaminate a selected profile.
            if (profile != null) WriteHeaders(Path.Combine(locations.BaseDirectory, "entraauthproxy.json"), new() { ["X-Ignored"] = "base" });

            using var configuration = new ConfigurationManager();
            var startup = StartupConfiguration.Load(configuration, locations)!;
            var values = startup.Headers.Values;
            var overlayApplies = environment != "environment" && !emptyOverlay;
            Require(values.Count == (overlayApplies ? 5 : 4), "key-level overlay retains unrelated entries; empty overlay does not erase");
            Require(values["X-SHARED"] == (overlayApplies ? "local" : "global"), "case-insensitive overlay and lookup");
            Require(values["X-Global"] == "inherited" && values["X-Empty"] == "", "static and empty values retained");
            Require(values["X-Literal"] == "Bearer ${token} {value}", "values are not interpolated or prefixed");
            Require(!values.ContainsKey("X-Ignored"), "profile selection excludes the unprofiled map");
            Require(values.ContainsKey("X-Local") == overlayApplies, "environment base disables local Headers");
            var authenticated = false;
            await startup.AuthenticateAsync(new TokenProvider(), (_, _, _) =>
            {
                authenticated = true;
                return Task.FromResult(authenticationResult);
            });
            Require(authenticated, "non-Authorization headers reach authentication boundary");

            WriteHeaders(locations.GlobalConfigurationPath, new() { ["X-Shared"] = "edited", ["X-New"] = "added" });
            if (locations.LocalConfigurationPath != null) WriteHeaders(locations.LocalConfigurationPath, new());
            ((IConfigurationRoot)configuration).Reload();
            Require(configuration["Headers:X-Shared"] == "edited", "production JSON sources really reload");
            Require(values["X-Shared"] == (overlayApplies ? "local" : "global") && values.ContainsKey("X-Global") && !values.ContainsKey("X-New"),
                "captured map ignores live additions, edits, and removals");
            cases++;
        }

        using (var workspace = new TestWorkspace())
        {
            var explicitPath = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            WriteHeaders(explicitPath, new() { ["X-Explicit"] = "exact" });
            WriteHeaders(Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json"), new() { ["Authorization"] = "ignored" });
            WriteHeaders(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), new() { ["Authorization"] = "ignored" });
            using var configuration = new ConfigurationManager();
            var startup = StartupConfiguration.Load(configuration, workspace.Resolve(environment: workspace.EnvironmentDirectory, explicitFile: explicitPath));
            Require(startup!.Headers.Values.Count == 1 && startup.Headers.Values["X-Explicit"] == "exact", "explicit selection ignores global/local maps");
            cases++;
        }

        foreach (var name in new[] { "Authorization", "authorization", "AuThOrIzAtIoN" })
        foreach (var value in new[] { "", "secret-reserved-value" })
        foreach (var overlay in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve(profile: "work");
            WriteHeaders(locations.GlobalConfigurationPath, overlay ? new() { ["X-Accepted"] = "yes" } : new() { [name] = value });
            if (overlay) WriteHeaders(locations.LocalConfigurationPath!, new() { [name] = value });
            using var configuration = new ConfigurationManager();
            var authenticated = false;
            try
            {
                var startup = StartupConfiguration.Load(configuration, locations)!;
                await startup.AuthenticateAsync(new TokenProvider(), (_, _, _) =>
                {
                    authenticated = true;
                    return Task.FromResult(authenticationResult);
                });
                throw new Exception("Reserved header unexpectedly accepted.");
            }
            catch (InvalidOperationException exception)
            {
                CheckDiagnostic(exception);
            }
            Require(!authenticated, "reserved header rejects before authentication delegate");
            try
            {
                new ServiceCollection().AddTokenInjectingProxy(configuration);
                throw new Exception("Proxy registration accepted reserved header.");
            }
            catch (InvalidOperationException exception)
            {
                CheckDiagnostic(exception);
            }
            Require(workspace.Files().All(path => path.EndsWith(".json")), "rejection creates no persistent cache files");
            cases++;
        }

        Console.WriteLine($"PASS: {cases} custom-header loading, validation, overlay, and startup-snapshot cases.");
    }

    private static void WriteHeaders(string path, Dictionary<string, string> headers) =>
        TestWorkspace.WriteJson(path, new { Headers = headers });

    private static void CheckDiagnostic(InvalidOperationException exception)
    {
        Require(exception.Message.Contains("Headers") && exception.Message.Contains("Authorization") && exception.Message.Contains("proxy manages"), "reserved-header diagnostic explains ownership");
        Require(!exception.Message.Contains("secret-reserved-value"), "diagnostic does not expose header values");
    }
}
