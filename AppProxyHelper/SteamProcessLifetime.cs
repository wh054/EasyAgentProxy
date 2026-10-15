using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AppProxyHelper;

internal readonly record struct SteamProcessSnapshot(int ProcessId, string ExecutablePath);

/// <summary>Waits for the Steam installation, including single-instance handoff and update restarts.</summary>
internal static class SteamProcessLifetime
{
    private static readonly TimeSpan DefaultRestartGracePeriod = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    internal static Task WaitForExitAsync(string fullSteamExePath, Process? initialProcess, CancellationToken token)
    {
        var expectedPath = NormalizeExecutablePath(fullSteamExePath);
        return WaitForExitAsync(expectedPath, () =>
        {
            var processes = CaptureSteamProcesses();
            // The process returned by Process.Start may be only a short-lived bootstrap.
            // Include it while alive even if its image path could not yet be queried.
            if (initialProcess is not null)
            {
                try
                {
                    if (!initialProcess.HasExited && processes.All(process => process.ProcessId != initialProcess.Id
                        || !IsMatchingSteamProcessPath(expectedPath, process.ExecutablePath)))
                        processes.Add(new SteamProcessSnapshot(initialProcess.Id, expectedPath));
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            return processes;
        }, Task.Delay, token);
    }

    /// <summary>
    /// Testable polling loop. The injected delay represents elapsed polling time, so tests
    /// can advance a complete session without real processes or wall-clock waits.
    /// </summary>
    internal static async Task WaitForExitAsync(string fullSteamExePath,
        Func<IReadOnlyList<SteamProcessSnapshot>> snapshot,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken,
        TimeSpan? restartGracePeriod = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(delayAsync);
        var expectedPath = NormalizeExecutablePath(fullSteamExePath);
        if (!Path.GetFileName(expectedPath).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Steam 生命周期目标必须是 Steam.exe 的完整路径。", nameof(fullSteamExePath));

        var grace = restartGracePeriod ?? DefaultRestartGracePeriod;
        var polling = pollInterval ?? DefaultPollInterval;
        if (grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(restartGracePeriod));
        if (polling <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));

        var continuouslyAbsent = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = snapshot() ?? throw new InvalidOperationException("Steam 进程快照不能为空。");
            var alive = current.Any(process => process.ProcessId > 0 && IsMatchingSteamProcessPath(expectedPath, process.ExecutablePath));
            if (alive)
            {
                continuouslyAbsent = TimeSpan.Zero;
            }
            else if (continuouslyAbsent >= grace)
            {
                return;
            }

            // Start the quiet period at the first empty observation, not at the previous
            // positive observation. A short updater restart always resets the full grace.
            var delay = !alive && grace - continuouslyAbsent < polling ? grace - continuouslyAbsent : polling;
            await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            if (!alive) continuouslyAbsent += delay;
        }
    }

    internal static bool IsMatchingSteamProcessPath(string expectedExePath, string? actualExePath)
    {
        if (string.IsNullOrWhiteSpace(actualExePath)) return false;
        try
        {
            var expected = NormalizeExecutablePath(expectedExePath);
            var actual = NormalizeExecutablePath(actualExePath);
            return Path.GetFileName(actual).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static List<SteamProcessSnapshot> CaptureSteamProcesses()
    {
        var result = new List<SteamProcessSnapshot>();
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    var path = TryGetExecutablePath(process);
                    if (!string.IsNullOrWhiteSpace(path)) result.Add(new SteamProcessSnapshot(process.Id, path));
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
        return result;
    }

    private static string? TryGetExecutablePath(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            // Unlike MainModule, this works across 32/64-bit processes and normally does
            // not require elevation just to inspect another same-user Steam process.
            using var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, process.Id);
            if (!handle.IsInvalid)
            {
                var capacity = 32768;
                var path = new StringBuilder(capacity);
                if (QueryFullProcessImageNameW(handle, 0, path, ref capacity)) return path.ToString();
            }
        }
        try { return process.MainModule?.FileName; }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    private static string NormalizeExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Steam 进程路径必须是完整路径。", nameof(path));
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder executableName, ref int size);
}
