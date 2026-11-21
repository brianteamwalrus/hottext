using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HotText
{
    internal sealed class Hotkey
    {
        public string Display;
        public int Vk;
        public Mods Mods;
        public string Text;
        public List<Step> Steps;
    }

    /// <summary>One piece of a hotkey's output: either text to type, or a pause.</summary>
    internal sealed class Step
    {
        public string Text;  // null for a pause
        public int DelayMs;
    }

    internal sealed class AppConfig
    {
        public static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotText", "hotkeys.json");

        public bool Enabled = true;
        public string[] OnlyWhenForeground = new string[0];
        public int KeyDelayMs;
        public bool UnicodeMode = true;
        public bool Debug;
        public List<Hotkey> Hotkeys = new List<Hotkey>();
        public List<string> Warnings = new List<string>();

        private readonly Dictionary<long, Hotkey> _lookup = new Dictionary<long, Hotkey>();

        public Hotkey Find(int vk, Mods mods) => _lookup.TryGetValue(Key(vk, mods), out var h) ? h : null;

        public bool ForegroundAllowed(string processName) =>
            OnlyWhenForeground.Length == 0 ||
            OnlyWhenForeground.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

        private static long Key(int vk, Mods mods) => ((long)vk << 8) | (long)mods;

        public const string SampleJson =
@"{
  ""_help"": [
    ""Edit this file and save - HotText reloads it automatically."",
    ""keys: modifiers Ctrl, Alt, Shift, Win plus one key, e.g. Ctrl+1, Ctrl+Shift+F1, Ctrl+Alt+S, Win+Num5."",
    ""text: a string, or a list of lines (joined with Enter)."",
    ""   For Enter type backslash+n, for Tab backslash+t - ONE backslash, as in the Ctrl+3 example below."",
    ""   Pause: {delay 100} anywhere in the text waits 100 ms (max 10000)."",
    ""onlyWhenForeground: process names where hotkeys are active; [] = everywhere."",
    ""   Windows App = Windows365 / msrdc; classic Remote Desktop = mstsc."",
    ""keyDelayMs: pause between characters if the remote drops keys (try 5-20)."",
    ""sendMode: unicode (default, any character) or keys (presses real keys; try if an app ignores unicode input).""
  ],
  ""enabled"": true,
  ""onlyWhenForeground"": [""Windows365"", ""msrdc"", ""mstsc""],
  ""keyDelayMs"": 0,
  ""sendMode"": ""unicode"",
  ""hotkeys"": [
    { ""keys"": ""Ctrl+1"", ""text"": ""text 1"" },
    { ""keys"": ""Ctrl+2"", ""text"": ""text 2"" },
    { ""keys"": ""Ctrl+3"", ""text"": ""first line\nName:\t{delay 100}value"" },
    { ""keys"": ""Ctrl+Alt+S"", ""text"": [""Kind regards,"", ""Your Name""] }
  ]
}
";

        public static void EnsureExists(string path)
        {
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, SampleJson);
        }

        /// <summary>Loads the config. Throws on unreadable/invalid JSON; individual bad hotkeys become warnings.</summary>
        public static AppConfig Load(string path)
        {
            string json = ReadShared(path);
            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(json); }
            catch (ArgumentException ex) { throw new FormatException(DescribeJsonError(ex.Message, json)); }
            var root = parsed as Dictionary<string, object>
                       ?? throw new FormatException("The file must contain a JSON object { ... }.");

            var cfg = new AppConfig();

            if (root.TryGetValue("enabled", out var en) && en is bool b) cfg.Enabled = b;
            if (root.TryGetValue("debug", out var dbg) && dbg is bool d) cfg.Debug = d;

            if (root.TryGetValue("onlyWhenForeground", out var fg) && fg != null)
            {
                if (fg is string s) fg = new object[] { s };
                if (fg is IEnumerable list)
                    cfg.OnlyWhenForeground = list.Cast<object>()
                        .Select(o => Convert.ToString(o)?.Trim())
                        .Where(o => !string.IsNullOrEmpty(o))
                        .Select(o => o.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? o.Substring(0, o.Length - 4) : o)
                        .ToArray();
                else cfg.Warnings.Add("onlyWhenForeground should be a list of process names.");
            }

            if (root.TryGetValue("keyDelayMs", out var kd) && kd != null)
            {
                try { cfg.KeyDelayMs = Math.Max(0, Math.Min(1000, Convert.ToInt32(kd))); }
                catch { cfg.Warnings.Add("keyDelayMs should be a number."); }
            }

            if (root.TryGetValue("sendMode", out var sm) && sm is string mode)
            {
                if (mode.Equals("keys", StringComparison.OrdinalIgnoreCase)) cfg.UnicodeMode = false;
                else if (!mode.Equals("unicode", StringComparison.OrdinalIgnoreCase))
                    cfg.Warnings.Add($"sendMode \"{mode}\" unknown - using unicode.");
            }

            if (!root.TryGetValue("hotkeys", out var hk) || !(hk is IEnumerable entries) || hk is string)
            {
                cfg.Warnings.Add("No \"hotkeys\": [ ... ] list found.");
                return cfg;
            }

            int n = 0;
            foreach (var item in entries)
            {
                n++;
                if (!(item is Dictionary<string, object> e))
                {
                    cfg.Warnings.Add($"Hotkey #{n}: expected {{ \"keys\": ..., \"text\": ... }}.");
                    continue;
                }

                string keys = e.TryGetValue("keys", out var k) ? Convert.ToString(k) : null;
                if (string.IsNullOrWhiteSpace(keys))
                {
                    cfg.Warnings.Add($"Hotkey #{n}: missing \"keys\".");
                    continue;
                }

                string text = ReadText(e.TryGetValue("text", out var t) ? t : null);
                if (text == null)
                {
                    cfg.Warnings.Add($"{keys}: missing \"text\".");
                    continue;
                }

                if (!TryParseKeys(keys, out int vk, out Mods mods, out string err))
                {
                    cfg.Warnings.Add($"{keys}: {err}");
                    continue;
                }

                long id = Key(vk, mods);
                if (cfg._lookup.ContainsKey(id))
                {
                    cfg.Warnings.Add($"{keys}: duplicate of {cfg._lookup[id].Display} - ignored.");
                    continue;
                }

                if (mods == Mods.None && !(vk >= (int)Keys.F1 && vk <= (int)Keys.F24))
                    cfg.Warnings.Add($"{keys}: no modifier - that key can no longer be typed normally where hotkeys are active.");

                var hotkey = new Hotkey
                {
                    Display = Describe(vk, mods), Vk = vk, Mods = mods, Text = text,
                    Steps = ParseSteps(text, keys, cfg.Warnings),
                };
                cfg.Hotkeys.Add(hotkey);
                cfg._lookup[id] = hotkey;
            }

            return cfg;
        }

        /// <summary>
        /// Turns JavaScriptSerializer's "Invalid object passed in, ':' or '}' expected. (68): {...whole file..."
        /// into "Line 2, col 66: ':' or '}' expected.  →  "Edit this file...".
        /// </summary>
        private static string DescribeJsonError(string message, string json)
        {
            var m = System.Text.RegularExpressions.Regex.Match(message, @"^(?:Invalid (?:object|JSON primitive)[^,.]*[,.]\s*)?(.*?)\s*\((\d+)\):");
            if (!m.Success) return "Invalid JSON: " + message.Split('\n')[0];

            string what = m.Groups[1].Value.Trim();
            if (what.Length == 0) what = "invalid JSON";
            int offset = Math.Min(int.Parse(m.Groups[2].Value), json.Length);

            int line = 1, lineStart = 0;
            for (int i = 0; i < offset; i++)
                if (json[i] == '\n') { line++; lineStart = i + 1; }
            int lineEnd = json.IndexOf('\n', lineStart);
            string lineText = (lineEnd < 0 ? json.Substring(lineStart) : json.Substring(lineStart, lineEnd - lineStart)).Trim();
            if (lineText.Length > 60) lineText = lineText.Substring(0, 57) + "...";

            return $"Line {line}, col {offset - lineStart + 1}: {what}\n→ {lineText}";
        }

        public const int MaxDelayMs = 10000;

        public static readonly System.Text.RegularExpressions.Regex DelayToken =
            new System.Text.RegularExpressions.Regex(@"\{delay[ :]?\s*(\d+)\}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Splits text on {delay N} tokens. Any other {...} is ordinary text.</summary>
        public static List<Step> ParseSteps(string text, string keys, List<string> warnings)
        {
            var steps = new List<Step>();
            int pos = 0;
            foreach (System.Text.RegularExpressions.Match m in DelayToken.Matches(text))
            {
                if (m.Index > pos) steps.Add(new Step { Text = text.Substring(pos, m.Index - pos) });
                int ms = int.TryParse(m.Groups[1].Value, out int v) ? v : int.MaxValue;
                if (ms > MaxDelayMs)
                {
                    warnings.Add($"{keys}: {m.Value} is over {MaxDelayMs} ms - using {MaxDelayMs}.");
                    ms = MaxDelayMs;
                }
                steps.Add(new Step { DelayMs = ms });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) steps.Add(new Step { Text = text.Substring(pos) });
            return steps;
        }

        private static string ReadText(object t)
        {
            if (t is string s) return s;
            if (t is IEnumerable lines) return string.Join("\n", lines.Cast<object>().Select(Convert.ToString));
            return null;
        }

        private static string ReadShared(string path)
        {
            // Editors may still hold the file for a moment right after saving.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                        return sr.ReadToEnd();
                }
                catch (IOException) when (attempt < 5 && File.Exists(path))
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        private static readonly Dictionary<string, Keys> Aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            ["Esc"] = Keys.Escape, ["Del"] = Keys.Delete, ["Ins"] = Keys.Insert,
            ["PgUp"] = Keys.PageUp, ["PgDn"] = Keys.PageDown, ["PageDown"] = Keys.PageDown,
            ["Enter"] = Keys.Return, ["Backspace"] = Keys.Back,
            ["`"] = Keys.Oemtilde, ["Backtick"] = Keys.Oemtilde, ["Tilde"] = Keys.Oemtilde,
            ["-"] = Keys.OemMinus, ["Minus"] = Keys.OemMinus, ["="] = Keys.Oemplus, ["Equals"] = Keys.Oemplus, ["Plus"] = Keys.Oemplus,
            ["["] = Keys.OemOpenBrackets, ["]"] = Keys.OemCloseBrackets, [";"] = Keys.OemSemicolon,
            ["'"] = Keys.OemQuotes, [","] = Keys.Oemcomma, ["Comma"] = Keys.Oemcomma, ["."] = Keys.OemPeriod, ["Period"] = Keys.OemPeriod,
            ["/"] = Keys.OemQuestion, ["Slash"] = Keys.OemQuestion, ["\\"] = Keys.OemPipe, ["Backslash"] = Keys.OemPipe,
            ["NumPlus"] = Keys.Add, ["NumMinus"] = Keys.Subtract, ["NumStar"] = Keys.Multiply, ["NumSlash"] = Keys.Divide,
        };

        private static readonly HashSet<Keys> ModifierKeys = new HashSet<Keys>
        {
            Keys.ControlKey, Keys.LControlKey, Keys.RControlKey, Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey,
            Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin, Keys.Control, Keys.Shift, Keys.Alt,
        };

        public static bool TryParseKeys(string text, out int vk, out Mods mods, out string error)
        {
            vk = 0;
            mods = Mods.None;
            error = null;

            var parts = text.Split('+').Select(p => p.Trim()).ToList();
            if (parts.Any(p => p.Length == 0))
            {
                error = "empty part (to use the + key, write \"Plus\").";
                return false;
            }

            for (int i = 0; i < parts.Count - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= Mods.Ctrl; break;
                    case "alt": mods |= Mods.Alt; break;
                    case "shift": mods |= Mods.Shift; break;
                    case "win": case "windows": mods |= Mods.Win; break;
                    default:
                        error = $"\"{parts[i]}\" is not a modifier (use Ctrl, Alt, Shift, Win; only the last part is the key).";
                        return false;
                }
            }

            string keyName = parts[parts.Count - 1];
            Keys key;
            if (keyName.Length == 1 && keyName[0] >= '0' && keyName[0] <= '9')
                key = Keys.D0 + (keyName[0] - '0');
            else if (keyName.Length == 4 && keyName.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsDigit(keyName[3]))
                key = Keys.NumPad0 + (keyName[3] - '0');
            else if (Aliases.TryGetValue(keyName, out var aliased))
                key = aliased;
            else if (!keyName.All(char.IsDigit) && Enum.TryParse(keyName, true, out Keys parsed) && Enum.IsDefined(typeof(Keys), parsed))
                key = parsed;
            else
            {
                error = $"unknown key \"{keyName}\".";
                return false;
            }

            if (ModifierKeys.Contains(key) || (int)key <= 0 || (int)key > 0xFE)
            {
                error = "needs a non-modifier key at the end, e.g. Ctrl+1.";
                return false;
            }

            vk = (int)key;
            return true;
        }

        public static string Describe(int vk, Mods mods)
        {
            var parts = new List<string>();
            if (mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
            if (mods.HasFlag(Mods.Alt)) parts.Add("Alt");
            if (mods.HasFlag(Mods.Shift)) parts.Add("Shift");
            if (mods.HasFlag(Mods.Win)) parts.Add("Win");
            var k = (Keys)vk;
            parts.Add(k >= Keys.D0 && k <= Keys.D9 ? ((char)('0' + (k - Keys.D0))).ToString() : k.ToString());
            return string.Join("+", parts);
        }
    }
}
