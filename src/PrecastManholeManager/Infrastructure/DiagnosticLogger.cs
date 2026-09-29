using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Hatco.PrecastManholeManager.Infrastructure
{
    internal sealed class DiagnosticLogger : IDisposable
    {
        private readonly StreamWriter _writer;
        public string LogPath { get; }

        public DiagnosticLogger()
        {
            string logsFolder = OutputPathService.GetLogsFolder();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            LogPath = Path.Combine(logsFolder, $"PrecastManholeManager_{stamp}.txt");
            _writer = new StreamWriter(LogPath, false, new UTF8Encoding(true)) { AutoFlush = true };

            WriteHeader("SESSION START");
            Info($"Machine: {Environment.MachineName}");
            Info($"User: {Environment.UserName}");
            Info($"Process bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            Info($".NET: {Environment.Version}");
            Info($"Output folder: {logsFolder}");
        }

        public void WriteHeader(string title)
        {
            _writer.WriteLine();
            _writer.WriteLine(new string('=', 90));
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {title}");
            _writer.WriteLine(new string('=', 90));
        }

        public void Info(string message) => _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] INFO  {message}");
        public void Warn(string message) => _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] WARN  {message}");

        public void Error(string message, Exception ex = null)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ERROR {message}");
            if (ex != null)
            {
                _writer.WriteLine(ex.ToString());
                if (ex.InnerException != null)
                    _writer.WriteLine("INNER: " + ex.InnerException);
            }
        }

        public void Dispose()
        {
            WriteHeader("SESSION END");
            _writer?.Dispose();
        }
    }
}
