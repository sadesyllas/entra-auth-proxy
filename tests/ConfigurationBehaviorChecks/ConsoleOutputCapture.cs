namespace ConfigurationBehaviorChecks;

/// <summary>Captures explicit stdout and restores it even if an assertion fails.</summary>
internal sealed class ConsoleOutputCapture : IDisposable
{
    private readonly TextWriter original = Console.Out;
    private readonly StringWriter output = new();

    public ConsoleOutputCapture() => Console.SetOut(output);

    public string Text => output.ToString();

    public void Dispose()
    {
        Console.SetOut(original);
        output.Dispose();
    }
}
