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

        var scopes = new[] { _targetScope };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var accounts = await _app.GetAccountsAsync();
                var firstAccount = accounts.FirstOrDefault();
                if (firstAccount != null)
                {
                    var result = await _app.AcquireTokenSilent(scopes, firstAccount).ExecuteAsync(stoppingToken);
                    _tokenProvider.SetToken(result.AccessToken);
                }
            }
            catch (MsalUiRequiredException)
            {
                // Background service cannot easily prompt interactively.
            }
            catch (Exception)
            {
                // Ignore transient errors and try again next tick
            }
        }
    }
}