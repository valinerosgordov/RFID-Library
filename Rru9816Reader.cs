using System;
using System.IO;
using System.Text.RegularExpressions;

namespace LibraryTerminal
{
    /// <summary>
    /// RRU9816: читает EPC-96 (24 HEX) из построчного ASCII-потока SerialWorker.
    /// Извлекает первую подпоследовательность из ≥24 HEX, берёт первые 24,
    /// подавляет дребезг и поднимает событие OnEpcHex(epc24).
    /// Лог пишет и в epc.log, и дублирует в консоль.
    /// </summary>
    internal sealed class Rru9816Reader : SerialWorker
    {
        public event Action<string> OnEpcHex; // EPC (24 HEX, UPPER)

        private static readonly Regex RxHex24Plus =
            new Regex(@"([0-9A-Fa-f]{24,})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private string _lastEpc;
        private DateTime _lastEpcTs = DateTime.MinValue;
        private readonly TimeSpan _dedupWindow;

        private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "epc.log");

        public Rru9816Reader(
            string portName,
            int baudRate,
            string newline,
            int readTimeoutMs,
            int writeTimeoutMs,
            int autoReconnectMs,
            int debounceMs = 300
        )
        : base(portName, baudRate, newline, readTimeoutMs, writeTimeoutMs, autoReconnectMs)
        {
            _dedupWindow = TimeSpan.FromMilliseconds(Math.Max(50, debounceMs));
            SafeLog($"==== START RRU9816 Port={portName}, Baud={baudRate}, NL={newline} @ {DateTime.Now:HH:mm:ss} ====");
        }

        protected override void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            SafeLog($"[RAW] {DateTime.Now:HH:mm:ss.fff} {line}");

            var m = RxHex24Plus.Match(line);
            if (!m.Success) return;

            var hex = m.Groups[1].Value.ToUpperInvariant();
            if (hex.Length < 24) return;
            var epc = hex.Substring(0, 24);

            if (_lastEpc == epc && (DateTime.UtcNow - _lastEpcTs) < _dedupWindow) return;

            _lastEpc = epc;
            _lastEpcTs = DateTime.UtcNow;
            SafeLog($"[EPC] {DateTime.Now:HH:mm:ss.fff} {epc}");
            OnEpcHex?.Invoke(epc);
        }

        private static void SafeLog(string line)
        {
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
                Console.WriteLine(line);
            } catch { /* no-throw лог */ }
        }
    }
}