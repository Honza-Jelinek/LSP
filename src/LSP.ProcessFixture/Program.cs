namespace LSP.ProcessFixture;

public static class Marker { }

public static class Program
{
    public static async Task Main(string[] args)
    {
        var marker = args.Last();
        // Fill the pipe before announcing readiness; callers must drain stderr concurrently.
        await Console.Error.WriteAsync(new string('x', 200_000));
        var temp = marker + ".tmp";
        await File.WriteAllTextAsync(temp, Environment.ProcessId.ToString());
        File.Move(temp, marker);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
