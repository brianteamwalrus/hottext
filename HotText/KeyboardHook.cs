using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static HotText.Native;

namespace HotText
{
    /// <summary>
    /// Global low-level keyboard hook. Must be created on a thread with a message loop (the UI thread).
    /// Low-level hooks are called newest-first, so <see cref="Reinstall"/> puts this hook back in front
    /// of a hook another app (e.g. the Remote Desktop client) installed after us.
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        /// <summary>Called for each physical (non-injected) non-modifier key-down. Return true to swallow the key.</summary>
        public Func<int, Mods, bool> KeyDown;

        private readonly LowLevelKeyboardProc _proc; // kept in a field so the GC never collects the delegate
        private IntPtr _hook;

        // Modifiers tracked from the hook's own events. GetAsyncKeyState alone isn't enough: if another
        // hook swallows a modifier, the system's async key state never sees it.
        private volatile int _modBits;

        // Keys whose key-down we swallowed: swallow their auto-repeats and matching key-up too.
        private readonly Dictionary<int, uint> _swallowed = new Dictionary<int, uint>();

        public KeyboardHook()
        {
            _proc = HookProc;
            Install();
        }

        private void Install()
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
        }

        /// <summary>
        /// Puts this hook back at the front of the chain. Install the new hook before removing the old one,
        /// so there's never a moment with no hook (a hotkey pressed right then would leak to the remote).
        /// </summary>
        public void Reinstall()
        {
            IntPtr old = _hook;
            try { Install(); }
            catch (Win32Exception) { _hook = old; return; } // keep the existing hook
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        }

        public void ResyncModifiers()
        {
            int bits = 0;
            foreach (int vk in new[] { VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LWIN, VK_RWIN })
                if (IsDown(vk)) bits |= ModBit(vk);
            _modBits = bits;
        }

        /// <summary>Currently-held modifiers. Safe to call from any thread.</summary>
        public Mods CurrentMods()
        {
            int b = _modBits;
            var m = Mods.None;
            if ((b & 0x03) != 0 || IsDown(VK_CONTROL)) m |= Mods.Ctrl;
            if ((b & 0x0C) != 0 || IsDown(VK_MENU)) m |= Mods.Alt;
            if ((b & 0x30) != 0 || IsDown(VK_SHIFT)) m |= Mods.Shift;
            if ((b & 0xC0) != 0 || IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= Mods.Win;
            return m;
        }

        private static int ModBit(int vk)
        {
            switch (vk)
            {
                case VK_LCONTROL: return 0x01;
                case VK_RCONTROL: return 0x02;
                case VK_LMENU: return 0x04;
                case VK_RMENU: return 0x08;
                case VK_LSHIFT: return 0x10;
                case VK_RSHIFT: return 0x20;
                case VK_LWIN: return 0x40;
                case VK_RWIN: return 0x80;
                // Generic VKs occasionally show up in hook data.
                case VK_CONTROL: return 0x01;
                case VK_MENU: return 0x04;
                case VK_SHIFT: return 0x10;
                default: return 0;
            }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    int vk = (int)k.vkCode;
                    int bit = ModBit(vk);
                    // Our own typed text must never fire hotkeys. Other injected input (macro keyboards,
                    // automation tools) is allowed to.
                    bool ours = (k.flags & LLKHF_INJECTED) != 0 && k.dwExtraInfo == OurMarker;

                    if (bit != 0)
                    {
                        if (down) _modBits |= bit;
                        else if (up) _modBits &= ~bit;
                    }
                    else if (!ours)
                    {
                        if (down)
                        {
                            // Auto-repeat of a hotkey we already handled (repeats arrive every ~30 ms;
                            // a long gap means we missed its key-up, so treat it as a fresh press).
                            if (_swallowed.TryGetValue(vk, out uint last) && unchecked(k.time - last) < 1000)
                            {
                                _swallowed[vk] = k.time;
                                return (IntPtr)1;
                            }
                            _swallowed.Remove(vk);

                            var handler = KeyDown;
                            if (handler != null && handler(vk, CurrentMods()))
                            {
                                _swallowed[vk] = k.time;
                                return (IntPtr)1;
                            }
                        }
                        else if (up && _swallowed.Remove(vk))
                        {
                            return (IntPtr)1;
                        }
                    }
                }
            }
            catch
            {
                // Never let an exception escape a hook callback.
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
