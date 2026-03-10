using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace ScadaQTNN
{
    public static class Logger
    {
        private static readonly object _sync = new object();
        private static string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static string _currentFile;
        private static DateTime _currentDate = DateTime.MinValue;

        private static void EnsureOpen()
        {
            lock (_sync)
            {
                var today = DateTime.UtcNow.Date;
                if (_currentDate != today)
                {
                    _currentDate = today;
                    if (!Directory.Exists(_logDirectory))
                        Directory.CreateDirectory(_logDirectory);
                    _currentFile = Path.Combine(_logDirectory, $"scada_{_currentDate:yyyyMMdd}.log");
                }
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                EnsureOpen();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                lock (_sync)
                {
                    // console
                    Console.WriteLine(line);
                    // file append
                    File.AppendAllText(_currentFile, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Best-effort logger: swallow to avoid cascading failures
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(Exception ex, string context = null)
        {
            var msg = (context != null ? context + " - " : "") + ex.ToString();
            Write("ERROR", msg);
        }
    }
}
