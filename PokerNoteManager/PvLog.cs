using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PokerNoteManager
{
    /// <summary>
    /// Central, thread-safe logging for PokerVision HUD.
    /// Everything is appended to %LocalAppData%\PokerVisionHUD\pokervision_debug.log
    /// so crash reports, tracker output ([TRACK]) and database events ([DB]) end up in one file.
    /// </summary>
    public static class PvLog
    {
        private static readonly object _lock = new object();

        public static string LogDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokerVisionHUD");

        public static string LogFilePath { get; } = Path.Combine(LogDirectory, "pokervision_debug.log");

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                    RotateIfNeeded();
                    File.AppendAllText(LogFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }

        private const long MaxLogBytes = 2 * 1024 * 1024;      // a released program must not grow forever
        private static int _writesSinceSizeCheck;

        /// <summary>Keeps the log at a sane size by renaming it to .1 once it passes 2 MB.</summary>
        private static void RotateIfNeeded()
        {
            try
            {
                if (++_writesSinceSizeCheck < 200) return;      // do not stat the file on every line
                _writesSinceSizeCheck = 0;
                FileInfo info = new(LogFilePath);
                if (!info.Exists || info.Length < MaxLogBytes) return;

                string previous = LogFilePath + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(LogFilePath, previous);
            }
            catch { }
        }

        private static readonly Dictionary<string, DateTime> _throttleMap = new Dictionary<string, DateTime>();

        /// <summary>
        /// Logs at most once per <paramref name="seconds"/> for a given key.
        /// Used for noisy, repeated failures (e.g. per-seat OCR helpers) so the log stays readable.
        /// </summary>
        public static void Throttled(string key, string message, int seconds = 60)
        {
            try
            {
                lock (_lock)
                {
                    if (_throttleMap.TryGetValue(key, out DateTime last) && (DateTime.Now - last).TotalSeconds < seconds) return;
                    _throttleMap[key] = DateTime.Now;
                }
                Write(message);
            }
            catch { }
        }

        /// <summary>Logs an exception with inner exceptions and stack trace (never throws).</summary>
        public static void Error(string context, Exception? ex)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("!!! ERROR in ").Append(context).Append(" -> ")
                  .Append(ex?.GetType().Name ?? "unknown").Append(": ")
                  .Append(ex?.Message ?? "(no message)");

                Exception? inner = ex?.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    sb.Append(Environment.NewLine)
                      .Append("    caused by: ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }

                if (!string.IsNullOrEmpty(ex?.StackTrace))
                {
                    sb.Append(Environment.NewLine)
                      .Append("    ").Append(ex!.StackTrace!.Replace(Environment.NewLine, Environment.NewLine + "    "));
                }

                Write(sb.ToString());
            }
            catch { }
        }
    }
}
