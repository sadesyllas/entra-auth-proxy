using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;

namespace EntraAuthProxy;

public class TokenRefreshService : BackgroundService
{
    private readonly IPublicClientApplication _app;
    private readonly TokenProvider _tokenProvider;
    private readonly string _targetScope;

    public TokenRefreshService(IPublicClientApplication app, TokenProvider tokenProvider, IConfiguration configuration)
    {
        _app = app;
        _tokenProvider = tokenProvider;
        _targetScope = configuration["EntraAuth:TargetScope"] ?? string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_targetScope))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshTokenAsync(stoppingToken);
    }

    /// <summary>
    /// Runs one background acquisition and publishes only a successful result.
    /// The optional acquisition stand-in permits credential-free checks of the
    /// production publication and failure boundary without waiting for a timer.
    /// </summary>
    internal async Task RefreshTokenAsync(CancellationToken stoppingToken,
        Func<CancellationToken, Task<AuthenticationResult?>>? acquire = null)
    {
        try
        {
            var result = await (acquire ?? AcquireTokenAsync)(stoppingToken);
            if (result != null) _tokenProvider.SetToken(result.AccessToken);
        }
        catch (MsalUiRequiredException)
        {
            // Background service cannot easily prompt interactively.
        }
        catch (Exception)
        {
            // Ignore transient errors and try again next tick.
        }
    }

    private async Task<AuthenticationResult?> AcquireTokenAsync(CancellationToken stoppingToken)
    {
        var accounts = await _app.GetAccountsAsync();
        var firstAccount = accounts.FirstOrDefault();
        return firstAccount == null ? null :
            await _app.AcquireTokenSilent([_targetScope], firstAccount).ExecuteAsync(stoppingToken);
    }
}
