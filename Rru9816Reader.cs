using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LibraryTerminal
{
    /// <summary>
    /// Чтение EPC с RRU9816 через нативную DLL (RRU9816.dll).
    /// Ридер работает в активном режиме: кладёт теги в буфер,
    /// мы забираем их через ReadActiveModeData в фоновой нити и поднимаем OnEpcHex.
    /// Сигнатуры P/Invoke взяты из RRU9816Demo (RWDev.cs).
    /// </summary>
    public sealed class Rru9816DllReader : IDisposable
    {
        public event Action<string> OnEpcHex;

        private readonly string _portName;     // "COM5" — если пусто, используем автопоиск
        private readonly int _baud;            // 9600..115200
        private readonly byte _comAdr;         // адрес устройства (обычно 0xFF)

        private volatile bool _running;
        private Thread _thread;

        private int _frmIdx = -1;              // хэндл (индекс) открытого порта внутри DLL
        private int _openedPortNum = 0;        // номер COM-порта, если открывали явно/авто

        private const string DLL = "RRU9816.dll";

        // ====== Точные сигнатуры из демо ======
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int OpenComPort(int Port,
                                              ref byte ComAddr,
                                              byte Baud,
                                              ref int PortHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int AutoOpenComPort(ref int Port,
                                                  ref byte ComAddr,
                                                  byte Baud,
                                                  ref int PortHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int CloseComPort(); // без параметров — закрывает текущее соединение

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int CloseSpecComPort(int Port); // при необходимости закрыть конкретный COM

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetWorkMode(ref byte ComAdr,
                                             byte Read_mode,     // 0: запрос/ответ; 1: активный режим (буфер)
                                             int frmComPortindex);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int ClearTagBuffer(ref byte ComAdr,
                                                 int frmComPortindex);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        private static extern int ReadActiveModeData(byte[] ScanModeData,
                                                     ref int ValidDatalength,
                                                     int frmComPortindex);
        // ======================================

        public Rru9816DllReader(string comPort, int baud = 115200, byte comAdr = 0xFF)
        {
            _portName = comPort;
            _baud = baud;
            _comAdr = comAdr;
        }

        public void Start()
        {
            if (_running) return;

            _frmIdx = -1;
            _openedPortNum = 0;

            byte comAdrLocal = _comAdr;

            // 0) Список портов для перебора
            var names = System.IO.Ports.SerialPort.GetPortNames(); // ["COM3","COM5",...]
            var list = new System.Collections.Generic.List<int>();
            foreach (var n in names)
            {
                int p = ExtractPortNumber(n);
                if (p > 0 && !list.Contains(p)) list.Add(p);
            }
            list.Sort();

            // Если явно задан порт — ставим его в голову списка
            if (!string.IsNullOrWhiteSpace(_portName))
            {
                int desired = ExtractPortNumber(_portName);
                if (desired <= 0) throw new InvalidOperationException($"RRU9816: неверный порт '{_portName}'.");
                list.Remove(desired);
                list.Insert(0, desired);
            }

            // Если список пуст — переберём 1..32
            if (list.Count == 0)
                for (int i = 1; i <= 32; i++) list.Add(i);

            // 1) Кандидаты кодов бодрейта (разные версии DLL по-разному кодируют 115200)
            byte[][] baudCandidates = (_baud >= 115200)
                ? new[] { new byte[] { 4 }, new byte[] { 5 }, new byte[] { 3 } }   // 115200: 4/5; 57600: 3
                : new[] { new byte[] { BaudToCode(_baud) }, new byte[] { 4 }, new byte[] { 3 } };

            int lastRet = -1;

            // 2) Перебираем порты и коды бодрейта
            foreach (var portNum in list)
            {
                foreach (var codes in baudCandidates)
                {
                    foreach (var baudCode in codes)
                    {
                        lastRet = OpenComPort(portNum, ref comAdrLocal, baudCode, ref _frmIdx);
                        if (lastRet == 0)
                        {
                            _openedPortNum = portNum;
                            goto OPEN_OK;
                        }
                    }
                }
            }

            // 3) Фолбэк: AutoOpen на те же коды
            foreach (var codes in baudCandidates)
            {
                foreach (var baudCode in codes)
                {
                    int autoPort = 0;
                    lastRet = AutoOpenComPort(ref autoPort, ref comAdrLocal, baudCode, ref _frmIdx);
                    if (lastRet == 0)
                    {
                        _openedPortNum = autoPort;
                        goto OPEN_OK;
                    }
                }
            }

            throw new InvalidOperationException(
                $"RRU9816: порт не найден/не открыт (последний ret={lastRet}). " +
                $"COM занято другим процессом или скорость не совпадает с настройкой устройства.");

        OPEN_OK:
            // Активный режим
            int retWM = SetWorkMode(ref comAdrLocal, 1 /*active*/, _frmIdx);
            if (retWM != 0)
                throw new InvalidOperationException($"RRU9816: SetWorkMode failed (ret={retWM}).");

            try { ClearTagBuffer(ref comAdrLocal, _frmIdx); } catch { }

            _running = true;
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "RRU9816-Poll" };
            _thread.Start();
        }



        private void ReadLoop()
        {
            var buf = new byte[4096];
            while (_running)
            {
                try
                {
                    int valid = 0;
                    int ret = ReadActiveModeData(buf, ref valid, _frmIdx);
                    if (ret == 0 && valid > 0)
                        TryExtractEpcs(buf, valid);
                } catch
                {
                    // не роняем поток из-за единичных сбоёв
                }
                Thread.Sleep(50);
            }
        }

        /// <summary>
        /// Простейший парсер: подряд идут EPC-блоки, у каждого первый байт — длина.
        /// Если у твоей прошивки формат иной — подстроим этот метод.
        /// </summary>
        private void TryExtractEpcs(byte[] data, int length)
        {
            int i = 0;
            while (i < length)
            {
                int epcLen = data[i];
                if (epcLen >= 8 && epcLen <= 64 && i + 1 + epcLen <= length)
                {
                    var epcBytes = new byte[epcLen];
                    Buffer.BlockCopy(data, i + 1, epcBytes, 0, epcLen);
                    SafeRaise(BytesToHex(epcBytes));
                    i += 1 + epcLen;
                }
                else i++;
            }
        }

        private void SafeRaise(string epc)
        {
            try { OnEpcHex?.Invoke(epc); } catch { }
        }

        private static string BytesToHex(byte[] arr)
        {
            var sb = new StringBuilder(arr.Length * 2);
            for (int i = 0; i < arr.Length; i++) sb.Append(arr[i].ToString("X2"));
            return sb.ToString();
        }

        private static byte BaudToCode(int baud)
        {
            // Таблица из SDK демо:
            // 0:9600, 1:19200, 2:38400, 3:57600, 4:115200
            switch (baud)
            {
                case 9600: return 0;
                case 19200: return 1;
                case 38400: return 2;
                case 57600: return 3;
                default: return 4; // 115200
            }
        }

        private static int ExtractPortNumber(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName)) return 0;
            var s = portName.Trim().ToUpperInvariant();
            if (s.StartsWith("COM")) s = s.Substring(3);
            return int.TryParse(s, out var n) ? n : 0;
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(300); } catch { }

            try
            {
                // У части SDK хватает CloseComPort() без параметров.
                // На всякий — если знаем номер порта, закрываем конкретный.
                if (_openedPortNum > 0)
                    CloseSpecComPort(_openedPortNum);
                else
                    CloseComPort();
            } catch { }

            _frmIdx = -1;
            _openedPortNum = 0;
        }
    }
}
