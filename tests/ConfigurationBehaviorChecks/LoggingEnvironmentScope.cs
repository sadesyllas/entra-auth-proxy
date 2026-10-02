using EntraAuthProxy;

namespace ConfigurationBehaviorChecks;

/// <summary>Isolates the selected process variables and restores their previous values.</summary>
internal sealed class LoggingEnvironmentScope : IDisposable
{
    private readonly string? previousDefault = Environment.GetEnvironmentVariable(StartupLogging.DefaultLevelVariable);
    private readonly string? previousAspNetCore = Environment.GetEnvironmentVariable(StartupLogging.AspNetCoreLevelVariable);

    public LoggingEnvironmentScope(string? defaultLevel = null, string? aspNetCoreLevel = null)
    {
        Environment.SetEnvironmentVariable(StartupLogging.DefaultLevelVariable, defaultLevel);
        Environment.SetEnvironmentVariable(StartupLogging.AspNetCoreLevelVariable, aspNetCoreLevel);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StartupLogging.DefaultLevelVariable, previousDefault);
        Environment.SetEnvironmentVariable(StartupLogging.AspNetCoreLevelVariable, previousAspNetCore);
    }
}
