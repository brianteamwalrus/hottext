using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HotText
{
    internal sealed class TrayContext : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "HotText";

        private readonly string _configPath;
        private readonly Control _ui; // hidden control, used to marshal callbacks onto the UI thread
        private readonly NotifyIcon _tray;
        private readonly Icon _iconOn, _iconOff;
        private readonly KeyboardHook _hook;
        private readonly ForegroundWatcher _foreground;
        private readonly TextSender _sender;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _reloadDebounce = new Timer { Interval = 300 };
        private readonly Timer _rehookDelay = new Timer { Interval = 1000 };

        private readonly ToolStripMenuItem _pauseItem, _hotkeysMenu, _startupItem;

        private AppConfig _config = new AppConfig();
        private bool _paused;

        public TrayContext(string configPath)
        {
            _configPath = Path.GetFullPath(configPath);

            _ui = new Control();
            _ = _ui.Handle; // force handle creation so BeginInvoke works

            _iconOn = MakeIcon(Color.FromArgb(0, 120, 212));
            _iconOff = MakeIcon(Color.Gray);

            _pauseItem = new ToolStripMenuItem("Pause hotkeys", null, (s, e) => SetPaused(!_paused));
            _hotkeysMenu = new ToolStripMenuItem("Hotkeys");
            _startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup())
            {
                Checked = IsStartupEnabled(),
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(_hotkeysMenu);
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Edit config", null, (s, e) => EditConfig()) { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add("Reload config", null, (s, e) => Reload(notify: true));
            menu.Items.Add("Open config folder", null, (s, e) => Process.Start("explorer.exe", $"/select,\"{_configPath}\""));
            menu.Items.Add("Open log", null, (s, e) =>
            {
                if (!File.Exists(Log.FilePath)) Log.Write("(log opened)");
                System.Threading.Thread.Sleep(100);
                Process.Start("notepad.exe", $"\"{Log.FilePath}\"");
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());

            _tray = new NotifyIcon { Icon = _iconOn, Text = "HotText", ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (s, e) => EditConfig();

            _hook = new KeyboardHook { KeyDown = OnKeyDown };
            _sender = new TextSender(_hook);
            _sender.Error += ex => Log.Write("Send failed: " + ex.Message);
            _sender.Error += ex => _ui.BeginInvoke((Action)(() => Balloon("Couldn't type text", ex.Message, ToolTipIcon.Warning)));

            // When a Remote Desktop window comes to the front it (re)installs its own keyboard hook,
            // which would then see keys before us. Re-hook right away and again shortly after.
            _foreground = new ForegroundWatcher();
            _foreground.Changed += OnForegroundChanged;
            _rehookDelay.Tick += (s, e) =>
            {
                if (IsRemoteDesktop(_foreground.CurrentProcess())) _hook.Reinstall();
                else _rehookDelay.Stop();
            };

            AppConfig.EnsureExists(_configPath);
            Reload(notify: false);

            _reloadDebounce.Tick += (s, e) => { _reloadDebounce.Stop(); Reload(notify: true); };
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_configPath), Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (s, e) => _ui.BeginInvoke((Action)(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }));
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
        }

        private bool OnKeyDown(int vk, Mods mods)
        {
            var hk = _config.Find(vk, mods);
            // Debug: log key combos (never plain/Shift typing, so passwords etc. never reach the log).
            bool combo = (mods & (Mods.Ctrl | Mods.Alt | Mods.Win)) != 0;
            if (_config.Debug && combo && hk == null)
                Log.Write($"Key seen: {AppConfig.Describe(vk, mods)} in '{_foreground.CurrentProcess()}' - no hotkey for it");
            if (hk == null) return false;

            string fg = _foreground.CurrentProcess();
            if (_paused || !_config.Enabled)
            {
                Log.Write($"Hotkey {hk.Display} pressed in '{fg}' - ignored (HotText paused/disabled)");
                return false;
            }
            if (!_config.ForegroundAllowed(fg))
            {
                Log.Write($"Hotkey {hk.Display} pressed in '{fg}' - ignored ('{fg}' is not in onlyWhenForeground)");
                return false;
            }

            Log.Write($"Hotkey {hk.Display} pressed in '{fg}' - sending");
            _sender.Enqueue(hk.Steps, mods, _config.KeyDelayMs, _config.UnicodeMode);
            return true;
        }

        private void OnForegroundChanged(string process)
        {
            _hook.ResyncModifiers();
            if (_config.Debug) Log.Write($"Foreground: '{process}'");
            if (IsRemoteDesktop(process))
            {
                // In full screen the Remote Desktop client installs its own keyboard hook at times we
                // can't observe (e.g. after the full-screen switch), which then swallows keys before us.
                // Keep re-hooking every second while it is in front so we're always back first.
                if (_config.Debug) Log.Write("  remote desktop window - re-hooking keyboard every 1 s while it's in front");
                _hook.Reinstall();
                _rehookDelay.Stop();
                _rehookDelay.Start();
            }
            else
            {
                _rehookDelay.Stop();
            }
        }

        private bool IsRemoteDesktop(string process) =>
            new[] { "msrdc", "Windows365", "mstsc", "msrdcw" }.Concat(_config.OnlyWhenForeground)
                .Any(p => string.Equals(p, process, StringComparison.OrdinalIgnoreCase));

        private void Reload(bool notify)
        {
            AppConfig cfg;
            try
            {
                cfg = AppConfig.Load(_configPath);
            }
            catch (Exception ex)
            {
                Log.Write("Config NOT loaded: " + ex.Message.Replace("\n", " "));
                Balloon("Config not loaded",
                    $"{ex.Message}\nStill using the previous {_config.Hotkeys.Count} hotkey(s).", ToolTipIcon.Error);
                return;
            }

            _config = cfg;
            Log.Write($"Config loaded: {cfg.Hotkeys.Count} hotkey(s) [{string.Join(", ", cfg.Hotkeys.Select(h => h.Display))}], " +
                      $"active in: {(cfg.OnlyWhenForeground.Length == 0 ? "all apps" : string.Join(", ", cfg.OnlyWhenForeground))}" +
                      (cfg.Debug ? ", debug ON" : "") + (cfg.Warnings.Count > 0 ? ", warnings: " + string.Join(" | ", cfg.Warnings) : ""));
            RebuildHotkeysMenu();
            UpdateTrayState();

            if (cfg.Warnings.Count > 0)
            {
                var shown = cfg.Warnings.Take(4).ToList();
                if (cfg.Warnings.Count > 4) shown.Add($"...and {cfg.Warnings.Count - 4} more");
                Balloon($"Loaded {cfg.Hotkeys.Count} hotkey(s), with problems", string.Join("\n", shown), ToolTipIcon.Warning);
            }
            else if (notify)
            {
                Balloon("Config reloaded", $"{cfg.Hotkeys.Count} hotkey(s) active.", ToolTipIcon.Info);
            }
        }

        private void RebuildHotkeysMenu()
        {
            _hotkeysMenu.DropDownItems.Clear();
            if (_config.Hotkeys.Count == 0)
                _hotkeysMenu.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
            foreach (var hk in _config.Hotkeys)
            {
                string preview = AppConfig.DelayToken.Replace(hk.Text, m => $" ⏱{m.Groups[1].Value}ms ").Replace("\r", "").Replace("\n", " ⏎ ").Replace("\t", " ⇥ ");
                if (preview.Length > 50) preview = preview.Substring(0, 47) + "...";
                _hotkeysMenu.DropDownItems.Add(new ToolStripMenuItem($"{hk.Display}\t{preview}") { Enabled = false });
            }
            if (_config.OnlyWhenForeground.Length > 0)
            {
                _hotkeysMenu.DropDownItems.Add(new ToolStripSeparator());
                _hotkeysMenu.DropDownItems.Add(new ToolStripMenuItem(
                    "Active only in: " + string.Join(", ", _config.OnlyWhenForeground)) { Enabled = false });
            }
        }

        private void SetPaused(bool paused)
        {
            _paused = paused;
            UpdateTrayState();
        }

        private void UpdateTrayState()
        {
            bool active = !_paused && _config.Enabled;
            _pauseItem.Checked = _paused;
            _tray.Icon = active ? _iconOn : _iconOff;
            string state = _paused ? "paused" : !_config.Enabled ? "disabled in config" : $"{_config.Hotkeys.Count} hotkey(s)";
            string tip = $"HotText - {state}";
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        private void EditConfig()
        {
            AppConfig.EnsureExists(_configPath);
            Process.Start("notepad.exe", $"\"{_configPath}\"");
        }

        private static string ExePath => Application.ExecutablePath;

        private static bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key?.GetValue(RunValue) is string v &&
                       v.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
        }

        private void ToggleStartup()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsStartupEnabled()) key.DeleteValue(RunValue, false);
                else key.SetValue(RunValue, $"\"{ExePath}\"");
            }
            _startupItem.Checked = IsStartupEnabled();
        }

        private void Balloon(string title, string text, ToolTipIcon icon) =>
            _tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, icon);

        private static Icon MakeIcon(Color background)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            using (var bg = new SolidBrush(background))
            using (var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var path = new GraphicsPath())
                {
                    path.AddArc(1, 1, 10, 10, 180, 90);
                    path.AddArc(21, 1, 10, 10, 270, 90);
                    path.AddArc(21, 21, 10, 10, 0, 90);
                    path.AddArc(1, 21, 10, 10, 90, 90);
                    path.CloseFigure();
                    g.FillPath(bg, path);
                }
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("Tx", font, Brushes.White, new RectangleF(0, 1, 32, 32), fmt);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watcher?.Dispose();
                _foreground?.Dispose();
                _hook?.Dispose();
                _sender?.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                _ui.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
