using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.RegularExpressions;

namespace LibraryTerminal
{
    public partial class MainForm : Form
    {
        // ===== FSM (машина состояний экранов, ТЗ: 9 логических экранов) =====
        private enum Screen
        {
            S1_Menu,          // Экран 1. Главное меню: "Взять" / "Вернуть"
            S2_WaitCardTake,  // Экран 2. Ожидание карты (сценарий "Взять")
            S3_WaitBookTake,  // Экран 3. Ожидание книжной метки для выдачи
            S4_WaitCardReturn,// Экран 4. Ожидание карты (сценарий "Вернуть")
            S5_WaitBookReturn,// Экран 5. Ожидание книжной метки для возврата
            S6_Success,       // Экран 6. Успех (выдача/возврат выполнен)
            S7_BookRejected,  // Экран 7. Метка не распознана / книга не найдена
            S8_CardFail,      // Экран 8. Ошибка карты/авторизации
            S9_NoSpace        // Экран 9. Нет свободного места (шкаф переполнен)
        }
        private enum Mode { None, Take, Return } // текущий сценарий пользователя

        private Screen _screen = Screen.S1_Menu;
        private Mode _mode = Mode.None;

        // --- Таймауты авто-возврата (ТЗ: 20–30 сек) ---
        private const int TIMEOUT_SEC_SUCCESS = 20; // после успеха
        private const int TIMEOUT_SEC_ERROR = 20; // после ошибок авторизации/общих
        private const int TIMEOUT_SEC_NO_SPACE = 20; // при переполнении
        private const int TIMEOUT_SEC_NO_TAG = 20; // метка не распознана

        // Автоматический возврат на экран 1 (меню)
        private readonly Timer _tick = new Timer { Interval = 250 };
        private DateTime? _deadline = null;

        // === Режимы разработки/демо ===
        private static readonly bool SIM_MODE = false; // true — без реального железа
        private const bool DEMO_UI = true;           // показать демо-кнопки на экранах
        private const bool DEMO_KEYS = true;           // горячие клавиши (1–4, F9)

        // ===== Статусы поля 910^a в записи ИРБИС (примерная договорённость) =====
        private const string STATUS_IN_STOCK = "0"; // в фонде (доступно для выдачи)
        private const string STATUS_ISSUED = "1"; // выдано читателю

        // ===== Сервисы/устройства (ТЗ: 3 типа считывателей + Arduino + БИС) =====
        private IrbisServiceManaged _svc;     // интеграция с ИРБИС (БИС)
        private BookReaderSerial _bookTake;   // книжный ридер (выдача, COM)
        private BookReaderSerial _bookReturn; // книжный ридер (возврат, COM)
        private ArduinoClientSerial _ardu;    // механика (Arduino Nano, COM)

        // Вариант карт: ACR1281U-C1 (PC/SC) — 1-2 типы считывателей карт по ТЗ
        private Acr1281PcscReader _acr;

        // 3-й тип считывателя по ТЗ: RRU9816 (книжные EPC-метки, COM)
        private Rru9816Reader _rru;

        // Вспомогательные фоновые задачи, чтобы не блокировать UI-поток
        private static Task OffUi(Action a) => Task.Run(a);
        private static Task<T> OffUi<T>(Func<T> f) => Task.Run(f);

        public MainForm()
        {
            InitializeComponent();
        }

        // === Хелперы чтения строк подключения из конфигурации ===
        private static string GetConnString()
        {
            // app.config: <add key="ConnectionString" value="host=...;port=...;user=...;password=...;DB=IBIS;" />
            var cfg = ConfigurationManager.AppSettings["ConnectionString"];
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg;
            // запасной дефолт
            return "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;DB=IBIS;";
        }
        private static string GetBooksDb()
        {
            // имя БД ИРБИС (по умолчанию IBIS)
            return ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
        }

        // === При показе окна: подключаемся к ИРБИС и делаем probe-запрос ===
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                await InitIrbisWithRetryAsync();  // несколько попыток коннекта
                await TestIrbisConnectionAsync(); // проверка запросом FindOneByInvOrTag
            } catch { /* не роняем UI, ошибки покажем ниже */ }
        }

        /// <summary>
        /// Подключение к ИРБИС с ретраями (ТЗ: устойчивость/обработка ошибок).
        /// </summary>
        private async Task InitIrbisWithRetryAsync()
        {
            if (SIM_MODE) { _svc = new IrbisServiceManaged(); return; }

            string conn = GetConnString();
            string db = GetBooksDb();
            _svc = new IrbisServiceManaged();
            Exception last = null;

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await OffUi(() => {
                        _svc.Connect(conn);
                        _svc.UseDatabase(db);
                    });
                    return; // успех
                } catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(1500);
                }
            }

            MessageBox.Show("Ошибка подключения к ИРБИС: " + (last == null ? "неизвестно" : last.Message),
                "IRBIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// Быстрый тест работоспособности соединения с ИРБИС (смена БД + пустой поиск).
        /// </summary>
        private async Task TestIrbisConnectionAsync()
        {
            try
            {
                string conn = GetConnString();
                string db = GetBooksDb();

                if (_svc == null) _svc = new IrbisServiceManaged();
                await OffUi(() => {
                    try
                    {
                        _svc.UseDatabase(db); // если сессия активна — просто выбираем БД
                    } catch
                    {
                        // если сессия умерла — переподключаемся
                        _svc.Connect(conn);
                        _svc.UseDatabase(db);
                    }
                    // пробный запрос (нам важен сам вызов к серверу)
                    var probe = Guid.NewGuid().ToString("N");
                    _svc.FindOneByInvOrTag(probe);
                });

                if (DEMO_UI)
                    MessageBox.Show("IRBIS: подключение OK", "IRBIS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex)
            {
                MessageBox.Show("IRBIS: " + ex.Message, "IRBIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // хоткеи и тики таймера автo-возврата
            this.KeyPreview = true;
            _tick.Tick += Tick_Tick;

            // базовые надписи/индикаторы/демо-кнопки
            SetUiTexts();
            AddWaitIndicators();
            if (DEMO_UI) AddSimButtons();
            if (DEMO_UI) AddBackButtonForSim();

            // стартуем с меню (Экран 1)
            ShowScreen(panelMenu);

            // --- Подъём железа (ТЗ: 3 вида ридеров + Arduino) ---
            if (!SIM_MODE)
            {
                // общие таймауты/паузы чтения/записи и авто-переподключение COM
                int readTo = int.Parse(ConfigurationManager.AppSettings["ReadTimeoutMs"] ?? "700");
                int writeTo = int.Parse(ConfigurationManager.AppSettings["WriteTimeoutMs"] ?? "700");
                int reconnMs = int.Parse(ConfigurationManager.AppSettings["AutoReconnectMs"] ?? "1500");
                int debounce = int.Parse(ConfigurationManager.AppSettings["DebounceMs"] ?? "250");

                try
                {
                    // Книжные ридеры (выдача/возврат) и Arduino — COM-порты
                    string bookTakePort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookTakePort"] ?? ConfigurationManager.AppSettings["BookPort"]);
                    string bookRetPort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookReturnPort"] ?? ConfigurationManager.AppSettings["BookPort"]);
                    string arduinoPort = PortResolver.Resolve(ConfigurationManager.AppSettings["ArduinoPort"]);

                    int baudBookTake = int.Parse(ConfigurationManager.AppSettings["BaudBookTake"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudBookRet = int.Parse(ConfigurationManager.AppSettings["BaudBookReturn"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudArduino = int.Parse(ConfigurationManager.AppSettings["BaudArduino"] ?? "115200");

                    string nlBookTake = ConfigurationManager.AppSettings["NewLineBookTake"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlBookRet = ConfigurationManager.AppSettings["NewLineBookReturn"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlArduino = ConfigurationManager.AppSettings["NewLineArduino"] ?? "\n";

                    // Ридер "выдачи"
                    if (!string.IsNullOrWhiteSpace(bookTakePort))
                    {
                        _bookTake = new BookReaderSerial(bookTakePort, baudBookTake, nlBookTake, readTo, writeTo, reconnMs, debounce);
                        _bookTake.OnTag += OnBookTagTake; // событие метки → сценарий "выдача"
                        _bookTake.Start();
                    }

                    // Ридер "возврата" (может совпадать с "выдачей", тогда делим один инстанс)
                    if (!string.IsNullOrWhiteSpace(bookRetPort))
                    {
                        if (_bookTake != null && bookRetPort == bookTakePort)
                        {
                            _bookReturn = _bookTake; // один и тот же физический порт
                        }
                        else
                        {
                            _bookReturn = new BookReaderSerial(bookRetPort, baudBookRet, nlBookRet, readTo, writeTo, reconnMs, debounce);
                            _bookReturn.Start();
                        }
                        _bookReturn.OnTag += OnBookTagReturn; // событие метки → сценарий "возврат"
                    }

                    // Arduino (открыть/принять/проверить место в шкафу)
                    if (!string.IsNullOrWhiteSpace(arduinoPort))
                    {
                        _ardu = new ArduinoClientSerial(arduinoPort, baudArduino, nlArduino, readTo, writeTo, reconnMs);
                        _ardu.Start();
                    }
                } catch (Exception ex)
                {
                    MessageBox.Show("Оборудование (COM): " + ex.Message, "COM",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // RRU9816-USB — третий считыватель по ТЗ (книжные EPC-метки, 24 HEX)
                try
                {
                    string rruPort = PortResolver.Resolve(
                        ConfigurationManager.AppSettings["RruPort"]
                        ?? ConfigurationManager.AppSettings["BookTakePort"] // fallback: использовать тот же COM, если нужно
                    );
                    int rruBaud = int.Parse(
                        ConfigurationManager.AppSettings["RruBaudRate"]
                        ?? ConfigurationManager.AppSettings["BaudBookTake"]
                        ?? ConfigurationManager.AppSettings["BaudBook"]
                        ?? "115200"
                    );
                    string rruNewline =
                        ConfigurationManager.AppSettings["NewLineRru"]
                        ?? ConfigurationManager.AppSettings["NewLineBookTake"]
                        ?? ConfigurationManager.AppSettings["NewLineBook"]
                        ?? "\r\n";

                    if (!string.IsNullOrWhiteSpace(rruPort))
                    {
                        // Важно: сигнатура конструктора под твой SerialWorker (6 аргументов)
                        _rru = new Rru9816Reader(rruPort, rruBaud, rruNewline, readTo, writeTo, reconnMs);
                        _rru.OnEpcHex += OnRruEpc; // EPC → наш обработчик
                        _rru.Start();
                    }
                } catch (Exception ex)
                {
                    MessageBox.Show("RRU9816: " + ex.Message, "RRU9816",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // Считыватель карт ACR1281 через PC/SC (вариант 1 из ТЗ)
                try
                {
                    _acr = new Acr1281PcscReader(); // выбирает первый доступный PICC
                    _acr.OnUid += uid => OnAnyCardUid(uid, "ACR1281"); // единая точка для карт
                    _acr.Start();
                } catch (Exception ex)
                {
                    MessageBox.Show("PC/SC (ACR1281): " + ex.Message, "PC/SC",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        /// <summary>
        /// Корректное закрытие COM/PCSC при выходе.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                if (_bookReturn != null && _bookReturn != _bookTake) _bookReturn.Dispose();
                if (_bookTake != null) _bookTake.Dispose();
            } catch { }
            try { if (_ardu != null) _ardu.Dispose(); } catch { }
            try { if (_acr != null) _acr.Dispose(); } catch { }
            try { if (_rru != null) _rru.Dispose(); } catch { }

            base.OnFormClosing(e);
        }

        // ===== Горячие клавиши для демо/отладки =====
        // 1: карта; 2: выдача OK; 3: выдача BAD; 4: возврат FULL; F9: проверка ИРБИС
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.D1) { OnAnyCardUid("SIM_CARD", "SIM"); return true; }
            if (keyData == Keys.D2) { OnBookTagTake("SIM_BOOK_OK"); return true; }
            if (keyData == Keys.D3) { OnBookTagTake("SIM_BOOK_BAD"); return true; }
            if (keyData == Keys.D4) { OnBookTagReturn("SIM_BOOK_FULL"); return true; }
            if (keyData == Keys.F9) { _ = TestIrbisConnectionAsync(); return true; }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ===== Переключение экранов + авто-возврат на меню =====
        private void Switch(Screen s, Panel panel, int? timeoutSeconds)
        {
            _screen = s;
            ShowScreen(panel);

            if (timeoutSeconds.HasValue)
            {
                _deadline = DateTime.Now.AddSeconds(timeoutSeconds.Value);
                _tick.Enabled = true;
            }
            else
            {
                _deadline = null;
                _tick.Enabled = false;
            }
        }
        private void Switch(Screen s, Panel panel) { Switch(s, panel, null); }

        private void Tick_Tick(object sender, EventArgs e)
        {
            // Возврат в меню по истечении таймаута (ТЗ: 20–30 сек)
            if (_deadline.HasValue && DateTime.Now >= _deadline.Value)
            {
                _deadline = null;
                _tick.Enabled = false;
                _mode = Mode.None;
                Switch(Screen.S1_Menu, panelMenu);
            }
        }

        private void ShowScreen(Panel p)
        {
            // скрыть все панели и показать только нужную
            foreach (Control c in Controls)
            {
                var pn = c as Panel;
                if (pn != null) pn.Visible = false;
            }
            p.Dock = DockStyle.Fill;
            p.Visible = true;
            p.BringToFront();
        }

        // ===== Кнопки экрана 1 (меню) — начало сценариев из ТЗ =====
        private void btnTakeBook_Click(object sender, EventArgs e)
        {
            // Шаг 2 (ТЗ): "Взять книгу" → ждём карту
            _mode = Mode.Take;
            Switch(Screen.S2_WaitCardTake, panelWaitCardTake);
        }
        private void btnReturnBook_Click(object sender, EventArgs e)
        {
            // Шаг 4 (ТЗ): "Вернуть книгу" → ждём карту
            _mode = Mode.Return;
            Switch(Screen.S4_WaitCardReturn, panelWaitCardReturn);
        }

        // ===== ЕДИНАЯ точка для ЛЮБОЙ карты (PC/SC, COM, симуляция) =====
        private void OnAnyCardUid(string rawUid, string source)
        {
            // приводим вызов к UI-потоку
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source);
                return;
            }
            _ = OnAnyCardUidAsync(rawUid, source);
        }

        /// <summary>
        /// Авторизация карты в ИРБИС и переход на следующий шаг сценария.
        /// </summary>
        private async Task OnAnyCardUidAsync(string rawUid, string source)
        {
            string uid = NormalizeUid(rawUid); // нормализуем UID (HEX, без разделителей)

            if (SIM_MODE)
            {
                // В демо-режиме просто двигаем FSM дальше
                if (_screen == Screen.S2_WaitCardTake) Switch(Screen.S3_WaitBookTake, panelScanBook);
                else if (_screen == Screen.S4_WaitCardReturn) Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
                return;
            }

            // Авторизация (ТЗ: запрос к БИС, обработка ошибок)
            bool ok = await TryAuthorizeByTwoExprAsync(uid);

            if (!ok)
            {
                // Экран 8 (ошибка карты/авторизации), затем авто-возврат
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
                return;
            }

            // Если авторизация прошла — переходим к сканированию книги
            if (_screen == Screen.S2_WaitCardTake) Switch(Screen.S3_WaitBookTake, panelScanBook);
            else if (_screen == Screen.S4_WaitCardReturn) Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
        }

        private async Task<bool> TryAuthorizeByTwoExprAsync(string uid)
        {
            try
            {
                // Обёртка над _svc.ValidateCard — запрос к БИС
                return await OffUi(() => _svc.ValidateCard(uid));
            } catch
            {
                return false;
            }
        }

        private string NormalizeUid(string uid)
        {
            // Опции нормализации задаются в app.config:
            // UidStripDelimiters=true (убрать двоеточия/пробелы/дефисы), UidUpperHex=true
            if (string.IsNullOrEmpty(uid)) return "";
            bool strip = "true".Equals(ConfigurationManager.AppSettings["UidStripDelimiters"] ?? "true", StringComparison.OrdinalIgnoreCase);
            if (strip) uid = uid.Replace(":", "").Replace(" ", "").Replace("-", "");
            bool upper = "true".Equals(ConfigurationManager.AppSettings["UidUpperHex"] ?? "true", StringComparison.OrdinalIgnoreCase);
            if (upper) uid = uid.ToUpperInvariant();
            return uid;
        }

        // ===== Получение книжной метки от COM-ридеров (выдача/возврат) =====
        private void OnBookTagTake(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagTake), tag); return; }
            if (_screen == Screen.S3_WaitBookTake)
            {
                // Преобразуем EPC-96 → наш ключ экземпляра (или оставим как есть)
                var bookKey = ResolveBookKey(tag);
                _ = HandleTakeAsync(bookKey);
            }
        }

        private void OnBookTagReturn(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagReturn), tag); return; }
            if (_screen == Screen.S5_WaitBookReturn)
            {
                var bookKey = ResolveBookKey(tag);
                _ = HandleReturnAsync(bookKey);
            }
        }

        // ===== EPC от RRU9816 (24 HEX) — третий ридер по ТЗ =====
        private void OnRruEpc(string epcHex)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnRruEpc), epcHex); return; }

            // EPC → ключ книги (например, "LL-Serial")
            var bookKey = ResolveBookKey(epcHex);

            if (_screen == Screen.S3_WaitBookTake)
                _ = HandleTakeAsync(bookKey);    // сценарий "выдача"
            else if (_screen == Screen.S5_WaitBookReturn)
                _ = HandleReturnAsync(bookKey);  // сценарий "возврат"
        }

        // ===== Хелперы EPC → ключ экземпляра =====

        // Проверяем, что строка похожа на EPC-96: 24 шестнадцатеричных символа
        private static bool IsHex24(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 24) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Если пришёл EPC-96 — парсим его (EpcParser) и строим устойчивый ключ экземпляра,
        /// например "LL-Serial". Если это не EPC (инв/штрихкод) — возвращаем как есть.
        /// </summary>
        private string ResolveBookKey(string tagOrEpc)
        {
            if (IsHex24(tagOrEpc))
            {
                var epc = EpcParser.Parse(tagOrEpc);
                if (epc != null && epc.Kind == TagKind.Book)
                {
                    // Формат ключа можно поменять под соглашение с ИРБИС
                    return string.Format("{0:D2}-{1}", epc.LibraryCode, epc.Serial);
                }
                // Если пришла не книжная метка (например, билет) — пусть обработается дальше как "не найдено"
                return tagOrEpc;
            }
            // Уже готовый инвентарный/штрихкод/старый формат
            return tagOrEpc;
        }

        // Команда механике: открыть бункер/люк (в коде ArduinoClientSerial)
        private Task<bool> OpenBinAsync()
        {
            if (_ardu == null) return Task.FromResult(true);
            return OffUi<bool>(() => { _ardu.OpenBin(); return true; });
        }

        // Запрос механике: есть ли свободное место (для возврата)
        private Task<bool> HasSpaceAsync()
        {
            if (_ardu == null) return Task.FromResult(true);
            return OffUi<bool>(() => _ardu.HasSpace());
        }

        // --- Сценарий "Выдача" (экраны 3 → 6/7/8 по ТЗ) ---
        private async Task HandleTakeAsync(string bookTag)
        {
            try
            {
                if (SIM_MODE)
                {
                    // Демо: "BAD" → ошибка метки; иначе — успех
                    if (bookTag.IndexOf("BAD", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                        return;
                    }
                    await OpenBinAsync();
                    lblSuccess.Text = "Книга выдана";
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                // 1) Находим запись книги по инв/штрихкоду или тегу (FindOneByInvOrTag)
                var rec = await OffUi(() => _svc.FindOneByInvOrTag(bookTag));
                if (rec == null)
                {
                    // Экран 7: метка не распознана/книга не найдена
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // 2) Ищем поле 910 с h == bookTag (соглашение хранения тега/ключа)
                var f910 = rec.Fields
                    .Where(f => f.Tag == "910")
                    .FirstOrDefault(f => string.Equals(f.GetFirstSubFieldText('h'), bookTag, StringComparison.OrdinalIgnoreCase));
                if (f910 == null)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // 3) Проверяем статус: можно ли выдавать
                string status = f910.GetFirstSubFieldText('a') ?? string.Empty;
                bool canIssue = string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK;
                if (!canIssue)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                // 4) Команда механике + фиксация выдачи в БИС (установка 910^a = "1")
                await OpenBinAsync();
                await OffUi(() => _svc.Set910StatusAndWrite(rec, STATUS_ISSUED, null, bookTag, null, true));

                // 5) Экран успеха → авто-возврат
                lblSuccess.Text = "Книга выдана";
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch
            {
                // Любая ошибка → экран 8 (ошибка) → авто-возврат
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // --- Сценарий "Возврат" (экраны 5 → 6/7/9 по ТЗ) ---
        private async Task HandleReturnAsync(string bookTag)
        {
            try
            {
                if (SIM_MODE)
                {
                    // Демо: "BAD" → сначала 7, затем 9; "FULL" → сразу 9; иначе — успех
                    if (bookTag.IndexOf("BAD", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Switch(Screen.S7_BookRejected, panelNoTag, null);
                        var hop = new Timer { Interval = 2000 };
                        hop.Tick += (s, e2) => {
                            hop.Stop(); hop.Dispose();
                            Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                        };
                        hop.Start();
                        return;
                    }
                    if (bookTag.IndexOf("FULL", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                        return;
                    }
                    await OpenBinAsync();
                    lblSuccess.Text = "Книга принята";
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                // 1) Находим книгу
                var rec = await OffUi(() => _svc.FindOneByInvOrTag(bookTag));
                if (rec == null)
                {
                    // Как в ТЗ: 7 → (пауза/действие) → 9 → авто-возврат
                    Switch(Screen.S7_BookRejected, panelNoTag, null);
                    var hop = new Timer { Interval = 2000 };
                    hop.Tick += (s, e2) => {
                        hop.Stop(); hop.Dispose();
                        Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                    };
                    hop.Start();
                    return;
                }

                // 2) Проверка свободного места (механика)
                bool hasSpace = await HasSpaceAsync();
                if (!hasSpace)
                {
                    Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                    return;
                }

                // 3) Фиксируем возврат (910^a = "0")
                await OffUi(() => _svc.Set910StatusAndWrite(rec, STATUS_IN_STOCK, null, bookTag, null, true));

                // 4) Команда механике и успех
                await OpenBinAsync();
                lblSuccess.Text = "Книга принята";
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch (Exception ex)
            {
                // Экран 8: ошибка возврата/общая ошибка
                lblError.Text = "Ошибка возврата: " + ex.Message;
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // ===== Тексты на экранах (минимальный UX) =====
        private void SetUiTexts()
        {
            lblTitleMenu.Text = "Библиотека\nФилиал №1";
            btnTakeBook.Text = "📕 Взять книгу";
            btnReturnBook.Text = "📗 Вернуть книгу";

            lblWaitCardTake.Text = "Приложите карту читателя (Петербуржца или читательский билет)";
            lblWaitCardReturn.Text = "Приложите карту читателя (Петербуржца или читательский билет)";
            lblScanBook.Text = "Поднесите книгу к считывателю";
            lblScanBookReturn.Text = "Поднесите возвращаемую книгу к считывателю";

            lblSuccess.Text = "Операция выполнена";
            lblNoTag.Text = "Метка книги не распознана. Попробуйте ещё раз";
            lblError.Text = "Карта не распознана или ошибка авторизации";
            lblOverflow.Text = "Нет свободного места в шкафу. Обратитесь к сотруднику";
        }

        // Индикация ожидания (бегущая линия) на нужных панелях
        private void AddWaitIndicators()
        {
            AddMarquee(panelWaitCardTake);
            AddMarquee(panelWaitCardReturn);
            AddMarquee(panelScanBook);
            AddMarquee(panelScanBookReturn);
        }

        private void AddMarquee(Panel p)
        {
            var pr = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                Dock = DockStyle.Bottom,
                Height = 12,
                MarqueeAnimationSpeed = 35
            };
            p.Controls.Add(pr);
            pr.BringToFront();
        }

        // Демо-кнопки для ручной симуляции сценариев (без железа)
        private void AddSimButtons()
        {
            var b1 = new Button { Text = "Сим-карта", Width = 140, Height = 36, Left = 20, Top = 20 };
            b1.Click += (s, e) => OnAnyCardUid("SIM_CARD", "SIM");
            panelWaitCardTake.Controls.Add(b1);

            var b2 = new Button { Text = "Сим-карта", Width = 140, Height = 36, Left = 20, Top = 20 };
            b2.Click += (s, e) => OnAnyCardUid("SIM_CARD", "SIM");
            panelWaitCardReturn.Controls.Add(b2);

            var b3 = new Button { Text = "Сим-книга OK", Width = 140, Height = 36, Left = 20, Top = 20 };
            b3.Click += (s, e) => OnBookTagTake("SIM_BOOK_OK");
            panelScanBook.Controls.Add(b3);

            var b4 = new Button { Text = "Сим-книга OK", Width = 140, Height = 36, Left = 20, Top = 20 };
            b4.Click += (s, e) => OnBookTagReturn("SIM_BOOK_OK");
            panelScanBookReturn.Controls.Add(b4);
        }

        // Кнопка "в меню" на всех экранах для удобной отладки
        private void AddBackButtonForSim()
        {
            var back = new Button
            {
                Text = "⟵ В меню",
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Width = 120,
                Height = 36,
                Left = this.ClientSize.Width - 130,
                Top = 8
            };
            back.Click += (s, e) => { _mode = Mode.None; Switch(Screen.S1_Menu, panelMenu); };

            foreach (Control c in Controls)
            {
                var p = c as Panel;
                if (p != null) p.Controls.Add(back);
            }
        }

        private void btnToMenu_Click(object sender, EventArgs e)
        {
            Switch(Screen.S1_Menu, panelMenu);
        }

        // Кнопка/пункт меню для ручной проверки ИРБИС
        private async void TestIrbisConnection(object sender, EventArgs e)
        {
            await TestIrbisConnectionAsync();
        }
    }
}
