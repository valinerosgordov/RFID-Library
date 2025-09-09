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
        // ===== FSM =====
        private enum Screen
        {
            S1_Menu,
            S2_WaitCardTake,
            S3_WaitBookTake,
            S4_WaitCardReturn,
            S5_WaitBookReturn,
            S6_Success,
            S7_BookRejected,
            S8_CardFail,
            S9_NoSpace
        }
        private enum Mode { None, Take, Return }

        private Screen _screen = Screen.S1_Menu;
        private Mode _mode = Mode.None;

        // --- UI timeouts per ТЗ ---
        private const int TIMEOUT_SEC_SUCCESS = 20;
        private const int TIMEOUT_SEC_ERROR = 20;
        private const int TIMEOUT_SEC_NO_SPACE = 20;
        private const int TIMEOUT_SEC_NO_TAG = 20;

        // Автовозврат на меню
        private readonly Timer _tick = new Timer { Interval = 250 };
        private DateTime? _deadline = null;

        // === Режимы ===
        private static readonly bool SIM_MODE = false; // железо активно
        private const bool DEMO_UI = true;             // показывать демо-кнопки
        private const bool DEMO_KEYS = true;           // горячие клавиши 1–4, F9

        // ===== Статусы 910^a =====
        private const string STATUS_IN_STOCK = "0"; // в фонде (можно выдавать)
        private const string STATUS_ISSUED = "1";   // выдано

        // ===== Сервисы/устройства =====
        private IrbisServiceManaged _svc;
        private BookReaderSerial _bookTake;    // книга (выдача)
        private BookReaderSerial _bookReturn;  // книга (возврат)
        private ArduinoClientSerial _ardu;     // контроллер шкафа/места

        // Вариант 1: ACR1281U-C1 (PC/SC)
        private Acr1281PcscReader _acr;        // карта (ACR1281U-C1, PC/SC)

        // RRU9816 (книжный EPC-ридер)
        private Rru9816Reader _rru;

        // фоновые задачи (чтобы не блокировать UI)
        private static Task OffUi(Action a) => Task.Run(a);
        private static Task<T> OffUi<T>(Func<T> f) => Task.Run(f);

        public MainForm()
        {
            InitializeComponent();
        }

        // === Хелперы для подключения ===
        private static string GetConnString()
        {
            var cfg = ConfigurationManager.AppSettings["ConnectionString"];
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg;
            return "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;DB=IBIS;";
        }
        private static string GetBooksDb()
        {
            return ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
        }

        // === Автоконнект к ИРБИС после показа окна + probe ===
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                await InitIrbisWithRetryAsync();
                await TestIrbisConnectionAsync();
            } catch { /* не роням UI */ }
        }

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
                        _svc.UseDatabase(db);
                    } catch
                    {
                        _svc.Connect(conn);
                        _svc.UseDatabase(db);
                    }
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
            this.KeyPreview = true;
            _tick.Tick += Tick_Tick;

            SetUiTexts();
            AddWaitIndicators();
            if (DEMO_UI) AddSimButtons();
            if (DEMO_UI) AddBackButtonForSim();

            ShowScreen(panelMenu);

            // --- Оборудование ---
            if (!SIM_MODE)
            {
                int readTo = int.Parse(ConfigurationManager.AppSettings["ReadTimeoutMs"] ?? "700");
                int writeTo = int.Parse(ConfigurationManager.AppSettings["WriteTimeoutMs"] ?? "700");
                int reconnMs = int.Parse(ConfigurationManager.AppSettings["AutoReconnectMs"] ?? "1500");
                int debounce = int.Parse(ConfigurationManager.AppSettings["DebounceMs"] ?? "250");

                try
                {
                    // КНИЖНЫЕ ридеры и ардуино по COM
                    string bookTakePort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookTakePort"] ?? ConfigurationManager.AppSettings["BookPort"]);
                    string bookRetPort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookReturnPort"] ?? ConfigurationManager.AppSettings["BookPort"]);
                    string arduinoPort = PortResolver.Resolve(ConfigurationManager.AppSettings["ArduinoPort"]);

                    int baudBookTake = int.Parse(ConfigurationManager.AppSettings["BaudBookTake"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudBookRet = int.Parse(ConfigurationManager.AppSettings["BaudBookReturn"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudArduino = int.Parse(ConfigurationManager.AppSettings["BaudArduino"] ?? "115200");

                    string nlBookTake = ConfigurationManager.AppSettings["NewLineBookTake"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlBookRet = ConfigurationManager.AppSettings["NewLineBookReturn"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlArduino = ConfigurationManager.AppSettings["NewLineArduino"] ?? "\n";

                    // Книжные ридеры (выдача/возврат)
                    if (!string.IsNullOrWhiteSpace(bookTakePort))
                    {
                        _bookTake = new BookReaderSerial(bookTakePort, baudBookTake, nlBookTake, readTo, writeTo, reconnMs, debounce);
                        _bookTake.OnTag += OnBookTagTake;
                        _bookTake.Start();
                    }

                    if (!string.IsNullOrWhiteSpace(bookRetPort))
                    {
                        if (_bookTake != null && bookRetPort == bookTakePort)
                        {
                            _bookReturn = _bookTake;
                        }
                        else
                        {
                            _bookReturn = new BookReaderSerial(bookRetPort, baudBookRet, nlBookRet, readTo, writeTo, reconnMs, debounce);
                            _bookReturn.Start();
                        }
                        _bookReturn.OnTag += OnBookTagReturn;
                    }

                    // Arduino — опционально
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

                // RRU9816-USB (EPC-ридер для книг)
                try
                {
                    string rruPort = PortResolver.Resolve(
                        ConfigurationManager.AppSettings["RruPort"]
                        ?? ConfigurationManager.AppSettings["BookTakePort"] // fallback
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
                        _rru = new Rru9816Reader(rruPort, rruBaud, rruNewline, readTo, writeTo, reconnMs);
                        _rru.OnEpcHex += OnRruEpc;
                        _rru.Start();
                    }
                } catch (Exception ex)
                {
                    MessageBox.Show("RRU9816: " + ex.Message, "RRU9816",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // КАРТА: ACR1281U-C1 через PC/SC (вариант 1)
                try
                {
                    _acr = new Acr1281PcscReader(); // сам выберет PICC 0
                    _acr.OnUid += uid => OnAnyCardUid(uid, "ACR1281");
                    _acr.Start();
                } catch (Exception ex)
                {
                    MessageBox.Show("PC/SC (ACR1281): " + ex.Message, "PC/SC",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

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

        // ===== Горячие клавиши (демо) =====
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.D1) { OnAnyCardUid("SIM_CARD", "SIM"); return true; }
            if (keyData == Keys.D2) { OnBookTagTake("SIM_BOOK_OK"); return true; }
            if (keyData == Keys.D3) { OnBookTagTake("SIM_BOOK_BAD"); return true; }
            if (keyData == Keys.D4) { OnBookTagReturn("SIM_BOOK_FULL"); return true; }
            if (keyData == Keys.F9) { _ = TestIrbisConnectionAsync(); return true; }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ===== Навигация экранов =====
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
            foreach (Control c in Controls)
            {
                var pn = c as Panel;
                if (pn != null) pn.Visible = false;
            }
            p.Dock = DockStyle.Fill;
            p.Visible = true;
            p.BringToFront();
        }

        // ===== Кнопки меню =====
        private void btnTakeBook_Click(object sender, EventArgs e)
        {
            _mode = Mode.Take;
            Switch(Screen.S2_WaitCardTake, panelWaitCardTake);
        }
        private void btnReturnBook_Click(object sender, EventArgs e)
        {
            _mode = Mode.Return;
            Switch(Screen.S4_WaitCardReturn, panelWaitCardReturn);
        }

        // ===== ЕДИНАЯ точка для ЛЮБОЙ карты (ACR/COM/SIM) =====
        private void OnAnyCardUid(string rawUid, string source)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source);
                return;
            }
            _ = OnAnyCardUidAsync(rawUid, source);
        }

        private async Task OnAnyCardUidAsync(string rawUid, string source)
        {
            string uid = NormalizeUid(rawUid);

            if (SIM_MODE)
            {
                if (_screen == Screen.S2_WaitCardTake) Switch(Screen.S3_WaitBookTake, panelScanBook);
                else if (_screen == Screen.S4_WaitCardReturn) Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
                return;
            }

            bool ok = await TryAuthorizeByTwoExprAsync(uid);

            if (!ok)
            {
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
                return;
            }

            if (_screen == Screen.S2_WaitCardTake) Switch(Screen.S3_WaitBookTake, panelScanBook);
            else if (_screen == Screen.S4_WaitCardReturn) Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
        }

        private async Task<bool> TryAuthorizeByTwoExprAsync(string uid)
        {
            try
            {
                return await OffUi(() => _svc.ValidateCard(uid));
            } catch
            {
                return false;
            }
        }

        private string NormalizeUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return "";
            bool strip = "true".Equals(ConfigurationManager.AppSettings["UidStripDelimiters"] ?? "true", StringComparison.OrdinalIgnoreCase);
            if (strip) uid = uid.Replace(":", "").Replace(" ", "").Replace("-", "");
            bool upper = "true".Equals(ConfigurationManager.AppSettings["UidUpperHex"] ?? "true", StringComparison.OrdinalIgnoreCase);
            if (upper) uid = uid.ToUpperInvariant();
            return uid;
        }

        // ===== Считывание книги (2 ридера) =====
        private void OnBookTagTake(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagTake), tag); return; }
            if (_screen == Screen.S3_WaitBookTake)
            {
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

        // ===== EPC от RRU9816 =====
        private void OnRruEpc(string epcHex)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnRruEpc), epcHex); return; }

            var bookKey = ResolveBookKey(epcHex);

            if (_screen == Screen.S3_WaitBookTake)
                _ = HandleTakeAsync(bookKey);
            else if (_screen == Screen.S5_WaitBookReturn)
                _ = HandleReturnAsync(bookKey);
        }

        // ===== Хелперы EPC → ключ экземпляра =====
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
        /// Если на вход прилетел EPC-96 (24 HEX) — парсим его через EpcParser и
        /// строим ключ экземпляра вида "LL-Serial". Иначе возвращаем исходную строку.
        /// </summary>
        private string ResolveBookKey(string tagOrEpc)
        {
            if (IsHex24(tagOrEpc))
            {
                var epc = EpcParser.Parse(tagOrEpc);
                if (epc != null && epc.Kind == TagKind.Book)
                {
                    return string.Format("{0:D2}-{1}", epc.LibraryCode, epc.Serial);
                }
                return tagOrEpc; // не книжная метка — пойдёт как есть
            }
            return tagOrEpc; // это уже инв/штрихкод/старый формат
        }

        private Task<bool> OpenBinAsync()
        {
            if (_ardu == null) return Task.FromResult(true);
            return OffUi<bool>(() => { _ardu.OpenBin(); return true; });
        }

        private Task<bool> HasSpaceAsync()
        {
            if (_ardu == null) return Task.FromResult(true);
            return OffUi<bool>(() => _ardu.HasSpace());
        }

        // --- Выдача (3 -> 6/7/8) ---
        private async Task HandleTakeAsync(string bookTag)
        {
            try
            {
                if (SIM_MODE)
                {
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

                var rec = await OffUi(() => _svc.FindOneByInvOrTag(bookTag));
                if (rec == null)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                var f910 = rec.Fields
                    .Where(f => f.Tag == "910")
                    .FirstOrDefault(f => string.Equals(f.GetFirstSubFieldText('h'), bookTag, StringComparison.OrdinalIgnoreCase));
                if (f910 == null)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                string status = f910.GetFirstSubFieldText('a') ?? string.Empty;
                bool canIssue = string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK;
                if (!canIssue)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                await OpenBinAsync();
                await OffUi(() => _svc.Set910StatusAndWrite(rec, STATUS_ISSUED, null, bookTag, null, true));
                lblSuccess.Text = "Книга выдана";
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch
            {
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // --- Возврат (5 -> 6/7/9) ---
        private async Task HandleReturnAsync(string bookTag)
        {
            try
            {
                if (SIM_MODE)
                {
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

                var rec = await OffUi(() => _svc.FindOneByInvOrTag(bookTag));
                if (rec == null)
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

                bool hasSpace = await HasSpaceAsync();
                if (!hasSpace)
                {
                    Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                    return;
                }

                await OffUi(() => _svc.Set910StatusAndWrite(rec, STATUS_IN_STOCK, null, bookTag, null, true));

                await OpenBinAsync();
                lblSuccess.Text = "Книга принята";
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch (Exception ex)
            {
                lblError.Text = "Ошибка возврата: " + ex.Message;
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // ===== UI =====
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

        // простая версия демо-кнопок
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

        // ===== Обёртка для обработчика из Designer (Click += this.TestIrbisConnection) =====
        private async void TestIrbisConnection(object sender, EventArgs e)
        {
            await TestIrbisConnectionAsync();
        }
    }
}
