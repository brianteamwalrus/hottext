using System;
using System.Threading;
using System.Windows.Forms;

namespace HotText
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            using (var mutex = new Mutex(true, @"Local\HotText.SingleInstance", out bool created))
            {
                if (!created)
                {
                    MessageBox.Show("HotText is already running (see the system tray).", "HotText",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Optional: HotText.exe "C:\path\to\hotkeys.json"
                string configPath = args.Length > 0 ? args[0] : AppConfig.DefaultPath;
                Application.Run(new TrayContext(configPath));
            }
        }
    }
}
