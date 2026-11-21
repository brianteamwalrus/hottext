using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace HotText
{
    /// <summary>
    /// Small append-only diagnostic log (%APPDATA%\HotText\hottext.log). Writes happen on a background
    /// thread so logging from the keyboard hook never slows key handling. Never log typed text.
    /// </summary>
    internal static class Log
    {
        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotText", "hottext.log");

        private const long MaxBytes = 512 * 1024;
        private static readonly BlockingCollection<string> Queue = new BlockingCollection<string>();

        static Log()
        {
            new Thread(Writer) { IsBackground = true, Name = "HotText log" }.Start();
        }

        public static void Write(string message) =>
            Queue.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}");

        private static void Writer()
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                        File.Copy(FilePath, FilePath + ".old", true);
                    using (var w = new StreamWriter(FilePath, append: !(fi.Exists && fi.Length > MaxBytes)))
                    {
                        w.WriteLine(line);
                        while (Queue.TryTake(out var more)) w.WriteLine(more);
                    }
                }
                catch
                {
                    // Logging must never break the app.
                }
            }
        }
    }
}
