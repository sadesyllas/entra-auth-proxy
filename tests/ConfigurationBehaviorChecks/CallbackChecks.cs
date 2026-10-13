using System.Diagnostics;
using System.Reflection;
using System.Text;
using EntraAuthProxy;
using Microsoft.Identity.Client;
using static ConfigurationBehaviorChecks.TestAssertions;

namespace ConfigurationBehaviorChecks;

internal static class CallbackChecks
{
    public static async Task RunAsync()
    {
        var cases = 0;
        var defaultUri = PublicClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111")
            .WithDefaultRedirectUri().Build().AppConfig.RedirectUri;
        foreach (var path in new[] { Missing, null, "/" })
        foreach (var (redirectPort, port, expectedAuthority) in new (string?, string?, string)[]
        {
            ("5100", "5200", "http://localhost:5100"),
            (null, "5200", "http://localhost:5200"),
            (null, null, "http://localhost"),
            ("", "5200", "http://localhost"),
            ("not-a-port", "5200", "http://localhost"),
            (null, "", "http://localhost"),
            (null, "not-a-port", "http://localhost"),
            (" 5300 ", "5200", "http://localhost:5300"),
            (Missing, Missing, "http://localhost"),
            (Missing, "5200", "http://localhost:5200"),
            ("0", "5200", "http://localhost:0"),
            ("80", "5200", "http://localhost:80"),
            (null, "0", "http://localhost:0"),
            (null, "80", "http://localhost:80")
        })
        {
            using var configuration = CreateConfiguration(path, redirectPort, port);
            var callback = CallbackSettings.Capture(configuration);
            var app = AuthExtensions.BuildClient(configuration, callback);
            var expectedPath = path is null or Missing ? "/" : path;
            AssertClient(app, expectedAuthority + expectedPath);
            if (expectedAuthority == "http://localhost" && expectedPath == "/")
                Require(app.AppConfig.RedirectUri == defaultUri, "root fallback retains the exact MSAL default");
            Require(callback.Path == expectedPath, "captured path preserves supplied case, escaping, and trailing slash");
            var configured = new Uri(app.AppConfig.RedirectUri);
            var interactive = TransformInteractiveRedirect(configured);
            Require(interactive.AbsolutePath == "/", "root interactive callback retains the root path");
            if (configured.Port is 0 or 80)
                Require(interactive.Port is > 0 and not 80, "root callback retains MSAL automatic-port selection");
            else
                Require(interactive.AbsoluteUri == configured.AbsoluteUri, "root fixed port remains unchanged by MSAL");
            cases++;
        }

        foreach (var path in new[] { "/auth/callback", "/Auth/Callback/", "/auth%2Fcallback" })
        foreach (var (redirectPort, port, expectedPort) in new (string?, string?, int)[]
        {
            ("5000", "5200", 5000), ("5100", "5200", 5100), (null, "5200", 5200), (Missing, "5200", 5200),
            (" 5300 ", "5200", 5300), ("1", "5200", 1), ("65535", "5200", 65535),
            (null, "1", 1), (Missing, "65535", 65535)
        })
        {
            using var configuration = CreateConfiguration(path, redirectPort, port);
            var callback = CallbackSettings.Capture(configuration);
            Require(callback.Path == path, "custom path snapshot preserves case, escaping, and trailing slash");
            AssertClient(AuthExtensions.BuildClient(configuration, callback), $"http://localhost:{expectedPort}{path}");
            cases++;
        }

        // Keep selection cases explicit: an invalid RedirectPort must not use a
        // valid Port, whereas omission/null permits Port fallback.
        var invalidPorts = new (string? RedirectPort, string? Port, string Setting)[]
        {
            (Missing, Missing, "RedirectPort or Port"), (null, null, "RedirectPort or Port"),
            ("", "5200", "RedirectPort"), ("not-a-port", "5200", "RedirectPort"),
            ("-1", "5200", "RedirectPort"), ("65536", "5200", "RedirectPort"),
            ("0", "5200", "RedirectPort"), ("80", "5200", "RedirectPort"),
            ("2147483648", "5200", "RedirectPort"),
            (Missing, "", "Port"), (null, "not-a-port", "Port"), (null, "-1", "Port"),
            (null, "65536", "Port"), (null, "0", "Port"), (null, "80", "Port"),
            (Missing, "2147483648", "Port")
        };
        foreach (var (redirectPort, port, setting) in invalidPorts)
        foreach (var mode in new[] { "default", "profile", "overlay", "environment", "environment-profile", "explicit" })
        {
            using var workspace = new TestWorkspace();
            var explicitFile = mode == "explicit" ? Path.Combine(workspace.WorkingDirectory, "explicit.json") : null;
            var locations = workspace.Resolve(mode.Contains("profile") ? "work" : null,
                mode.StartsWith("environment") ? workspace.EnvironmentDirectory : null, explicitFile);
            var selectedFile = explicitFile ?? (mode == "overlay" ? locations.LocalConfigurationPath! : locations.GlobalConfigurationPath);
            if (mode == "overlay") WriteSettings(locations.GlobalConfigurationPath, "/");
            WriteSettings(selectedFile, "/Auth/Callback/", redirectPort, port);
            using var configuration = new ConfigurationManager();
            var authenticated = false;
            try
            {
                var startup = workspace.LoadStartup(configuration, locations)!;
                await startup.AuthenticateAsync(new TokenProvider(), (_, _, _, _) =>
                {
                    authenticated = true;
                    throw new Exception("Invalid custom-path port reached authentication.");
                });
                throw new Exception("Invalid custom-path port was accepted.");
            }
            catch (InvalidOperationException exception)
            {
                Require(exception.Message.Contains("RedirectPath") && exception.Message.Contains("from " + setting + ":"),
                    "custom-path port diagnostic identifies the selected setting or both missing settings");
                Require(exception.Message.Contains("1 through 65535") && exception.Message.Contains("excluding 80"),
                    "custom-path port diagnostic explains the valid fixed range");
            }
            Require(!authenticated, "custom-path port rejection precedes authentication and persistent cache access");
            Require(workspace.Files().All(path => path.EndsWith(".json")), "invalid custom-path port creates no token cache files");
            cases++;
        }

        foreach (var environment in new[] { false, true })
        foreach (var profile in new string?[] { null, "work" })
        foreach (var overlay in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve(profile, environment ? workspace.EnvironmentDirectory : null);
            WriteSettings(locations.GlobalConfigurationPath, "/Global/Callback/", "5100", "5200");
            if (profile != null)
                WriteSettings(Path.Combine(locations.BaseDirectory, "entraauthproxy.json"), "invalid-ignored-base");
            if (overlay)
                WriteSettings(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), "/Local/Callback/", "5300", "5400");
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration, locations)!;
            var useLocal = overlay && !environment;
            await AssertAuthenticationAsync(startup, configuration,
                useLocal ? "http://localhost:5300/Local/Callback/" : "http://localhost:5100/Global/Callback/");
            cases++;
        }

        foreach (var environment in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var path = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            WriteSettings(path, "/Explicit/Callback/", "5100");
            WriteSettings(Path.Combine(workspace.DefaultBase, "entraauthproxy.json"), "invalid-ignored-global");
            WriteSettings(Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json"), "invalid-ignored-environment");
            WriteSettings(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), "invalid-ignored-local");
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration,
                workspace.Resolve(environment: environment ? workspace.EnvironmentDirectory : null, explicitFile: path))!;
            await AssertAuthenticationAsync(startup, configuration, "http://localhost:5100/Explicit/Callback/");
            cases++;
        }

        foreach (var pathValue in new object?[] { Missing, null, "/", "/Nested/Callback/" })
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve("work");
            WriteSettings(locations.GlobalConfigurationPath, "/Global", "5100");
            WriteSettings(locations.LocalConfigurationPath!, pathValue, null, "5200");
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration, locations)!;
            await AssertAuthenticationAsync(startup, configuration,
                "http://localhost:5200" + (pathValue is null ? "/" : Equals(pathValue, Missing) ? "/Global" : pathValue));
            cases++;
        }

        var nonScalarPaths = new object[]
        {
            new { child = "/ignored" }, new Dictionary<string, object>(),
            new[] { "/ignored" }, Array.Empty<string>()
        };
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            // Exercise production loading and client construction, not a separate
            // decoder. Both UTF-16 byte orders and UTF-8 BOM must retain the
            // standard JSON provider's supported configuration encodings.
            foreach (var path in new object?[] { Missing, null, "/", "/Encoded/Callback/" })
            {
                using var workspace = new TestWorkspace();
                var file = Path.Combine(workspace.WorkingDirectory, "explicit.json");
                WriteSettings(file, path, "5100", encoding: encoding);
                using var configuration = new ConfigurationManager();
                var startup = workspace.LoadStartup(configuration, workspace.Resolve(explicitFile: file))!;
                await AssertAuthenticationAsync(startup, configuration,
                    "http://localhost:5100" + (path is null or Missing ? "/" : path));
                Require(workspace.Files().SequenceEqual(new[] { file }), "encoded callback loading accesses no persistent cache");
                cases++;
            }

            foreach (var profile in new string?[] { null, "work" })
            foreach (var localPath in new object?[] { Missing, null, "/", "/Encoded/Local/" })
            {
                using var workspace = new TestWorkspace();
                var locations = workspace.Resolve(profile);
                WriteSettings(locations.GlobalConfigurationPath, new Dictionary<string, object>(), "5100", encoding: encoding);
                WriteSettings(locations.LocalConfigurationPath!, localPath, null, "5200", encoding);
                if (Equals(localPath, Missing))
                {
                    await AssertPathRejectedAsync(workspace, locations);
                }
                else
                {
                    using var configuration = new ConfigurationManager();
                    var startup = workspace.LoadStartup(configuration, locations)!;
                    await AssertAuthenticationAsync(startup, configuration, "http://localhost:5200" + (localPath ?? "/"));
                    Require(workspace.Files().All(path => path.EndsWith(".json")), "encoded overlays access no persistent cache");
                }
                cases++;
            }

            foreach (var nonScalarPath in nonScalarPaths)
            {
                using var workspace = new TestWorkspace();
                var locations = workspace.Resolve();
                WriteSettings(locations.GlobalConfigurationPath, "/Global/", "5100");
                WriteSettings(locations.LocalConfigurationPath!, nonScalarPath, encoding: encoding);
                await AssertPathRejectedAsync(workspace, locations);
                cases++;
            }
        }

        foreach (var profile in new string?[] { null, "work" })
        foreach (var lowerPath in nonScalarPaths)
        foreach (var localPath in new object?[] { Missing, null, "/", "/Local/Callback/" })
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve(profile);
            WriteSettings(locations.GlobalConfigurationPath, lowerPath, "5100");
            WriteSettings(locations.LocalConfigurationPath!, localPath, null, "5200");
            if (Equals(localPath, Missing))
            {
                await AssertPathRejectedAsync(workspace, locations);
            }
            else
            {
                using var configuration = new ConfigurationManager();
                var startup = workspace.LoadStartup(configuration, locations)!;
                await AssertAuthenticationAsync(startup, configuration, "http://localhost:5200" + (localPath ?? "/"));
                Require(workspace.Files().All(path => path.EndsWith(".json")), "valid shape overrides access no persistent cache");
            }
            cases++;
        }

        foreach (var profile in new string?[] { null, "work" })
        foreach (var localPath in nonScalarPaths)
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve(profile);
            WriteSettings(locations.GlobalConfigurationPath, "/Global/", "5100");
            WriteSettings(locations.LocalConfigurationPath!, localPath);
            await AssertPathRejectedAsync(workspace, locations);
            cases++;
        }

        foreach (var invalid in new object[]
        {
            "", "auth/callback", "http://localhost/auth", "https://example.com/auth", "//example.com/auth",
            "/auth?x=1", "/auth#fragment", "/auth\\callback", "/auth callback", " /auth", "/auth\tcallback",
            "/auth\r\ncallback", "/auth\0callback", "/auth\u00a0callback", "/auth\u007fcallback",
            "/a/../callback", "/./callback", "/%2e/callback", "/auth%", "/auth%zz", "/café",
            12, true, new { child = "/callback" }, new Dictionary<string, object>(), new[] { "/callback" }, Array.Empty<string>()
        })
        foreach (var overlay in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve("work");
            WriteSettings(locations.GlobalConfigurationPath, overlay ? "/valid" : invalid, "5100");
            if (overlay) WriteSettings(locations.LocalConfigurationPath!, invalid);
            await AssertPathRejectedAsync(workspace, locations);
            cases++;
        }

        using (var workspace = new TestWorkspace())
        {
            var locations = workspace.Resolve("work");
            WriteSettings(locations.GlobalConfigurationPath, new { child = "/ignored" }, "5100");
            WriteSettings(locations.LocalConfigurationPath!, "/Startup/", "5300");
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration, locations)!;
            WriteSettings(locations.LocalConfigurationPath!, null, "0");
            await WaitForCallbackReloadAsync(configuration, null, "0");
            await AssertAuthenticationAsync(startup, configuration, "http://localhost:5300/Startup/");
            using var restartedConfiguration = new ConfigurationManager();
            var restarted = workspace.LoadStartup(restartedConfiguration, locations)!;
            await AssertAuthenticationAsync(restarted, restartedConfiguration, "http://localhost:0/");
            WriteSettings(locations.LocalConfigurationPath!, Missing, "80");
            await WaitForCallbackReloadAsync(configuration, null, "80");
            await AssertAuthenticationAsync(startup, configuration, "http://localhost:5300/Startup/");
            await AssertPathRejectedAsync(workspace, locations);
            cases++;
        }

        using (var workspace = new TestWorkspace())
        {
            var path = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            var locations = workspace.Resolve(explicitFile: path);
            WriteSettings(path, "/Startup/", "5100");
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration, locations)!;
            WriteSettings(path, "/Restart/", "5200");
            await WaitForCallbackReloadAsync(configuration, "/Restart/", "5200");
            await AssertAuthenticationAsync(startup, configuration, "http://localhost:5100/Startup/");
            using var restartedConfiguration = new ConfigurationManager();
            var restarted = workspace.LoadStartup(restartedConfiguration, locations)!;
            await AssertAuthenticationAsync(restarted, restartedConfiguration, "http://localhost:5200/Restart/");
            Require(workspace.Files().SequenceEqual(new[] { path }), "callback checks access no persistent cache or real user files");
            cases++;
        }

        Console.WriteLine($"PASS: {cases} callback path, fixed/automatic selected-port, actual MSAL construction/interactive transformation, BOM encoding, scalar/shape overlay, early-rejection, and watched-reload/restart cases.");
    }

    private const string Missing = "<omitted>";

    private static ConfigurationManager CreateConfiguration(string? path, string? redirectPort, string? port)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntraAuth:ClientId"] = "11111111-1111-1111-1111-111111111111",
            ["EntraAuth:TenantId"] = "22222222-2222-2222-2222-222222222222",
            ["EntraAuth:TargetScope"] = "api://checks/.default"
        });
        if (path != Missing) configuration["RedirectPath"] = path;
        if (redirectPort != Missing) configuration["RedirectPort"] = redirectPort;
        if (port != Missing) configuration["Port"] = port;
        return configuration;
    }

    private static void WriteSettings(string path, object? redirectPath, string? redirectPort = Missing, string? port = Missing,
        Encoding? encoding = null)
    {
        var settings = new Dictionary<string, object?>
        {
            ["EntraAuth"] = new
            {
                ClientId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222", TargetScope = "api://checks/.default"
            }
        };
        if (!Equals(redirectPath, Missing)) settings["RedirectPath"] = redirectPath;
        if (redirectPort != Missing) settings["RedirectPort"] = redirectPort;
        if (port != Missing) settings["Port"] = port;
        TestWorkspace.WriteJson(path, settings, encoding);
    }

    private static async Task AssertAuthenticationAsync(StartupConfiguration startup, IConfiguration configuration, string expectedUri)
    {
        var called = false;
        var result = await startup.AuthenticateAsync(new TokenProvider(), (_, loaded, _, callback) =>
        {
            called = true;
            Require(ReferenceEquals(loaded, configuration), "authentication uses the production configuration");
            Require(ReferenceEquals(callback, startup.Callback), "authentication uses the startup callback snapshot");
            return Task.FromResult(AuthExtensions.BuildClient(loaded, callback));
        });
        Require(called, "valid callback reaches the authentication stand-in");
        AssertClient(result, expectedUri);
    }

    private static async Task AssertPathRejectedAsync(TestWorkspace workspace, ConfigurationLocations locations)
    {
        using var configuration = new ConfigurationManager();
        var authenticated = false;
        try
        {
            var startup = workspace.LoadStartup(configuration, locations)!;
            await startup.AuthenticateAsync(new TokenProvider(), (_, _, _, _) =>
            {
                authenticated = true;
                throw new Exception("Invalid RedirectPath reached authentication.");
            });
            throw new Exception("Invalid RedirectPath was accepted.");
        }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains("RedirectPath"), "path validation names its setting");
        }
        Require(!authenticated, "path rejection precedes authentication and persistent cache access");
        Require(workspace.Files().All(path => path.EndsWith(".json")), "invalid path creates no token cache files");
    }

    private static async Task WaitForCallbackReloadAsync(IConfiguration configuration, string? path, string port)
    {
        var timeout = Stopwatch.StartNew();
        while (configuration["RedirectPath"] != path || configuration["RedirectPort"] != port)
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(10), "callback JSON watcher reloads edited path and port");
            await Task.Delay(50);
        }
    }

    private static void AssertClient(IPublicClientApplication app, string expectedUri)
    {
        Require(new Uri(app.AppConfig.RedirectUri).AbsoluteUri == new Uri(expectedUri).AbsoluteUri,
            $"actual MSAL redirect {app.AppConfig.RedirectUri} equals {expectedUri}");
        if (new Uri(expectedUri).AbsolutePath != "/")
            Require(TransformInteractiveRedirect(new Uri(app.AppConfig.RedirectUri)).AbsoluteUri == new Uri(expectedUri).AbsoluteUri,
                "installed MSAL interactive redirect preserves the exact custom path and fixed port");
    }

    // The pinned MSAL calls this private method from UpdateRedirectUri immediately
    // before interactive acquisition. Probe the installed implementation without
    // constructing browser UI, acquiring tokens, or accessing persistent caches.
    // Reflection is intentional test-only coupling to detect dependency behavior
    // changes; a missing method fails verification rather than bypassing the check.
    private static readonly MethodInfo InteractiveRedirectTransformation = typeof(PublicClientApplicationBuilder).Assembly
        .GetType("Microsoft.Identity.Client.Platforms.Shared.Desktop.OsBrowser.DefaultOsBrowserWebUi", throwOnError: true)!
        .GetMethod("FindFreeLocalhostRedirectUri", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Uri)])
        ?? throw new InvalidOperationException("Installed MSAL interactive redirect transformation is unavailable.");

    private static Uri TransformInteractiveRedirect(Uri configured) =>
        (Uri)InteractiveRedirectTransformation.Invoke(null, [configured])!;
}
