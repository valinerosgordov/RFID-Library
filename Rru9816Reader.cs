using System;
using System.Text.RegularExpressions;

namespace LibraryTerminal
{
    /// <summary>
    /// RRU9816-USB: читает EPC-96 книжных меток и отдаёт событие OnEpcHex.
    /// Работает поверх базового SerialWorker (читает построчно по заданному newline).
    ///
    /// Задача класса:
    ///  - извлечь из входной строки последовательности ровно из 24 HEX-символов (EPC-96)
    ///  - подавить дребезг (не дублировать тот же EPC в коротком окне)
    ///  - отдать наружу событие OnEpcHex(epcHex)
    ///
    /// Примечание:
    ///  Если прошивка ридера меняет формат строки (префиксы/суффиксы),
    ///  чаще всего всё равно внутри будет 24 HEX-символа — регулярка их достанет.
    /// </summary>
    internal sealed class Rru9816Reader : SerialWorker
    {
        /// <summary>
        /// Срабатывает для каждого распознанного EPC-96 (строка из 24 HEX-символов).
        /// </summary>
        public event Action<string> OnEpcHex;

        // Ищем в тексте любые подряд идущие 24 шестнадцатеричных символа.
        private static readonly Regex EpcRegex =
            new Regex(@"([0-9A-F]{24})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Память для "антидребезга"
        private string _lastEpc;
        private DateTime _lastEpcTs = DateTime.MinValue;
        private readonly TimeSpan _dedupWindow = TimeSpan.FromMilliseconds(300);

        /// <param name="portName">COM-порт (например, "COM5" или auto:... через PortResolver)</param>
        /// <param name="baudRate">Скорость порта (обычно 115200)</param>
        /// <param name="newline">Разделитель строк (обычно "\r\n")</param>
        /// <param name="readTimeoutMs">Таймаут чтения</param>
        /// <param name="writeTimeoutMs">Таймаут записи</param>
        /// <param name="autoReconnectMs">Период автопереподключения</param>
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
        /// Вызывается базовым SerialWorker на каждую принятую строку.
        /// Здесь парсим EPC и шлём событие наружу.
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

                // "Антидребезг": не спамим одинаковым EPC в течение короткого окна
                var now = DateTime.UtcNow;
                if (_lastEpc == epc && (now - _lastEpcTs) < _dedupWindow)
                    continue;

                _lastEpc = epc;
                _lastEpcTs = now;

                var handler = OnEpcHex;
                if (handler != null) handler(epc);
            }
        }
    }
}
