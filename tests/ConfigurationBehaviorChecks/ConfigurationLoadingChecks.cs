using EntraAuthProxy;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Identity.Client;
using static ConfigurationBehaviorChecks.TestAssertions;

namespace ConfigurationBehaviorChecks;

internal static class ConfigurationLoadingChecks
{
    public static async Task RunAsync()
    {
        var cases = 0;
        // Constructing a client does not sign in, register a persistent cache, or
        // access credential storage. It only provides a return marker for the seam.
        var authenticationResult = PublicClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111").Build();

        foreach (var environment in new string?[] { null, "", " \t\r\n" })
        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            SeedDistinctFiles(workspace);
            var globalMarker = profile == null ? "default-base" : "default-profile";
            var globalPath = profile == null
                ? Path.Combine(workspace.DefaultBase, "entraauthproxy.json")
                : Path.Combine(workspace.DefaultBase, "work", "entraauthproxy.json");
            await CheckLoadedAsync(workspace, workspace.Resolve(profile, environment), workspace.DefaultBase,
                [globalPath, Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json")],
                new Dictionary<string, string?>
                {
                    ["Marker"] = "local",
                    ["EntraAuth:TenantId"] = globalMarker + "-tenant",
                    ["EntraAuth:ClientId"] = "local-client",
                    ["EntraAuth:TargetScope"] = globalMarker + "-scope",
                    ["Port"] = "8100",
                    ["BaseOnly"] = profile == null ? "default-base" : null,
                    ["ProfileOnly"] = profile == null ? null : "default-profile",
                    ["LocalOnly"] = "local",
                    ["ExplicitOnly"] = null
                }, authenticationResult);
            cases++;
        }

        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            SeedDistinctFiles(workspace);
            var globalMarker = profile == null ? "environment-base" : "environment-profile";
            var globalPath = profile == null
                ? Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json")
                : Path.Combine(workspace.EnvironmentDirectory, "work", "entraauthproxy.json");
            await CheckLoadedAsync(workspace, workspace.Resolve(profile, workspace.EnvironmentDirectory), workspace.EnvironmentDirectory,
                [globalPath],
                new Dictionary<string, string?>
                {
                    ["Marker"] = globalMarker,
                    ["EntraAuth:TenantId"] = globalMarker + "-tenant",
                    ["EntraAuth:ClientId"] = globalMarker + "-client",
                    ["Port"] = "8000",
                    ["BaseOnly"] = profile == null ? "environment-base" : null,
                    ["ProfileOnly"] = profile == null ? null : "environment-profile",
                    ["LocalOnly"] = null,
                    ["ExplicitOnly"] = null
                }, authenticationResult);
            cases++;
        }

        foreach (var useEnvironment in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            SeedDistinctFiles(workspace);
            var explicitPath = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            await CheckLoadedAsync(workspace,
                workspace.Resolve(environment: useEnvironment ? workspace.EnvironmentDirectory : null, explicitFile: explicitPath),
                useEnvironment ? workspace.EnvironmentDirectory : workspace.DefaultBase,
                [explicitPath],
                new Dictionary<string, string?>
                {
                    ["Marker"] = "explicit",
                    ["EntraAuth:TenantId"] = "explicit-tenant",
                    ["EntraAuth:ClientId"] = "explicit-client",
                    ["BaseOnly"] = null,
                    ["ProfileOnly"] = null,
                    ["LocalOnly"] = null,
                    ["ExplicitOnly"] = "explicit"
                }, authenticationResult);
            cases++;
        }

        foreach (var environment in new string?[] { null, "", " \t\r\n" })
        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            var localPath = Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json");
            WriteGlobal(localPath, "local-only", "LocalOnly");
            if (profile != null)
                WriteGlobal(Path.Combine(workspace.DefaultBase, "entraauthproxy.json"), "ignored-base", "BaseOnly");
            await CheckLoadedAsync(workspace, workspace.Resolve(profile, environment), workspace.DefaultBase,
                [localPath], new Dictionary<string, string?>
                {
                    ["Marker"] = "local-only",
                    ["EntraAuth:TenantId"] = "local-only-tenant",
                    ["LocalOnly"] = "local-only",
                    ["BaseOnly"] = null
                }, authenticationResult);
            Require(!Directory.Exists(Path.Combine(workspace.DefaultBase, "work")), "local-only lookup does not create a profile folder or starter");
            cases++;
        }

        foreach (var useEnvironment in new[] { false, true })
        foreach (var profile in new string?[] { null, "work" })
        {
            using var workspace = new TestWorkspace();
            var expectedBase = useEnvironment ? workspace.EnvironmentDirectory : workspace.DefaultBase;
            var expectedStarter = profile == null
                ? Path.Combine(expectedBase, "entraauthproxy.json")
                : Path.Combine(expectedBase, "work", "entraauthproxy.json");
            // An unrelated base JSON must not supply a missing profile. An eligible
            // local JSON is covered above; under an environment base it is ignored.
            if (profile != null) WriteGlobal(Path.Combine(expectedBase, "entraauthproxy.json"), "ignored-base", "BaseOnly");
            if (useEnvironment) WriteGlobal(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), "ignored-local", "LocalOnly");
            var before = workspace.Files();
            using var configuration = new ConfigurationManager();
            var startup = StartupConfiguration.Load(configuration, workspace.Resolve(profile, useEnvironment ? expectedBase : null));
            Require(startup == null, "starter generation tells startup to exit before authentication");
            Require(File.Exists(expectedStarter), "starter is written to the selected global location");
            Require(workspace.Files().SequenceEqual(before.Append(expectedStarter).Order()), "starter generation writes only the selected JSON");
            Require(configuration.Sources.Count == 0, "starter exit does not load unrelated JSON");
            cases++;
        }

        using (var workspace = new TestWorkspace())
        {
            var path = Path.Combine(workspace.EnvironmentDirectory, "work", "entraauthproxy.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not JSON");
            using var configuration = new ConfigurationManager();
            try
            {
                StartupConfiguration.Load(configuration, workspace.Resolve("work", workspace.EnvironmentDirectory));
                throw new InvalidOperationException("Malformed selected JSON unexpectedly loaded.");
            }
            catch (InvalidDataException)
            {
                Require(File.ReadAllText(path) == "not JSON", "invalid JSON is not replaced with a starter");
            }
            cases++;
        }

        Console.WriteLine($"PASS: {cases} production loading, overlay, starter, and authentication-boundary cases without sign-in or persistent cache access.");
    }

    private static async Task CheckLoadedAsync(
        TestWorkspace workspace,
        ConfigurationLocations locations,
        string expectedCacheDirectory,
        string[] expectedSources,
        Dictionary<string, string?> expectedSettings,
        IPublicClientApplication authenticationResult)
    {
        var before = workspace.Files();
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["HostDefault"] = "must-clear" });
        var startup = StartupConfiguration.Load(configuration, locations);
        Require(startup != null, "existing eligible JSON prevents starter exit");
        Require(configuration["HostDefault"] == null, "standard host configuration sources are cleared");
        foreach (var setting in expectedSettings)
            Require(configuration[setting.Key] == setting.Value, $"effective setting {setting.Key}, expected {setting.Value ?? "absent"}");

        var sources = configuration.Sources.Cast<JsonConfigurationSource>().ToArray();
        Require(sources.Length == expectedSources.Length, "only selected configuration sources are loaded");
        for (var index = 0; index < sources.Length; index++)
        {
            Require(!sources[index].Optional && sources[index].ReloadOnChange, "required JSON keeps reload watching enabled");
            Require(sources[index].FileProvider!.GetFileInfo(sources[index].Path!).PhysicalPath == expectedSources[index], "JSON source path and ordering");
        }

        var tokens = new TokenProvider();
        var called = false;
        var result = await startup!.AuthenticateAsync(tokens, (cacheDirectory, loadedConfiguration, tokenProvider) =>
        {
            called = true;
            Require(cacheDirectory == expectedCacheDirectory, "authentication receives the base directory, independent of profile or explicit JSON");
            Require(ReferenceEquals(loadedConfiguration, configuration), "authentication receives the loaded production configuration");
            Require(ReferenceEquals(tokenProvider, tokens), "authentication receives the startup token provider");
            return Task.FromResult(authenticationResult);
        });
        Require(called && ReferenceEquals(result, authenticationResult), "startup invokes and returns the authentication boundary");
        Require(workspace.Files().SequenceEqual(before), "loading and the test authentication boundary do not write configuration or token caches");
    }

    private static void SeedDistinctFiles(TestWorkspace workspace)
    {
        WriteGlobal(Path.Combine(workspace.DefaultBase, "entraauthproxy.json"), "default-base", "BaseOnly");
        WriteGlobal(Path.Combine(workspace.DefaultBase, "work", "entraauthproxy.json"), "default-profile", "ProfileOnly");
        WriteGlobal(Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json"), "environment-base", "BaseOnly");
        WriteGlobal(Path.Combine(workspace.EnvironmentDirectory, "work", "entraauthproxy.json"), "environment-profile", "ProfileOnly");
        WriteGlobal(Path.Combine(workspace.WorkingDirectory, "explicit.json"), "explicit", "ExplicitOnly");
        TestWorkspace.WriteJson(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), new
        {
            Marker = "local",
            EntraAuth = new { ClientId = "local-client" },
            Port = 8100,
            LocalOnly = "local"
        });
    }

    private static void WriteGlobal(string path, string marker, string uniqueKey) =>
        TestWorkspace.WriteJson(path, new Dictionary<string, object>
        {
            ["Marker"] = marker,
            ["EntraAuth"] = new { TenantId = marker + "-tenant", ClientId = marker + "-client", TargetScope = marker + "-scope" },
            ["Port"] = 8000,
            [uniqueKey] = marker
        });
}
