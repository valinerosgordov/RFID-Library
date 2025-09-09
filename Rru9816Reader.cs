using System;
using System.Text.RegularExpressions;

namespace LibraryTerminal
{
    /// <summary>
    /// RRU9816-USB: читает EPC-96 и выдаёт событие OnEpcHex (24 HEX-символа).
    /// Базовый SerialWorker отдаёт сюда построчный ввод (разделённый newline).
    /// </summary>
    internal sealed class Rru9816Reader : SerialWorker
    {
        public event Action<string> OnEpcHex;

        private static readonly Regex EpcRegex =
            new Regex(@"([0-9A-F]{24})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private string _lastEpc;
        private DateTime _lastEpcTs = DateTime.MinValue;
        private readonly TimeSpan _dedupWindow = TimeSpan.FromMilliseconds(300);

        public Rru9816Reader(
            string portName,
            int baudRate,
            string newline,
            int readTimeoutMs,
            int writeTimeoutMs,
            int autoReconnectMs
        )
        : base(portName, baudRate, newline, readTimeoutMs, writeTimeoutMs, autoReconnectMs)
        {
        }

        /// <summary>
        /// Вызывается базовым классом на каждую принятую строку.
        /// </summary>
        protected override void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            string upper = line.ToUpperInvariant();
            var matches = EpcRegex.Matches(upper);
            if (matches.Count == 0) return;

            foreach (Match m in matches)
            {
                var epc = m.Groups[1].Value; // 24 HEX
                if (epc.Length != 24) continue;

                var now = DateTime.UtcNow;
                if (_lastEpc == epc && (now - _lastEpcTs) < _dedupWindow)
                    continue;

                _lastEpc = epc;
                _lastEpcTs = now;

                var h = OnEpcHex;
                if (h != null) h(epc);
            }
        }
    }
}
