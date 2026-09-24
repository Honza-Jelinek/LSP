namespace LSP.ProcessFixture;

public static class Marker { }

public static class Program
{
    public static async Task Main(string[] args)
    {
        var marker = args.Last();
        // Fill the pipe before announcing readiness; callers must drain stderr concurrently.
        await Console.Error.WriteAsync(new string('x', 200_000));
        await File.WriteAllTextAsync(marker, Environment.ProcessId.ToString());
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
