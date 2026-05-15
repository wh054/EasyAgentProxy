using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace AppProxyHelper;

internal sealed class GuiSingleInstance : IDisposable
{
    private const int SwRestore = 9;
    private const int SwShow = 5;

    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly string _activateEventName;
    private readonly string _closeEventName;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _closeEvent;
    private ManualResetEventSlim? _stopSignal;
    private Task? _listenerTask;
    private Form? _form;
    private Func<bool>? _canCloseForElevatedRestart;
    private Action? _closeForElevatedRestart;

    private GuiSingleInstance(
        Mutex mutex,
        bool ownsMutex,
        string activateEventName,
        string closeEventName)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _activateEventName = activateEventName;
        _closeEventName = closeEventName;
    }

    public bool IsOwner => _ownsMutex;

    public static GuiSingleInstance Acquire(bool autoRun)
    {
        var executablePath = GetExecutablePath();
        var instanceKey = BuildInstanceKey(executablePath);
        var mutexName = $@"Local\AppProxyHelper.Ui.{instanceKey}";
        var activateEventName = $@"Local\AppProxyHelper.Ui.Activate.{instanceKey}";
        var closeEventName = $@"Local\AppProxyHelper.Ui.CloseForElevation.{instanceKey}";

        Mutex mutex;
        try
        {
            mutex = new Mutex(false, mutexName);
        }
        catch (UnauthorizedAccessException)
        {
            SignalEvent(activateEventName);
            BringExistingProcessWindowToFront(executablePath);
            return new GuiSingleInstance(new Mutex(false), false, activateEventName, closeEventName);
        }

        var ownsMutex = TryWait(mutex, TimeSpan.Zero);
        if (!ownsMutex && autoRun && IsAdministrator())
        {
            SignalEvent(closeEventName);
            ownsMutex = TryWait(mutex, TimeSpan.FromSeconds(8));
        }

        if (!ownsMutex)
        {
            SignalEvent(activateEventName);
            BringExistingProcessWindowToFront(executablePath);
        }

        return new GuiSingleInstance(mutex, ownsMutex, activateEventName, closeEventName);
    }

    public void Attach(Form form, Func<bool> canCloseForElevatedRestart, Action? closeForElevatedRestart = null)
    {
        if (!_ownsMutex)
        {
            return;
        }

        _form = form;
        _canCloseForElevatedRestart = canCloseForElevatedRestart;
        _closeForElevatedRestart = closeForElevatedRestart;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activateEventName);
        _closeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _closeEventName);
        _stopSignal = new ManualResetEventSlim(false);
        _listenerTask = Task.Run(ListenForInstanceSignals);
    }

    public void Dispose()
    {
        _stopSignal?.Set();
        try
        {
            _listenerTask?.Wait(1000);
        }
        catch
        {
            // Shutdown should not be blocked by a stale instance listener.
        }

        _stopSignal?.Dispose();
        _activateEvent?.Dispose();
        _closeEvent?.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // The mutex may already be abandoned during process shutdown.
            }
        }

        _mutex.Dispose();
    }

    private void ListenForInstanceSignals()
    {
        if (_activateEvent is null || _closeEvent is null || _stopSignal is null)
        {
            return;
        }

        var handles = new WaitHandle[]
        {
            _activateEvent,
            _closeEvent,
            _stopSignal.WaitHandle
        };

        while (true)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch
            {
                return;
            }

            if (index == 0)
            {
                PostToForm(ActivateForm);
                continue;
            }

            if (index == 1)
            {
                PostToForm(CloseForElevatedRestart);
                continue;
            }

            return;
        }
    }

    private void PostToForm(Action action)
    {
        var form = _form;
        if (form is null || form.IsDisposed)
        {
            return;
        }

        try
        {
            if (form.InvokeRequired)
            {
                form.BeginInvoke(action);
                return;
            }

            action();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ActivateForm()
    {
        var form = _form;
        if (form is null || form.IsDisposed)
        {
            return;
        }

        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.ShowInTaskbar = true;
        form.Show();
        form.Activate();
        form.BringToFront();
    }

    private void CloseForElevatedRestart()
    {
        var form = _form;
        if (form is null || form.IsDisposed)
        {
            return;
        }

        if (_canCloseForElevatedRestart?.Invoke() == true)
        {
            if (_closeForElevatedRestart is not null)
            {
                _closeForElevatedRestart();
            }
            else
            {
                form.Close();
            }

            return;
        }

        ActivateForm();
    }

    private static bool TryWait(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return timeout == TimeSpan.Zero
                ? mutex.WaitOne(0)
                : mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static void SignalEvent(string eventName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool BringExistingProcessWindowToFront(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (var process in Process.GetProcessesByName(processName).OrderBy(static process => process.Id))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || !IsSameExecutable(process, executablePath))
                {
                    continue;
                }

                var windowHandle = process.MainWindowHandle;
                if (windowHandle == IntPtr.Zero)
                {
                    continue;
                }

                ShowWindow(windowHandle, SwRestore);
                ShowWindow(windowHandle, SwShow);
                SetForegroundWindow(windowHandle);
                return true;
            }
        }

        return false;
    }

    private static bool IsSameExecutable(Process process, string executablePath)
    {
        try
        {
            var modulePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(modulePath)
                && string.Equals(
                    Path.GetFullPath(modulePath),
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Application.ExecutablePath;
        }

        return Path.GetFullPath(executablePath);
    }

    private static string BuildInstanceKey(string executablePath)
    {
        var userSid = GetUserSid();
        var input = $"{userSid}|{Path.GetFullPath(executablePath).ToUpperInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 12);
    }

    private static string GetUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
