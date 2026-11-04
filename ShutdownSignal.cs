namespace TheWitness;

internal static class ShutdownSignal
{
    private static readonly ManualResetEventSlim requested = new(initialState: false);
    private static readonly ManualResetEventSlim finished = new(initialState: false);

    internal static bool IsRequested => requested.IsSet;

    internal static void Request() => requested.Set();

    internal static void SignalFinished() => finished.Set();

    internal static bool WaitForFinished(TimeSpan timeout) => finished.Wait(timeout);
}
