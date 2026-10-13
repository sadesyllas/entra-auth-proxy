using System.Reflection;
using Microsoft.Identity.Client;

namespace ConfigurationBehaviorChecks;

/// <summary>A strict MSAL stand-in for the production refresh no-account branch.</summary>
internal class NoAccountClient : DispatchProxy
{
    public int AccountReads { get; private set; }
    public List<string?> UnexpectedCalls { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IPublicClientApplication.GetAccountsAsync))
        {
            AccountReads++;
            return Task.FromResult(Enumerable.Empty<IAccount>());
        }
        UnexpectedCalls.Add(targetMethod?.Name);
        throw new InvalidOperationException("Unexpected MSAL operation in no-account check: " + targetMethod?.Name);
    }
}
