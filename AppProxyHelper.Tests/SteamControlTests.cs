using AppProxyHelper;

internal static class SteamControlTests
{
    public static IEnumerable<(string Name, Action Body)> Cases =>
    [
        ("Steam stop cancels shutdown before a failing status read", StopCancelsBeforeFailedStatusRead),
        ("Steam stop remains cancelled when its acknowledgement fails", StopCancelsBeforeFailedAcknowledgement),
        ("Steam status replies without stopping or confirming startup", StatusDoesNotStopOrConfirm),
        ("Steam confirmation acknowledges startup without stopping", ConfirmationDoesNotStop),
        ("Steam unknown control commands have no effects", UnknownCommandsHaveNoEffects)
    ];

    private static void StopCancelsBeforeFailedStatusRead()
    {
        using var stop = new CancellationTokenSource();
        var confirmed = false;
        var wroteResponse = false;
        Throws<IOException>(() => SteamProxyProcessController.HandleControlCommandAsync("stop", () =>
        {
            Equal(true, stop.IsCancellationRequested, "stop is cancelled before status access");
            throw new IOException("test status read failure");
        }, _ =>
        {
            wroteResponse = true;
            return Task.CompletedTask;
        }, () => confirmed = true, stop).GetAwaiter().GetResult());

        Equal(true, stop.IsCancellationRequested, "status failure cannot undo shutdown");
        Equal(false, wroteResponse, "status failure does not fabricate an acknowledgement");
        Equal(false, confirmed, "stop does not confirm startup");
    }

    private static void StopCancelsBeforeFailedAcknowledgement()
    {
        using var stop = new CancellationTokenSource();
        var confirmed = false;
        var statusReads = 0;
        var writes = 0;
        Throws<IOException>(() => SteamProxyProcessController.HandleControlCommandAsync("stop", () =>
        {
            Equal(true, stop.IsCancellationRequested, "shutdown precedes status access");
            statusReads++;
            return TestStatus;
        }, _ =>
        {
            Equal(true, stop.IsCancellationRequested, "shutdown precedes acknowledgement");
            writes++;
            return Task.FromException(new IOException("test client disconnected"));
        }, () => confirmed = true, stop).GetAwaiter().GetResult());

        Equal(true, stop.IsCancellationRequested, "broken acknowledgement cannot prevent shutdown");
        Equal(1, statusReads, "status read once");
        Equal(1, writes, "acknowledgement attempted once");
        Equal(false, confirmed, "stop does not confirm startup");
    }

    private static void StatusDoesNotStopOrConfirm()
    {
        using var stop = new CancellationTokenSource();
        var confirmed = false;
        var writes = 0;
        SteamProxyProcessController.HandleControlCommandAsync("status", () =>
        {
            Equal(false, stop.IsCancellationRequested, "status inspection leaves shutdown untouched");
            return TestStatus;
        }, response =>
        {
            Equal(TestStatus, response, "status payload preserved");
            writes++;
            return Task.CompletedTask;
        }, () => confirmed = true, stop).GetAwaiter().GetResult();

        Equal(1, writes, "status acknowledged once");
        Equal(false, stop.IsCancellationRequested, "status does not request shutdown");
        Equal(false, confirmed, "status does not confirm startup");
    }

    private static void ConfirmationDoesNotStop()
    {
        using var stop = new CancellationTokenSource();
        var events = new List<string>();
        SteamProxyProcessController.HandleControlCommandAsync("confirm", () =>
        {
            Equal(false, stop.IsCancellationRequested, "confirmation does not cancel shutdown");
            events.Add("read");
            return TestStatus;
        }, response =>
        {
            Equal(TestStatus, response, "confirmation payload preserved");
            events.Add("write");
            return Task.CompletedTask;
        }, () => events.Add("confirm"), stop).GetAwaiter().GetResult();

        Equal(true, events.SequenceEqual(new[] { "confirm", "read", "write" }), "startup confirmation precedes inspection and acknowledgement");
        Equal(false, stop.IsCancellationRequested, "confirmation leaves the worker running");
    }

    private static void UnknownCommandsHaveNoEffects()
    {
        foreach (var command in new string?[] { null, "", "shutdown", "stop extra" })
        {
            using var stop = new CancellationTokenSource();
            SteamProxyProcessController.HandleControlCommandAsync(command,
                () => throw new InvalidOperationException("Unknown command inspected status."),
                _ => throw new InvalidOperationException("Unknown command sent an acknowledgement."),
                () => throw new InvalidOperationException("Unknown command confirmed startup."), stop).GetAwaiter().GetResult();
            Equal(false, stop.IsCancellationRequested, "unknown command leaves shutdown untouched");
        }
    }

    private static SteamProxyStatus TestStatus => new(true, 123, null, true, true,
        "test-log-path", "test-data-directory", IsSession: true);

    private static void Throws<T>(Action body) where T : Exception
    {
        try { body(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }
}
