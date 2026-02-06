using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using System;
using System.IO;
using System.Text;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// Xbox-safe logging backend that writes to TemporaryFolder/logs.
    /// Uses simple file I/O compatible with the Xbox UWP sandbox.
    /// Avoids ETW or other APIs that may be restricted on Xbox.
    /// </summary>
    public sealed class XboxLogTarget : ILogTarget
    {
        private readonly StreamWriter _writer;
        private readonly string _logFilePath;
        private readonly object _lock = new();
        private bool _isDisposed;

        public string Name { get; }

        public XboxLogTarget(string logsDirectory, string name = "XboxFile")
        {
            Name = name;

            Directory.CreateDirectory(logsDirectory);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _logFilePath = Path.Combine(logsDirectory, $"ryujinx_{timestamp}.log");

            _writer = new StreamWriter(
                new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }

        public void Log(object sender, LogEventArgs args)
        {
            if (_isDisposed)
            {
                return;
            }

            string formattedMessage = FormatMessage(args);

            lock (_lock)
            {
                if (!_isDisposed)
                {
                    _writer.WriteLine(formattedMessage);
                }
            }
        }

        private static string FormatMessage(LogEventArgs args)
        {
            StringBuilder sb = new();

            sb.Append(args.Time.ToString(@"hh\:mm\:ss\.fff"));
            sb.Append(" | ");
            sb.Append(args.Level.ToString().PadRight(7));
            sb.Append(" | ");
            sb.Append(args.ThreadName?.PadRight(15) ?? "unknown".PadRight(15));
            sb.Append(" | ");
            sb.Append(args.Message);

            if (args.Data is not null)
            {
                sb.Append(" - ");
                sb.Append(args.Data);
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            lock (_lock)
            {
                _isDisposed = true;

                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }
}
