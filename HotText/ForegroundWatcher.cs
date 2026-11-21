using System;
using static HotText.Native;

namespace HotText
{
    /// <summary>Raises <see cref="Changed"/> (on the UI thread) whenever the foreground window changes.</summary>
    internal sealed class ForegroundWatcher : IDisposable
    {
        public event Action<string> Changed;

        private readonly WinEventProc _proc; // kept alive for the native callback
        private readonly IntPtr _hook;
        private IntPtr _cachedHwnd = IntPtr.Zero;
        private string _cachedName = "";

        public ForegroundWatcher()
        {
            _proc = OnEvent;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        /// <summary>Process name of the current foreground window (cached per window handle).</summary>
        public string CurrentProcess()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd != _cachedHwnd)
            {
                _cachedHwnd = hwnd;
                _cachedName = ProcessNameOfWindow(hwnd);
            }
            return _cachedName;
        }

        private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try { Changed?.Invoke(CurrentProcess()); }
            catch { /* never throw into native code */ }
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        }
    }
}
