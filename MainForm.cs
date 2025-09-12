using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using WinFormsTimer = System.Windows.Forms.Timer;

// PC/SC
using PCSC;
using PCSC.Exceptions;
using PCSC.Iso7816;

// ИРБИС клиент для форматирования brief
using ManagedClient;

namespace LibraryTerminal
{
    public partial class MainForm : Form
    {
        private enum Screen { S1_Menu, S2_WaitCardTake, S3_WaitBookTake, S4_WaitCardReturn, S5_WaitBookReturn, S6_Success, S7_BookRejected, S8_CardFail, S9_NoSpace }
        private enum Mode { None, Take, Return }

        private Screen _screen = Screen.S1_Menu;
        private Mode _mode = Mode.None;

        private const int TIMEOUT_SEC_SUCCESS = 20;
        private const int TIMEOUT_SEC_ERROR = 20;
        private const int TIMEOUT_SEC_NO_SPACE = 20;
        private const int TIMEOUT_SEC_NO_TAG = 20;

        private readonly WinFormsTimer _tick = new WinFormsTimer { Interval = 250 };
        private DateTime? _deadline = null;

        // Эмуляция и Dry-run из App.config
        private static readonly bool USE_EMULATOR =
            bool.TryParse(ConfigurationManager.AppSettings["UseEmulator"], out var _emu) && _emu;

        private static readonly bool DRY_RUN =
            bool.TryParse(ConfigurationManager.AppSettings["DryRun"], out var _dry) && _dry;

        // БЫЛО: const true/true. Теперь управляется конфигом
        private static readonly bool DEMO_UI =
            bool.TryParse(ConfigurationManager.AppSettings["UseEmulator"], out var _emuUI) && _emuUI;

        private static readonly bool DEMO_KEYS =
            bool.TryParse(ConfigurationManager.AppSettings["DemoKeys"], out var _dk) && _dk;

        private const string STATUS_IN_STOCK = "0";
        private const string STATUS_ISSUED = "1";

        // IRBIS и оборудование
        private IrbisServiceManaged _svc;
        private BookReaderSerial _bookTake;
        private BookReaderSerial _bookReturn;
        private ArduinoClientSerial _ardu;

        private Acr1281PcscReader _acr;     // PC/SC для карт
        private Rru9816Reader _rru;         // COM для книжных меток
        private BookReaderSerial _iqrfid;   // IQRFID-5102 как COM-считыватель карт

        // Эмулятор: панель и элементы
        private Panel _emuPanel;
        private TextBox _emuUid;
        private TextBox _emuRfid;
        private Button _btnEmuCard;
        private Button _btnEmuBookTake;
        private Button _btnEmuBookReturn;
        private CheckBox _chkDryRun;

        private static Task OffUi(Action a) => Task.Run(a);
        private static Task<T> OffUi<T>(Func<T> f) => Task.Run(f);

        public MainForm()
        {
            InitializeComponent();
            this.KeyPreview = true;

            // F2 — диагностика PC/SC
            this.KeyDown += async (s, e) => {
                if (e.KeyCode == Keys.F2)
                {
                    await DebugProbeAllReaders();
                    e.Handled = true;
                }
            };
        }

        // ---------- конфиг / строка подключения ----------
        private static string GetConnString()
        {
            var cfg = ConfigurationManager.AppSettings["ConnectionString"]
                   ?? ConfigurationManager.AppSettings["connection-string"];
            if (!string.IsNullOrWhiteSpace(cfg)) return cfg;
            return "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;db=IBIS;";
        }
        private static string GetBooksDb() => ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";

        private static Tuple<string, int> ParseHostPort(string conn)
        {
            string host = "127.0.0.1"; int port = 6666;
            if (!string.IsNullOrEmpty(conn))
            {
                var parts = conn.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    var kv = p.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    var key = kv[0].Trim().ToLowerInvariant(); var val = kv[1].Trim();
                    if (key == "host" && !string.IsNullOrWhiteSpace(val)) host = val;
                    else if (key == "port") int.TryParse(val, out port);
                }
            }
            return Tuple.Create(host, port);
        }
        private static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs = 1200)
        {
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(host, port);
                    using (cts.Token.Register(() => { try { client.Close(); } catch { } }, false))
                    {
                        await task.ConfigureAwait(false);
                        return client.Connected;
                    }
                }
            } catch { return false; }
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                await InitIrbisWithRetryAsync();
                await TestIrbisConnectionAsync();
            } catch { }
        }

        private async Task InitIrbisWithRetryAsync()
        {
            string conn = GetConnString();
            string db = GetBooksDb();
            _svc = new IrbisServiceManaged();
            Exception last = null;

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await OffUi(() => { _svc.Connect(conn); _svc.UseDatabase(db); });
                    return;
                } catch (Exception ex) { last = ex; await Task.Delay(1500); }
            }

            MessageBox.Show("Ошибка подключения к ИРБИС: " + (last == null ? "неизвестно" : last.Message),
                "IRBIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // === ИСПРАВЛЕНО: используем сервисный TestConnection() ===
        private async Task TestIrbisConnectionAsync()
        {
            try
            {
                string conn = GetConnString();
                string db = GetBooksDb();

                var hp = ParseHostPort(conn);
                bool tcpOk = await ProbeTcpAsync(hp.Item1, hp.Item2, 1200);
                if (!tcpOk) throw new Exception($"Нет TCP-доступа к {hp.Item1}:{hp.Item2}");

                if (_svc == null) _svc = new IrbisServiceManaged();

                string info = await OffUi(() => {
                    try { _svc.UseDatabase(db); } catch { _svc.Connect(conn); _svc.UseDatabase(db); }
                    return _svc.TestConnection();
                });

                MessageBox.Show(this, info, "IRBIS: подключение", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex)
            {
                MessageBox.Show(this, "IRBIS: нет подключения.\n" + ex.Message,
                    "IRBIS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _tick.Tick += Tick_Tick;

            SetUiTexts();
            ShowScreen(panelMenu);

            if (DEMO_UI)
                AddBackButtonForSim();

            if (USE_EMULATOR)
            {
                InitializeEmulatorPanel();
                return; // железо/порты не стартуем
            }

            // ===== Ниже — инициализация реальных ридеров =====
            try
            {
                int readTo = int.Parse(ConfigurationManager.AppSettings["ReadTimeoutMs"] ?? "700");
                int writeTo = int.Parse(ConfigurationManager.AppSettings["WriteTimeoutMs"] ?? "700");
                int reconnMs = int.Parse(ConfigurationManager.AppSettings["AutoReconnectMs"] ?? "1500");
                int debounce = int.Parse(ConfigurationManager.AppSettings["DebounceMs"] ?? "250");

                // --- COM: книжный ридер (выдача/возврат) + Arduino
                try
                {
                    string bookTakePort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookTakePort"] ?? ConfigurationManager.AppSettings["BookPort"]);
                    string bookRetPort = PortResolver.Resolve(ConfigurationManager.AppSettings["BookReturnPort"] ?? ConfigurationManager.AppSettings["BookPort"]);
                    string arduinoPort = PortResolver.Resolve(ConfigurationManager.AppSettings["ArduinoPort"]);

                    int baudBookTake = int.Parse(ConfigurationManager.AppSettings["BaudBookTake"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudBookRet = int.Parse(ConfigurationManager.AppSettings["BaudBookReturn"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudArduino = int.Parse(ConfigurationManager.AppSettings["BaudArduino"] ?? "115200");

                    string nlBookTake = ConfigurationManager.AppSettings["NewLineBookTake"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlBookRet = ConfigurationManager.AppSettings["NewLineBookReturn"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlArduino = ConfigurationManager.AppSettings["NewLineArduino"] ?? "\n";

                    if (!string.IsNullOrWhiteSpace(bookTakePort))
                    {
                        _bookTake = new BookReaderSerial(bookTakePort, baudBookTake, nlBookTake, readTo, writeTo, reconnMs, debounce);
                        _bookTake.OnTag += OnBookTagTake;
                        _bookTake.Start();
                    }

                    if (!string.IsNullOrWhiteSpace(bookRetPort))
                    {
                        if (_bookTake != null && bookRetPort == bookTakePort) _bookReturn = _bookTake;
                        else
                        {
                            _bookReturn = new BookReaderSerial(bookRetPort, baudBookRet, nlBookRet, readTo, writeTo, reconnMs, debounce);
                            _bookReturn.Start();
                        }
                        _bookReturn.OnTag += OnBookTagReturn;
                    }

                    if (!string.IsNullOrWhiteSpace(arduinoPort))
                    {
                        _ardu = new ArduinoClientSerial(arduinoPort, baudArduino, nlArduino, readTo, writeTo, reconnMs);
                        _ardu.Start();
                    }
                } catch (Exception ex)
                {
                    MessageBox.Show("Оборудование (COM): " + ex.Message, "COM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // --- COM: RRU9816 (книги)
                try
                {
                    string rruPort = PortResolver.Resolve(ConfigurationManager.AppSettings["RruPort"] ?? ConfigurationManager.AppSettings["BookTakePort"]);
                    int rruBaud = int.Parse(ConfigurationManager.AppSettings["RruBaudRate"] ?? ConfigurationManager.AppSettings["BaudBookTake"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "115200");
                    string rruNewline = ConfigurationManager.AppSettings["NewLineRru"] ?? ConfigurationManager.AppSettings["NewLineBookTake"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";

                    if (!string.IsNullOrWhiteSpace(rruPort))
                    {
                        _rru = new Rru9816Reader(rruPort, rruBaud, rruNewline, readTo, writeTo, reconnMs);
                        _rru.OnEpcHex += OnRruEpc;
                        _rru.Start();
                    }
                } catch (Exception ex)
                {
                    MessageBox.Show("RRU9816: " + ex.Message, "RRU9816", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // --- COM: IQRFID-5102 (карты)
                try
                {
                    string iqPort = PortResolver.Resolve(ConfigurationManager.AppSettings["IqrfidPort"]);
                    int iqBaud = int.Parse(ConfigurationManager.AppSettings["BaudIqrfid"] ?? "9600");
                    string iqNewLn = ConfigurationManager.AppSettings["NewLineIqrfid"] ?? "\r\n";

                    if (!string.IsNullOrWhiteSpace(iqPort))
                    {
                        _iqrfid = new BookReaderSerial(iqPort, iqBaud, iqNewLn, readTo, writeTo, reconnMs, debounce);
                        _iqrfid.OnTag += uid => OnAnyCardUid(uid, "IQRFID-5102");
                        _iqrfid.Start();
                    }
                } catch (Exception ex)
                {
                    MessageBox.Show("IQRFID-5102: " + ex.Message, "IQRFID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                // --- PC/SC: ACR1281 (карты)
                try
                {
                    string preferred = FindPreferredPiccReaderName() ?? "";
                    if (!string.IsNullOrWhiteSpace(preferred))
                    {
                        try { _acr = new Acr1281PcscReader(preferred); } catch { _acr = new Acr1281PcscReader(); }
                    }
                    else
                    {
                        _acr = new Acr1281PcscReader();
                    }

                    _acr.OnUid += uid => OnAnyCardUid(uid, "ACR1281");
                    _acr.Start();
                } catch (Exception ex)
                {
                    MessageBox.Show("PC/SC (ACR1281): " + ex.Message, "PC/SC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            } catch (Exception ex)
            {
                MessageBox.Show("Инициализация ридеров: " + ex.Message, "Init", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { if (_bookReturn != null && _bookReturn != _bookTake) _bookReturn.Dispose(); } catch { }
            try { _bookTake?.Dispose(); } catch { }
            try { _ardu?.Dispose(); } catch { }
            try { _acr?.Dispose(); } catch { }
            try { _rru?.Dispose(); } catch { }
            try { _iqrfid?.Dispose(); } catch { }
            try { _svc?.Dispose(); } catch { }

            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (DEMO_KEYS)
            {
                if (keyData == Keys.D1) { OnAnyCardUid("SIM_CARD", "SIM"); return true; }
                if (keyData == Keys.D2) { OnBookTagTake("SIM_BOOK_OK"); return true; }
                if (keyData == Keys.D3) { OnBookTagTake("SIM_BOOK_BAD"); return true; }
                if (keyData == Keys.D4) { OnBookTagReturn("SIM_BOOK_FULL"); return true; }
            }

            if (keyData == Keys.F9) { _ = TestIrbisConnectionAsync(); return true; }

            if (USE_EMULATOR && _emuPanel?.Visible == true)
            {
                if (keyData == (Keys.Control | Keys.K)) { _btnEmuCard?.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.T)) { _btnEmuBookTake?.PerformClick(); return true; }
                if (keyData == (Keys.Control | Keys.R)) { _btnEmuBookReturn?.PerformClick(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Switch(Screen s, Panel panel, int? timeoutSeconds)
        {
            _screen = s;
            ShowScreen(panel);
            if (timeoutSeconds.HasValue)
            {
                _deadline = DateTime.Now.AddSeconds(timeoutSeconds.Value);
                _tick.Enabled = true;
            }
            else { _deadline = null; _tick.Enabled = false; }
        }
        private void Switch(Screen s, Panel panel) => Switch(s, panel, null);

        private void Tick_Tick(object sender, EventArgs e)
        {
            if (_deadline.HasValue && DateTime.Now >= _deadline.Value)
            {
                _deadline = null; _tick.Enabled = false; _mode = Mode.None;
                Switch(Screen.S1_Menu, panelMenu);
            }
        }

        private void ShowScreen(Panel p)
        {
            foreach (Control c in Controls) { if (c is Panel pn) pn.Visible = false; }
            p.Dock = DockStyle.Fill; p.Visible = true; p.BringToFront();
        }

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

        // ---------- обработка UID ----------
        private void OnAnyCardUid(string rawUid, string source)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, string>(OnAnyCardUid), rawUid, source); return; }
            _ = OnAnyCardUidAsync(rawUid, source);
        }

        private async Task OnAnyCardUidAsync(string rawUid, string source)
        {
            SafeAppend("uids.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {rawUid}");

            string uid = NormalizeUid(rawUid);

            bool ok = await OffUi(() => _svc.ValidateCard(uid));
            if (!ok) { Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR); return; }

            // показать краткую инфу о читателе
            string brief = await SafeGetReaderBriefAsync(_svc.LastReaderMfn);
            if (!string.IsNullOrWhiteSpace(brief))
            {
                lblReaderInfoTake.Text = brief;
                lblReaderInfoReturn.Text = brief;
            }
            else
            {
                lblReaderInfoTake.Text = $"Читатель идентифицирован (MFN: {_svc.LastReaderMfn})";
                lblReaderInfoReturn.Text = lblReaderInfoTake.Text;
            }

            if (_screen == Screen.S2_WaitCardTake) Switch(Screen.S3_WaitBookTake, panelScanBook);
            else if (_screen == Screen.S4_WaitCardReturn) Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
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

        // ---------- книги ----------
        private void OnBookTagTake(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagTake), tag); return; }
            if (_screen == Screen.S3_WaitBookTake) { var bookKey = ResolveBookKey(tag); _ = HandleTakeAsync(bookKey); }
        }
        private void OnBookTagReturn(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagReturn), tag); return; }
            if (_screen == Screen.S5_WaitBookReturn) { var bookKey = ResolveBookKey(tag); _ = HandleReturnAsync(bookKey); }
        }
        private void OnRruEpc(string epcHex)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnRruEpc), epcHex); return; }
            var bookKey = ResolveBookKey(epcHex);
            if (_screen == Screen.S3_WaitBookTake) _ = HandleTakeAsync(bookKey);
            else if (_screen == Screen.S5_WaitBookReturn) _ = HandleReturnAsync(bookKey);
        }

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
        private string ResolveBookKey(string tagOrEpc)
        {
            if (IsHex24(tagOrEpc))
            {
                var epc = EpcParser.Parse(tagOrEpc);
                if (epc != null && epc.Kind == TagKind.Book) return $"{epc.LibraryCode:D2}-{epc.Serial}";
                return tagOrEpc;
            }
            return tagOrEpc;
        }

        private Task<bool> OpenBinAsync() => _ardu == null ? Task.FromResult(true) : OffUi<bool>(() => { _ardu.OpenBin(); return true; });
        private Task<bool> HasSpaceAsync() => _ardu == null ? Task.FromResult(true) : OffUi<bool>(() => _ardu.HasSpace());

        // ====== ВЫДАЧА ======
        private async Task HandleTakeAsync(string bookTag)
        {
            try
            {
                // ИСПРАВЛЕНО: ищем книгу по RFID (HIN)
                var rec = await OffUi(() => _svc.FindOneByBookRfid(bookTag));
                if (rec == null) { Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG); return; }

                var f910 = rec.Fields.Where(f => f.Tag == "910")
                                     .FirstOrDefault(f => string.Equals(f.GetFirstSubFieldText('h'), bookTag, StringComparison.OrdinalIgnoreCase));
                if (f910 == null) { Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG); return; }

                string status = f910.GetFirstSubFieldText('a') ?? string.Empty;
                bool canIssue = string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK;
                if (!canIssue) { Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG); return; }

                if ((_chkDryRun?.Checked ?? false) || DRY_RUN)
                {
                    lblSuccess.Text = "Dry-run: найдены читатель и книга (без записи в БД)";
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                bool okSet = await OffUi(() => _svc.UpdateBook910StatusByRfidStrict(rec, bookTag, STATUS_ISSUED, null));
                if (!okSet) { Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG); return; }

                await OffUi(() =>
                    _svc.AppendRdr40OnIssue(
                        _svc.LastReaderMfn,
                        rec,
                        bookTag,
                        ConfigurationManager.AppSettings["MaskMrg"] ?? "09",
                        _svc.CurrentLogin ?? "terminal",
                        ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS"
                    )
                );

                await OpenBinAsync();
                lblSuccess.Text = "Книга выдана";
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch
            {
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // ====== ВОЗВРАТ ======
        private async Task HandleReturnAsync(string bookTag)
        {
            try
            {
                // ИСПРАВЛЕНО: ищем книгу по RFID (HIN)
                var rec = await OffUi(() => _svc.FindOneByBookRfid(bookTag));
                if (rec == null)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, null);
                    var hop = new WinFormsTimer { Interval = 2000 };
                    hop.Tick += (s, e2) => { hop.Stop(); hop.Dispose(); Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE); };
                    hop.Start();
                    return;
                }

                bool hasSpace = await HasSpaceAsync();
                if (!hasSpace) { Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE); return; }

                if ((_chkDryRun?.Checked ?? false) || DRY_RUN)
                {
                    lblSuccess.Text = "Dry-run: книга найдена (возврат без записи в БД)";
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                bool okSet = await OffUi(() => _svc.UpdateBook910StatusByRfidStrict(rec, bookTag, STATUS_IN_STOCK, null));
                if (!okSet) { Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG); return; }

                await OffUi(() =>
                    _svc.CompleteRdr40OnReturn(
                        bookTag,
                        ConfigurationManager.AppSettings["MaskMrg"] ?? "09",
                        _svc.CurrentLogin ?? "terminal"
                    )
                );

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

        private void AddBackButtonForSim()
        {
            var back = new Button { Text = "⟵ В меню", Anchor = AnchorStyles.Top | AnchorStyles.Right, Width = 120, Height = 36, Left = this.ClientSize.Width - 130, Top = 8 };
            back.Click += (s, e) => { _mode = Mode.None; Switch(Screen.S1_Menu, panelMenu); };
            foreach (Control c in Controls) if (c is Panel p) p.Controls.Add(back);
        }

        private void btnToMenu_Click(object sender, EventArgs e) => Switch(Screen.S1_Menu, panelMenu);

        private async void TestIrbisConnection(object sender, EventArgs e) => await TestIrbisConnectionAsync();

        // ======= Эмулятор: панель =======
        private void InitializeEmulatorPanel()
        {
            _emuPanel = new Panel { Height = 72, Dock = DockStyle.Bottom };
            _emuUid = new TextBox { Left = 8, Top = 8, Width = 260 };
            _emuRfid = new TextBox { Left = 8, Top = 38, Width = 260 };

            _btnEmuCard = new Button { Left = 276, Top = 6, Width = 180, Height = 26, Text = "Эмулировать КАРТУ" };
            _btnEmuBookTake = new Button { Left = 276, Top = 36, Width = 180, Height = 26, Text = "Эмулировать КНИГУ (ВЫДАЧА)" };
            _btnEmuBookReturn = new Button { Left = 462, Top = 36, Width = 200, Height = 26, Text = "Эмулировать КНИГУ (ВОЗВРАТ)" };

            _chkDryRun = new CheckBox { Left = 462, Top = 8, Width = 160, Text = "Dry-run (без записи)" };
            _chkDryRun.Checked = DRY_RUN;

            _btnEmuCard.Click += async (_, __) => {
                var uid = _emuUid.Text?.Trim();
                if (string.IsNullOrEmpty(uid)) { MessageBox.Show("Введите UID карты"); return; }
                await OnAnyCardUidAsync(uid, "EMU");
            };

            _btnEmuBookTake.Click += async (_, __) => {
                var tag = _emuRfid.Text?.Trim();
                if (string.IsNullOrEmpty(tag)) { MessageBox.Show("Введите RFID книги"); return; }
                await HandleTakeAsync(ResolveBookKey(tag));
            };

            _btnEmuBookReturn.Click += async (_, __) => {
                var tag = _emuRfid.Text?.Trim();
                if (string.IsNullOrEmpty(tag)) { MessageBox.Show("Введите RFID книги"); return; }
                await HandleReturnAsync(ResolveBookKey(tag));
            };

            _emuPanel.Controls.AddRange(new Control[] { _emuUid, _emuRfid, _btnEmuCard, _btnEmuBookTake, _btnEmuBookReturn, _chkDryRun });
            this.Controls.Add(_emuPanel);
        }

        // ======= PC/SC: утилиты =======

        private static void SafeAppend(string path, string line)
        {
            try { System.IO.File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private string FindPreferredPiccReaderName()
        {
            try
            {
                using (var ctx = ContextFactory.Instance.Establish(SCardScope.System))
                {
                    var readers = ctx.GetReaders();
                    if (readers == null || readers.Length == 0) return null;

                    var picc = readers.FirstOrDefault(r =>
                        r.IndexOf("PICC", StringComparison.OrdinalIgnoreCase) >= 0
                        || r.IndexOf("Contactless", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!string.IsNullOrWhiteSpace(picc)) return picc;

                    var anyAcr = readers.FirstOrDefault(r => r.IndexOf("ACR1281", StringComparison.OrdinalIgnoreCase) >= 0);
                    return anyAcr ?? readers.First();
                }
            } catch { return null; }
        }

        private static void DiagLog(string msg)
        {
            SafeAppend("pcsc_diag.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
        }

        private async Task DebugProbeAllReaders()
        {
            await Task.Yield();

            var sb = new StringBuilder();
            sb.AppendLine("=== PC/SC DIAG ===");
            DiagLog("=== START ===");

            try
            {
                using (var ctx = ContextFactory.Instance.Establish(SCardScope.System))
                {
                    var readers = ctx.GetReaders();
                    if (readers == null || readers.Length == 0)
                    {
                        MessageBox.Show("PC/SC: ридеры не найдены", "Диагностика",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DiagLog("Нет ридеров.");
                        return;
                    }

                    sb.AppendLine("Найдены ридеры:");
                    for (int i = 0; i < readers.Length; i++)
                    {
                        sb.AppendLine($"  {i}: {readers[i]}");
                        DiagLog($"Reader[{i}]: {readers[i]}");
                    }

                    sb.AppendLine();
                    sb.AppendLine("Пробую получить UID (APDU FF CA 00 00 00)...");
                    var apdu = new CommandApdu(IsoCase.Case2Short, SCardProtocol.Any)
                    {
                        CLA = 0xFF,
                        INS = 0xCA,
                        P1 = 0x00,
                        P2 = 0x00,
                        Le = 0x00
                    };

                    foreach (var reader in readers)
                    {
                        sb.AppendLine($"--- {reader} ---");
                        DiagLog($"Connect: {reader}");

                        try
                        {
                            using (var isoReader = new IsoReader(ctx, reader, SCardShareMode.Shared, SCardProtocol.Any, false))
                            {
                                var response = isoReader.Transmit(apdu);
                                var sw = (response.SW1 << 8) | response.SW2;
                                if (sw == 0x9000)
                                {
                                    var uid = BitConverter.ToString(response.GetData()).Replace("-", "");
                                    sb.AppendLine($"UID: {uid} (OK)");
                                    DiagLog($"UID OK: {reader} UID={uid}");
                                }
                                else
                                {
                                    sb.AppendLine($"SW={sw:X4} (нет карты или команда не поддерживается)");
                                    DiagLog($"SW={sw:X4} {reader}");
                                }
                            }
                        } catch (PCSCException ex)
                        {
                            sb.AppendLine($"PCSC: {ex.SCardError} ({ex.Message})");
                            DiagLog($"PCSC EX: {reader} -> {ex.SCardError} {ex.Message}");
                        } catch (Exception ex)
                        {
                            sb.AppendLine($"ERR: {ex.Message}");
                            DiagLog($"GEN EX: {reader} -> {ex.Message}");
                        }
                    }

                    sb.AppendLine();
                    sb.AppendLine("Подсказка: используем ридер, в названии которого есть 'PICC' или 'Contactless'.");
                }
            } catch (Exception ex)
            {
                sb.AppendLine("FATAL: " + ex.Message);
                DiagLog("FATAL: " + ex);
            }
            finally
            {
                DiagLog("=== END ===");
            }

            MessageBox.Show(sb.ToString(), "Диагностика PC/SC (F2)",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Получение brief читателя по MFN
        private async Task<string> SafeGetReaderBriefAsync(int mfn)
        {
            try
            {
                if (mfn <= 0) return null;

                return await OffUi(() => {
                    using (var client = new ManagedClient64())
                    {
                        client.ParseConnectionString(GetConnString());
                        client.Connect();
                        client.PushDatabase("RDR");
                        var brief = client.FormatRecord("@brief", mfn);
                        client.PopDatabase();
                        return brief;
                    }
                });
            } catch
            {
                return null;
            }
        }
    }
}
