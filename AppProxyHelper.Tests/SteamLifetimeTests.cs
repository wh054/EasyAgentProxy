using AppProxyHelper;

internal static class SteamLifetimeTests
{
    public static IEnumerable<(string Name, Action Body)> Cases =>
    [
        ("Steam session waits for the real client after a bootstrap process exits", BootstrapExitDoesNotEndSession),
        ("Steam session tolerates a short client restart gap", RestartGapDoesNotEndSession),
        ("Steam session ignores other installations and browser helper processes", OtherSteamPathsDoNotKeepSessionAlive),
        ("Steam session cancellation stops waiting without changing live process snapshots", CancellationLeavesSteamRunning)
    ];

    private static void BootstrapExitDoesNotEndSession()
    {
        var path = TestSteamPath;
        var snapshots = new IReadOnlyList<SteamProcessSnapshot>[]
        {
            [],
            [new(200, path.ToUpperInvariant())],
            [new(200, path)],
            [], [], [], [], []
        };
        var queryCount = WaitForSnapshots(path, snapshots);
        Equal(true, queryCount >= 7, "a new same-path client resets the exit grace period even after the bootstrap PID disappears");
    }

    private static void RestartGapDoesNotEndSession()
    {
        var path = TestSteamPath;
        var snapshots = new IReadOnlyList<SteamProcessSnapshot>[]
        {
            [new(200, path)],
            [], [],
            [new(201, path)],
            [new(201, path)],
            [], [], [], [], []
        };
        var queryCount = WaitForSnapshots(path, snapshots);
        Equal(true, queryCount >= 9, "the new Steam PID resets the full grace period after a two-poll restart gap");
    }

    private static void OtherSteamPathsDoNotKeepSessionAlive()
    {
        var path = TestSteamPath;
        var otherInstallation = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "Other Steam", "steam.exe");
        IReadOnlyList<SteamProcessSnapshot> unrelated =
        [
            new(300, otherInstallation),
            new(301, Path.Combine(Path.GetDirectoryName(path)!, "steamwebhelper.exe"))
        ];
        var queryCount = WaitForSnapshots(path, Enumerable.Repeat(unrelated, 10).ToArray());
        Equal(true, queryCount <= 5, "unrelated Steam and browser processes do not extend this installation's grace period");
    }

    private static void CancellationLeavesSteamRunning()
    {
        var path = TestSteamPath;
        using var cancellation = new CancellationTokenSource();
        var liveProcess = new SteamProcessSnapshot(400, path);
        var snapshots = new List<SteamProcessSnapshot> { liveProcess };
        var delayCalls = 0;
        try
        {
            SteamProcessLifetime.WaitForExitAsync(path,
                () => snapshots,
                (_, token) =>
                {
                    Equal(cancellation.Token, token, "caller cancellation token is passed to the polling delay");
                    if (++delayCalls > 1) throw new InvalidOperationException("Cancelled Steam wait continued polling.");
                    cancellation.Cancel();
                    return Task.Delay(Timeout.Infinite, token);
                }, cancellation.Token, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Equal(1, delayCalls, "cancellation ends the first active delay");
            Equal(1, snapshots.Count, "the wait does not remove or terminate the live Steam process");
            Equal(liveProcess, snapshots[0], "Steam identity is preserved");
            return;
        }
        throw new InvalidOperationException("Expected Steam session wait cancellation.");
    }

    private static int WaitForSnapshots(string path, IReadOnlyList<SteamProcessSnapshot>[] snapshots)
    {
        var queries = 0;
        var delays = 0;
        SteamProcessLifetime.WaitForExitAsync(path,
            () => snapshots[Math.Min(queries++, snapshots.Length - 1)],
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (++delays > 20) throw new InvalidOperationException("Steam lifetime polling did not reach a stable exit state.");
                return Task.CompletedTask;
            }, CancellationToken.None, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        return queries;
    }

    private static string TestSteamPath => Path.Combine(Path.GetTempPath(), "EasyProxySteamLifetimeTests", "Steam", "steam.exe");

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }
}
