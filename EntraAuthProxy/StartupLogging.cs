namespace EntraAuthProxy;

/// <summary>Logging thresholds captured once after JSON loading and before authentication.</summary>
internal sealed record StartupLogging(LogLevel DefaultLevel, LogLevel AspNetCoreLevel)
{
    public const string DefaultLevelVariable = "ENTRAAUTHPROXY_LOG_LEVEL";
    public const string AspNetCoreLevelVariable = "ENTRAAUTHPROXY_ASPNETCORE_LOG_LEVEL";

    private static readonly string[] LevelNames = Enum.GetNames<LogLevel>();

    /// <summary>
    /// Reads only the supported logging environment variables. Blank values retain
    /// their independent fallbacks; invalid nonblank values throw with the variable
    /// name and permitted levels before authentication or persistent cache access.
    /// </summary>
    public static StartupLogging CaptureEnvironment() => new(
        Resolve(DefaultLevelVariable, LogLevel.Information),
        Resolve(AspNetCoreLevelVariable, LogLevel.Warning));

    /// <summary>
    /// Retains the simple console format and applies the captured default and
    /// ASP.NET Core thresholds. More specific category/provider rules retain native
    /// precedence; JSON reloads cannot replace these two startup rules.
    /// </summary>
    public void Configure(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            options.UseUtcTimestamp = true;
            options.SingleLine = true;
        });
        logging.SetMinimumLevel(DefaultLevel);
        // A default rule also replaces any JSON Logging:LogLevel:Default rule.
        // Using category rules avoids imposing the default as a global cutoff.
        logging.AddFilter(category: null, level: DefaultLevel);
        logging.AddFilter("Microsoft.AspNetCore", AspNetCoreLevel);
    }

    private static LogLevel Resolve(string variable, LogLevel fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var name = value.Trim();
        if (!LevelNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{variable} must be one of: {string.Join(", ", LevelNames)}.");

        return Enum.Parse<LogLevel>(name, ignoreCase: true);
    }
}
