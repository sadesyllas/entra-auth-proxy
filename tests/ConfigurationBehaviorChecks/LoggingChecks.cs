using EntraAuthProxy;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using static ConfigurationBehaviorChecks.TestAssertions;

namespace ConfigurationBehaviorChecks;

internal static class LoggingChecks
{
    private static readonly LogLevel[] EmittedLevels =
        [LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical];

    public static async Task RunAsync()
    {
        var cases = 0;
        foreach (var defaultValue in new string?[] { null, "", " \t\r\n" })
        foreach (var aspNetCoreValue in new string?[] { null, "", " \t\r\n" })
        {
            await CheckLevelsAsync(defaultValue, aspNetCoreValue, LogLevel.Information, LogLevel.Warning);
            cases++;
        }

        foreach (var level in Enum.GetValues<LogLevel>())
        {
            await CheckLevelsAsync(level.ToString(), null, level, LogLevel.Warning);
            await CheckLevelsAsync(null, level.ToString(), LogLevel.Information, level);
            await CheckLevelsAsync(" \t" + level.ToString().ToUpperInvariant() + "\r\n", level.ToString().ToLowerInvariant(), level, level);
            cases += 3;
        }
        await CheckLevelsAsync(" eRrOr ", "\tdEbUg\r\n", LogLevel.Error, LogLevel.Debug);
        await CheckLevelsAsync("None", "Debug", LogLevel.None, LogLevel.Debug);
        await CheckLevelsAsync("Debug", "None", LogLevel.Debug, LogLevel.None);
        cases += 3;

        foreach (var invalid in new[] { "Verbose", "1", "-1", "7", "999", "Debug, Warning", "Trace, Debug", "Information|Error", "Default", "warn", "Debug extra" })
        foreach (var invalidDefault in new[] { false, true })
        {
            using var environment = new LoggingEnvironmentScope(invalidDefault ? invalid : "Debug", invalidDefault ? "Error" : invalid);
            using var workspace = new TestWorkspace();
            var path = Path.Combine(workspace.WorkingDirectory, "explicit.json");
            TestWorkspace.WriteJson(path, new { Port = 5000 });
            using var configuration = new ConfigurationManager();
            var authenticated = false;
            try
            {
                var startup = StartupConfiguration.Load(configuration, workspace.Resolve(explicitFile: path))!;
                await startup.AuthenticateAsync(new TokenProvider(), (_, _, _) =>
                {
                    authenticated = true;
                    throw new Exception("Invalid logging reached authentication.");
                });
                throw new Exception("Invalid logging was accepted.");
            }
            catch (InvalidOperationException exception)
            {
                var variable = invalidDefault ? StartupLogging.DefaultLevelVariable : StartupLogging.AspNetCoreLevelVariable;
                Require(exception.Message.Contains(variable), "invalid logging diagnostic names its variable");
                foreach (var name in Enum.GetNames<LogLevel>())
                    Require(exception.Message.Contains(name), "invalid logging diagnostic lists " + name);
            }
            Require(!authenticated, "invalid logging rejects before authentication and cache access");
            Require(workspace.Files().SequenceEqual(new[] { path }), "invalid logging writes no persistent cache or starter");
            cases++;
        }

        Console.WriteLine($"PASS: {cases} startup logging resolution, filtering, captured-event, console-format, and pre-authentication validation cases.");
    }

    private static async Task CheckLevelsAsync(string? defaultValue, string? aspNetCoreValue,
        LogLevel expectedDefault, LogLevel expectedAspNetCore)
    {
        using var environment = new LoggingEnvironmentScope(defaultValue, aspNetCoreValue);
        var captured = StartupLogging.CaptureEnvironment();
        Require(captured.DefaultLevel == expectedDefault && captured.AspNetCoreLevel == expectedAspNetCore,
            "independent startup logging resolution");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        captured.Configure(builder.Logging);
        await using var app = builder.Build();
        Require(app.Services.GetServices<ILoggerProvider>().Single() is ConsoleLoggerProvider, "only the existing console provider is retained");
        var options = app.Services.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;
        Require(options.TimestampFormat == "yyyy-MM-dd HH:mm:ss " && options.UseUtcTimestamp && options.SingleLine,
            "simple console retains single-line UTC timestamp format");

        using var provider = new CapturingLoggerProvider();
        var factory = app.Services.GetRequiredService<ILoggerFactory>();
        factory.AddProvider(provider);
        AssertFiltering(factory, provider, expectedDefault, expectedAspNetCore);
    }

    public static void AssertFiltering(ILoggerFactory factory, CapturingLoggerProvider provider,
        LogLevel expectedDefault, LogLevel expectedAspNetCore)
    {
        foreach (var (category, threshold) in new[]
        {
            ("EntraAuthProxy.LoggingCheck", expectedDefault),
            ("Microsoft.AspNetCore", expectedAspNetCore),
            ("Microsoft.AspNetCore.Hosting.Diagnostics", expectedAspNetCore)
        })
        {
            AssertCategoryFiltering(factory, provider, category, threshold);
        }
    }

    public static void AssertCategoryFiltering(ILoggerFactory factory, CapturingLoggerProvider provider,
        string category, LogLevel threshold)
    {
        provider.Events.Clear();
        var logger = factory.CreateLogger(category);
        foreach (var level in EmittedLevels)
        {
            Require(logger.IsEnabled(level) == (level >= threshold), $"{category} IsEnabled({level}) at {threshold}");
            logger.Log(level, "event-{Level}", level);
        }
        Require(!logger.IsEnabled(LogLevel.None), category + " never enables None events");
        logger.Log(LogLevel.None, "none-event");
        Require(provider.Events.Select(entry => entry.Level).SequenceEqual(EmittedLevels.Where(level => level >= threshold)),
            category + " captures only events allowed by its threshold");
        Require(provider.Events.All(entry => entry.Category == category && entry.Message == "event-" + entry.Level),
            category + " preserves captured event category and message");
    }
}
