namespace ConfigurationBehaviorChecks;

internal static class TestAssertions
{
    public static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + behavior);
    }
}
