using System.Diagnostics;
using System.Reflection;
using System.Text;
using EntraAuthProxy;
using Microsoft.Identity.Client;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using static ConfigurationBehaviorChecks.TestAssertions;

namespace ConfigurationBehaviorChecks;

internal static class AccessTokenChecks
{
    private const string Missing = "<omitted>";
    private const string InitialToken = "synthetic.Header.Full-Startup-Access-Token.signature";
    private const string ChangedToken = "synthetic.Header.Full-Refresh-Access-Token.signature";

    public static async Task RunAsync()
    {
        var cases = 0;
        foreach (var (value, expected) in new (object?, bool)[]
        {
            (Missing, false), (null, false), (false, false), (true, true),
            ("false", false), ("true", true), (" TrUe ", true), (" FALSE ", false)
        })
        {
            using var workspace = new TestWorkspace();
            var path = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            WriteSettings(path, value);
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration, workspace.Resolve(explicitFile: path))!;
            Require(startup.PrintAccessToken == expected, "omitted/null/Boolean binding captured before authentication");
            await CheckPublicationAsync(startup, configuration, expected);
            cases++;
        }

        foreach (var environment in new[] { false, true })
        foreach (var profile in new string?[] { null, "work" })
        foreach (var overlay in new object?[] { Missing, null, false, true })
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve(profile, environment ? workspace.EnvironmentDirectory : null);
            WriteSettings(locations.GlobalConfigurationPath, true);
            WriteSettings(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), overlay);
            if (profile != null) WriteSettings(Path.Combine(locations.BaseDirectory, "entraauthproxy.json"), "ignored-invalid-base");
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration, locations)!;
            var expected = environment || Equals(overlay, Missing) || Equals(overlay, true);
            Require(startup.PrintAccessToken == expected, "profile/environment/default overlay precedence");
            await CheckPublicationAsync(startup, configuration, expected);
            cases++;
        }

        foreach (var environment in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var path = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            WriteSettings(path, true);
            WriteSettings(Path.Combine(workspace.DefaultBase, "entraauthproxy.json"), "ignored-invalid-global");
            WriteSettings(Path.Combine(workspace.EnvironmentDirectory, "entraauthproxy.json"), "ignored-invalid-environment");
            WriteSettings(Path.Combine(workspace.WorkingDirectory, "entraauthproxy.json"), "ignored-invalid-local");
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration,
                workspace.Resolve(environment: environment ? workspace.EnvironmentDirectory : null, explicitFile: path))!;
            Require(startup.PrintAccessToken, "explicit configuration ignores other sources");
            await CheckPublicationAsync(startup, configuration, true);
            cases++;
        }

        var nonScalars = new object[] { new { child = true }, new Dictionary<string, object>(), new[] { true }, Array.Empty<bool>() };
        foreach (var invalid in new object[] { "", " ", "yes", "1", "true,false", 0, 1, 1.5 }.Concat(nonScalars))
        foreach (var mode in new[] { "default", "profile", "overlay", "environment", "environment-profile", "explicit" })
        {
            using var workspace = new TestWorkspace();
            var explicitFile = mode == "explicit" ? Path.Combine(workspace.WorkingDirectory, "explicit.json") : null;
            var locations = workspace.Resolve(mode.Contains("profile") ? "work" : null,
                mode.StartsWith("environment") ? workspace.EnvironmentDirectory : null, explicitFile);
            var selectedFile = explicitFile ?? (mode == "overlay" ? locations.LocalConfigurationPath! : locations.GlobalConfigurationPath);
            if (mode == "overlay") WriteSettings(locations.GlobalConfigurationPath, true);
            WriteSettings(selectedFile, invalid);
            await AssertRejectedAsync(workspace, locations);
            cases++;
        }

        foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
        foreach (var lower in nonScalars)
        foreach (var overlay in new object?[] { Missing, null, false, true })
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve("work");
            WriteSettings(locations.GlobalConfigurationPath, lower, encoding);
            WriteSettings(locations.LocalConfigurationPath!, overlay, encoding);
            if (Equals(overlay, Missing))
            {
                await AssertRejectedAsync(workspace, locations);
            }
            else
            {
                using var configuration = new ConfigurationManager();
                var startup = workspace.LoadStartup(configuration, locations)!;
                Require(startup.PrintAccessToken == Equals(overlay, true), "higher scalar/null replaces lower non-scalar shape");
                await CheckPublicationAsync(startup, configuration, Equals(overlay, true));
            }
            cases++;
        }

        foreach (var overlay in nonScalars)
        {
            using var workspace = new TestWorkspace();
            var locations = workspace.Resolve("work");
            WriteSettings(locations.GlobalConfigurationPath, true);
            WriteSettings(locations.LocalConfigurationPath!, overlay);
            await AssertRejectedAsync(workspace, locations);
            cases++;
        }

        foreach (var mode in new[] { "profile-overlay", "environment", "explicit" })
        foreach (var initial in new[] { false, true })
        {
            using var workspace = new TestWorkspace();
            var explicitFile = mode == "explicit" ? Path.Combine(workspace.WorkingDirectory, "explicit.json") : null;
            var locations = workspace.Resolve(mode == "profile-overlay" ? "work" : null,
                mode == "environment" ? workspace.EnvironmentDirectory : null, explicitFile);
            var file = explicitFile ?? (mode == "profile-overlay" ? locations.LocalConfigurationPath! : locations.GlobalConfigurationPath);
            if (mode == "profile-overlay") WriteSettings(locations.GlobalConfigurationPath, !initial);
            WriteSettings(file, initial);
            using var configuration = new ConfigurationManager();
            var startup = workspace.LoadStartup(configuration, locations)!;
            using var output = new ConsoleOutputCapture();
            var provider = new TokenProvider(startup.PrintAccessToken);
            provider.SetToken(InitialToken);
            WriteSettings(file, !initial);
            await WaitForReloadAsync(configuration, (!initial).ToString());
            Require(startup.PrintAccessToken == initial, "flag is captured across watched JSON reload");
            provider.SetToken(ChangedToken);
            Require(output.Text == (initial ? Lines(InitialToken, ChangedToken) : ""), "existing provider preserves captured printing behavior");
            using var restartedConfiguration = new ConfigurationManager();
            var restarted = workspace.LoadStartup(restartedConfiguration, locations)!;
            Require(restarted.PrintAccessToken != initial, "restart applies changed flag");
            new TokenProvider(restarted.PrintAccessToken).SetToken("restart-token");
            Require(output.Text == (initial ? Lines(InitialToken, ChangedToken) : Lines("restart-token")), "new provider uses restarted flag");
            Require(workspace.Files().All(path => path.EndsWith(".json")), "reload/restart accesses no persistent cache");
            cases++;
        }

        foreach (var enabled in new[] { false, true })
        foreach (var level in new[] { "None", "Trace" })
        {
            using var environment = new LoggingEnvironmentScope(level, level);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            StartupLogging.CaptureEnvironment().Configure(builder.Logging);
            await using var app = builder.Build();
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EntraAuthProxy.TokenCheck");
            Require(logger.IsEnabled(LogLevel.Trace) == (level == "Trace"), "ordinary logging threshold is applied");
            using var output = new ConsoleOutputCapture();
            var provider = new TokenProvider(enabled);
            provider.SetToken(InitialToken);
            provider.SetToken(ChangedToken);
            Require(output.Text == (enabled ? Lines(InitialToken, ChangedToken) : ""), "stdout token printing is independent of logging threshold");
            cases++;
        }

        using (var output = new ConsoleOutputCapture())
        {
            var provider = new TokenProvider(true);
            Parallel.For(0, 200, _ => provider.SetToken(InitialToken));
            Require(provider.GetToken() == InitialToken && output.Text == Lines(InitialToken), "concurrent equal publications emit one complete token");
            cases++;
        }

        Console.WriteLine($"PASS: {cases} access-token binding, publication/refresh failures, effective shape/encoding/selection, logging independence, concurrency, and watched-reload/restart cases.");
    }

    private static async Task CheckPublicationAsync(StartupConfiguration startup, IConfiguration configuration, bool enabled)
    {
        using var output = new ConsoleOutputCapture();
        var provider = new TokenProvider(startup.PrintAccessToken);
        Require(provider.GetToken() == "", "initially no available token");
        provider.SetToken(null);
        provider.SetToken("");
        Require(output.Text == "", "missing and empty initial tokens are silent");

        try
        {
            await startup.AuthenticateAsync(provider, (_, _, _, _) => throw new IOException("synthetic startup acquisition failure"));
            throw new Exception("Unsuccessful startup acquisition unexpectedly returned.");
        }
        catch (IOException)
        {
            Require(provider.GetToken() == "" && output.Text == "", "unsuccessful startup acquisition produces no token/output");
        }

        var app = await startup.AuthenticateAsync(provider, (_, _, published, callback) =>
        {
            Require(ReferenceEquals(published, provider), "startup receives the same publication boundary");
            published.SetToken(InitialToken);
            return Task.FromResult(AuthExtensions.BuildClient(configuration, callback));
        });
        Require(provider.GetToken() == InitialToken, "startup token is available for forwarding");
        Require(output.Text == (enabled ? Lines(InitialToken) : ""), "startup emits the complete labeled token when enabled");


        using var refresh = new TokenRefreshService(app, provider, configuration);
        await refresh.RefreshTokenAsync(CancellationToken.None, _ => Task.FromResult<AuthenticationResult?>(Result(InitialToken)));
        Require(output.Text == (enabled ? Lines(InitialToken) : ""), "unchanged successful refresh is silent");
        await refresh.RefreshTokenAsync(CancellationToken.None, _ => Task.FromResult<AuthenticationResult?>(Result(ChangedToken)));
        await refresh.RefreshTokenAsync(CancellationToken.None, _ => Task.FromResult<AuthenticationResult?>(Result(ChangedToken.ToLowerInvariant())));
        await refresh.RefreshTokenAsync(CancellationToken.None, _ => Task.FromResult<AuthenticationResult?>(Result(InitialToken)));
        var expected = enabled ? Lines(InitialToken, ChangedToken, ChangedToken.ToLowerInvariant(), InitialToken) : "";
        Require(output.Text == expected, "changed refresh tokens use exact current-value comparison including case and reversion");

        // Exercise the normal acquisition branch with a strict no-account client.
        // A real MSAL GetAccountsAsync may perform authority metadata discovery;
        // this stand-in keeps the checks independent of network and credentials.
        var noAccountClient = DispatchProxy.Create<IPublicClientApplication, NoAccountClient>();
        using var noAccountRefresh = new TokenRefreshService(noAccountClient, provider, configuration);
        await noAccountRefresh.RefreshTokenAsync(CancellationToken.None);
        Require(((NoAccountClient)noAccountClient).AccountReads == 1 && ((NoAccountClient)noAccountClient).UnexpectedCalls.Count == 0,
            "no-account refresh queries accounts once and never acquires a token");
        await refresh.RefreshTokenAsync(CancellationToken.None, _ => Task.FromResult<AuthenticationResult?>(null));
        await refresh.RefreshTokenAsync(CancellationToken.None, _ => throw new MsalUiRequiredException("synthetic", "interaction required"));
        await refresh.RefreshTokenAsync(CancellationToken.None, _ => throw new IOException("synthetic transient failure"));
        Require(provider.GetToken() == InitialToken, "no-account/missing/failed refresh retains the published token");
        for (var index = 0; index < 20; index++) Require(provider.GetToken() == InitialToken, "reads retain token availability");
        Require(output.Text == expected, "failed/missing acquisition and token reads produce no output");
        provider.SetToken(null);
        Require(provider.GetToken() == InitialToken && output.Text == expected, "null publication is ignored");
        provider.SetToken("");
        Require(provider.GetToken() == "" && output.Text == expected, "empty publication clears storage without output");
    }

    private static AuthenticationResult Result(string token) => new(token, false, "synthetic-user",
        DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1), "synthetic-tenant", null, null,
        ["api://checks/.default"], Guid.Empty);

    private static string Lines(params string[] tokens) => string.Concat(tokens.Select(token => "Access token: " + token + Environment.NewLine));

    private static void WriteSettings(string path, object? flag, Encoding? encoding = null)
    {
        var settings = new Dictionary<string, object?>
        {
            ["EntraAuth"] = new
            {
                ClientId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222", TargetScope = "api://checks/.default"
            }
        };
        if (!Equals(flag, Missing)) settings["PrintAccessToken"] = flag;
        TestWorkspace.WriteJson(path, settings, encoding);
    }

    private static async Task AssertRejectedAsync(TestWorkspace workspace, ConfigurationLocations locations)
    {
        using var configuration = new ConfigurationManager();
        using var output = new ConsoleOutputCapture();
        var authenticated = false;
        try
        {
            var startup = workspace.LoadStartup(configuration, locations)!;
            await startup.AuthenticateAsync(new TokenProvider(), (_, _, _, _) =>
            {
                authenticated = true;
                throw new Exception("Invalid PrintAccessToken reached authentication.");
            });
            throw new Exception("Invalid PrintAccessToken accepted.");
        }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains("PrintAccessToken") && exception.Message.Contains("Boolean"), "Boolean diagnostic names setting and accepted type");
        }
        Require(!authenticated && output.Text == "", "Boolean validation precedes authentication/cache access and token output");
        Require(workspace.Files().All(path => path.EndsWith(".json")), "invalid Boolean creates no persistent cache files");
    }

    private static async Task WaitForReloadAsync(IConfiguration configuration, string value)
    {
        var timeout = Stopwatch.StartNew();
        while (!string.Equals(configuration["PrintAccessToken"], value, StringComparison.OrdinalIgnoreCase))
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(10), "JSON watcher reloads token-printing flag");
            await Task.Delay(50);
        }
    }
}
