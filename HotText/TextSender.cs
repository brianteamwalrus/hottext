using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static HotText.Native;

namespace HotText
{
    /// <summary>
    /// Types text into whatever window has focus, via SendInput, on a background thread
    /// (so the keyboard hook on the UI thread stays responsive while we wait and type).
    /// </summary>
    internal sealed class TextSender : IDisposable
    {
        private readonly KeyboardHook _hook;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public TextSender(KeyboardHook hook)
        {
            _hook = hook;
            _thread = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) SafeRun(a); })
            {
                IsBackground = true,
                Name = "HotText sender",
            };
            _thread.Start();
        }

        public event Action<Exception> Error;

        public void Enqueue(IReadOnlyList<Step> steps, Mods triggerMods, int keyDelayMs, bool unicode)
        {
            // Tap a dummy key right away (while Alt/Win are still held) so their release
            // doesn't open the app's menu bar or the Start menu.
            if ((triggerMods & (Mods.Alt | Mods.Win)) != 0)
                _queue.Add(() => Send(new List<INPUT> { Key(VK_MASK, false), Key(VK_MASK, true) }));

            _queue.Add(() =>
            {
                WaitForModifierRelease();
                foreach (var step in steps)
                {
                    if (step.Text != null) Type(step.Text, keyDelayMs, unicode); // sends everything before returning
                    else if (step.DelayMs > 0) Thread.Sleep(step.DelayMs);
                }
            });
        }

        private void SafeRun(Action a)
        {
            try { a(); }
            catch (Exception ex) { Error?.Invoke(ex); }
        }

        /// <summary>
        /// Wait for the user to let go of Ctrl/Alt/Shift/Win, otherwise "text 1" would arrive as Ctrl+T, Ctrl+E...
        /// If they're still held after a second, release them for the target app.
        /// </summary>
        private void WaitForModifierRelease()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(1000);
            while (_hook.CurrentMods() != Mods.None && DateTime.UtcNow < deadline)
                Thread.Sleep(10);

            var held = new List<INPUT>();
            foreach (int vk in new[] { VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LWIN, VK_RWIN })
                if (IsDown(vk)) held.Add(Key(vk, true));
            if (held.Count > 0) Send(held);
        }

        private void Type(string text, int keyDelayMs, bool unicode)
        {
            var batch = new List<INPUT>();
            void Flush()
            {
                if (batch.Count == 0) return;
                Send(batch);
                batch.Clear();
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++; // \r\n -> one Enter
                    AddTap(batch, VK_RETURN);
                }
                else if (c == '\n') AddTap(batch, VK_RETURN);
                else if (c == '\t') AddTap(batch, VK_TAB);
                else if (!unicode && TryAddLayoutKey(batch, c)) { }
                else
                {
                    batch.Add(Unicode(c, false));
                    batch.Add(Unicode(c, true));
                }

                if (keyDelayMs > 0) { Flush(); Thread.Sleep(keyDelayMs); }
                else if (batch.Count >= 64) Flush();
            }
            Flush();
        }

        /// <summary>"keys" send mode: press the real key (plus Shift) from the local keyboard layout.</summary>
        private static bool TryAddLayoutKey(List<INPUT> batch, char c)
        {
            short r = VkKeyScan(c);
            if (r == -1) return false;
            int vk = r & 0xFF;
            int shiftState = (r >> 8) & 0xFF;
            if ((shiftState & ~1) != 0) return false; // needs Ctrl/Alt (AltGr): fall back to Unicode
            bool shift = (shiftState & 1) != 0;
            if (shift) batch.Add(Key(VK_LSHIFT, false));
            AddTap(batch, vk);
            if (shift) batch.Add(Key(VK_LSHIFT, true));
            return true;
        }

        private static void AddTap(List<INPUT> batch, int vk)
        {
            batch.Add(Key(vk, false));
            batch.Add(Key(vk, true));
        }

        private static INPUT Key(int vk, bool up)
        {
            // Include the scan code: Remote Desktop clients forward scan codes, not just VKs.
            ushort scan = (ushort)MapVirtualKey((uint)vk, 0 /* MAPVK_VK_TO_VSC */);
            uint flags = up ? KEYEVENTF_KEYUP : 0;
            if (vk == VK_RCONTROL || vk == VK_RMENU || vk == VK_LWIN || vk == VK_RWIN) flags |= KEYEVENTF_EXTENDEDKEY;
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags, dwExtraInfo = OurMarker } },
            };
        }

        private static INPUT Unicode(char c, bool up) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = OurMarker },
            },
        };

        private static void Send(List<INPUT> inputs)
        {
            var arr = inputs.ToArray();
            uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
            if (sent != arr.Length)
                throw new InvalidOperationException(
                    $"SendInput sent {sent} of {arr.Length} events (error {Marshal.GetLastWin32Error()}). " +
                    "If the target window is running as administrator, HotText must be too.");
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}
