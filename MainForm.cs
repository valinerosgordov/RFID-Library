using System;
using System.Configuration;
using System.Linq;
using System.Windows.Forms;

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
        private readonly Timer _tick = new Timer { Interval = 1000 };
        private DateTime? _deadline = null;

        // === Демо-режим (для железа — false) ===
        private static readonly bool SIM_MODE = false;

        // ===== Статусы 910^a (пример) =====
        private const string STATUS_IN_STOCK = "0"; // в фонде (можно выдавать)
        private const string STATUS_ISSUED = "1"; // выдано

        // ===== Сервисы/устройства =====
        private IrbisServiceManaged _svc;
        private CardReaderSerial _card;        // карта
        private BookReaderSerial _bookTake;    // книга (выдача)
        private BookReaderSerial _bookReturn;  // книга (возврат)
        private ArduinoClientSerial _ardu;        // контроллер шкафа/места

        public MainForm()
        {
            InitializeComponent();
        }

        // Тест IRBIS
        private void TestIrbisConnection()
        {
            try
            {
                if (_svc == null)
                    throw new Exception("Сервис IRBIS не инициализирован.");

                string probe = Guid.NewGuid().ToString("N");
                try { _svc.UseDatabase("KNIGA"); } catch { }

                var _ = _svc.FindByInvOrTag(probe);
                MessageBox.Show("IRBIS: подключение OK", "IRBIS",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex)
            {
                MessageBox.Show("IRBIS: ошибка подключения\n" + ex.Message, "IRBIS",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            this.KeyPreview = true;

            _tick.Interval = 250;
            _tick.Tick += Tick_Tick;

            SetUiTexts();
            AddWaitIndicators();

            if (SIM_MODE) AddSimButtons();
            AddBackButtonForSim();

            ShowScreen(panelMenu);

            // --- IRBIS ---
            _svc = new IrbisServiceManaged();
            try
            {
                _svc.Connect("host=192.168.56.1;port=6666;user=MASTER;password=MASTERKEY;DB=IBIS;");
                _svc.UseDatabase("IBIS");
            } catch (Exception ex)
            {
                if (!SIM_MODE)
                    MessageBox.Show("Ошибка подключения к ИРБИС: " + ex.Message, "IRBIS",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            // --- Оборудование ---
            if (!SIM_MODE)
            {
                try
                {
                    // ПОРТЫ: можно указать явно (COM5) или auto:VID_xxxx&PID_yyyy,index=0
                    string cardPort = PortResolver.Resolve(ConfigurationManager.AppSettings["CardPort"]);
                    string bookTakePort = PortResolver.Resolve(
                                              ConfigurationManager.AppSettings["BookTakePort"]
                                              ?? ConfigurationManager.AppSettings["BookPort"]);
                    string bookRetPort = PortResolver.Resolve(
                                              ConfigurationManager.AppSettings["BookReturnPort"]
                                              ?? ConfigurationManager.AppSettings["BookPort"]);
                    string arduinoPort = PortResolver.Resolve(ConfigurationManager.AppSettings["ArduinoPort"]);

                    int baudCard = int.Parse(ConfigurationManager.AppSettings["BaudCard"] ?? "9600");
                    int baudBookTake = int.Parse(ConfigurationManager.AppSettings["BaudBookTake"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudBookRet = int.Parse(ConfigurationManager.AppSettings["BaudBookReturn"] ?? ConfigurationManager.AppSettings["BaudBook"] ?? "9600");
                    int baudArduino = int.Parse(ConfigurationManager.AppSettings["BaudArduino"] ?? "115200");

                    string nlCard = ConfigurationManager.AppSettings["NewLineCard"] ?? "\r\n";
                    string nlBookTake = ConfigurationManager.AppSettings["NewLineBookTake"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlBookRet = ConfigurationManager.AppSettings["NewLineBookReturn"] ?? ConfigurationManager.AppSettings["NewLineBook"] ?? "\r\n";
                    string nlArduino = ConfigurationManager.AppSettings["NewLineArduino"] ?? "\n";

                    int readTo = int.Parse(ConfigurationManager.AppSettings["ReadTimeoutMs"] ?? "700");
                    int writeTo = int.Parse(ConfigurationManager.AppSettings["WriteTimeoutMs"] ?? "700");
                    int reconnMs = int.Parse(ConfigurationManager.AppSettings["AutoReconnectMs"] ?? "1500");
                    int debounce = int.Parse(ConfigurationManager.AppSettings["DebounceMs"] ?? "500");

                    if (string.IsNullOrEmpty(cardPort)) throw new Exception("Не найден порт карт-ридера");
                    if (string.IsNullOrEmpty(bookTakePort)) throw new Exception("Не найден порт ридера книг (выдача)");
                    if (string.IsNullOrEmpty(bookRetPort)) throw new Exception("Не найден порт ридера книг (возврат)");

                    _card = new CardReaderSerial(cardPort, baudCard, nlCard, readTo, writeTo, reconnMs, debounce);
                    _bookTake = new BookReaderSerial(bookTakePort, baudBookTake, nlBookTake, readTo, writeTo, reconnMs, debounce);
                    _bookReturn = (bookRetPort == bookTakePort)
                                    ? _bookTake
                                    : new BookReaderSerial(bookRetPort, baudBookRet, nlBookRet, readTo, writeTo, reconnMs, debounce);
                    if (!string.IsNullOrEmpty(arduinoPort))
                        _ardu = new ArduinoClientSerial(arduinoPort, baudArduino, nlArduino, readTo, writeTo, reconnMs);

                    _card.OnUid += OnCardUid;
                    _bookTake.OnTag += OnBookTagTake;
                    if (_bookReturn != _bookTake) _bookReturn.OnTag += OnBookTagReturn;

                    _card.Start();
                    _bookTake.Start();
                    if (_bookReturn != _bookTake) _bookReturn.Start();
                    _ardu?.Start();
                } catch (Exception ex)
                {
                    MessageBox.Show("Оборудование: " + ex.Message, "COM",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {
                MessageBox.Show(
                    "Демо-режим:\n" +
                    "1 — карта\n2 — книга (ОК)\n3 — книга (ошибка)\n4 — книга (нет места)\nF9 — тест IRBIS",
                    "Demo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        } // конец MainForm_Load

        // ===== Симуляторы (только для SIM_MODE) =====
        private void AddSimButtons()
        {
            // S2 — карта (выдача)
            btnSimCardTake = new Button
            {
                Text = "Симулировать карту",
                Size = new System.Drawing.Size(240, 48),
                Location = new System.Drawing.Point((800 - 240) / 2, 420),
                Font = new System.Drawing.Font("Segoe UI", 12F)
            };
            btnSimCardTake.Click += (s, e) => OnCardUid("SIM_CARD");
            panelWaitCardTake.Controls.Add(btnSimCardTake);

            // S4 — карта (возврат)
            btnSimCardReturn = new Button
            {
                Text = "Симулировать карту",
                Size = new System.Drawing.Size(240, 48),
                Location = new System.Drawing.Point((800 - 240) / 2, 420),
                Font = new System.Drawing.Font("Segoe UI", 12F)
            };
            btnSimCardReturn.Click += (s, e) => OnCardUid("SIM_CARD");
            panelWaitCardReturn.Controls.Add(btnSimCardReturn);

            // S3 — книга (выдача)
            btnSimBookTake = new Button
            {
                Text = "Симулировать книгу (ОК)",
                Size = new System.Drawing.Size(240, 48),
                Location = new System.Drawing.Point((800 - 240) / 2, 420),
                Font = new System.Drawing.Font("Segoe UI", 12F)
            };
            btnSimBookTake.Click += (s, e) => OnBookTagTake("SIM_BOOK_OK");
            panelScanBook.Controls.Add(btnSimBookTake);

            // S5 — книга (возврат)
            btnSimBookReturn = new Button
            {
                Text = "Симулировать книгу (ОК)",
                Size = new System.Drawing.Size(240, 48),
                Location = new System.Drawing.Point((800 - 240) / 2, 420),
                Font = new System.Drawing.Font("Segoe UI", 12F)
            };
            btnSimBookReturn.Click += (s, e) => OnBookTagReturn("SIM_BOOK_OK");
            panelScanBookReturn.Controls.Add(btnSimBookReturn);

            // Доп.кнопки ошибок
            var bBad = new Button
            {
                Text = "Книга не принята",
                Size = new System.Drawing.Size(200, 40),
                Location = new System.Drawing.Point((800 - 200) / 2, 480),
                Font = new System.Drawing.Font("Segoe UI", 10F)
            };
            bBad.Click += (s, e) => OnBookTagTake("SIM_BOOK_BAD");
            panelScanBook.Controls.Add(bBad);

            var bFull = new Button
            {
                Text = "Нет места",
                Size = new System.Drawing.Size(200, 40),
                Location = new System.Drawing.Point((800 - 200) / 2, 480),
                Font = new System.Drawing.Font("Segoe UI", 10F)
            };
            bFull.Click += (s, e) => OnBookTagReturn("SIM_BOOK_FULL");
            panelScanBookReturn.Controls.Add(bFull);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _card?.Dispose(); } catch { }
            try
            {
                if (_bookReturn != null && _bookReturn != _bookTake) _bookReturn.Dispose();
                _bookTake?.Dispose();
            } catch { }
            try { _ardu?.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        // ===== Горячие клавиши (работают только в SIM_MODE) =====
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!SIM_MODE) return base.ProcessCmdKey(ref msg, keyData);

            if (keyData == Keys.D1) { OnCardUid("SIM_CARD"); return true; }
            if (keyData == Keys.D2) { OnBookTagTake("SIM_BOOK_OK"); return true; }
            if (keyData == Keys.D3) { OnBookTagTake("SIM_BOOK_BAD"); return true; }
            if (keyData == Keys.D4) { OnBookTagReturn("SIM_BOOK_FULL"); return true; }
            if (keyData == Keys.F9) { TestIrbisConnection(); return true; }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ===== Навигация экранов =====
        private void Switch(Screen s, Panel panel, int? timeoutSeconds = null)
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
                if (c is Panel panel) panel.Visible = false;

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

        // ===== Считывание карты =====
        private void OnCardUid(string uid)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnCardUid), uid); return; }

            bool ok = SIM_MODE ? true : CheckReader(uid);
            if (!ok)
            {
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
                return;
            }

            if (_screen == Screen.S2_WaitCardTake)
                Switch(Screen.S3_WaitBookTake, panelScanBook);
            else if (_screen == Screen.S4_WaitCardReturn)
                Switch(Screen.S5_WaitBookReturn, panelScanBookReturn);
        }

        private bool CheckReader(string uid) => !string.IsNullOrWhiteSpace(uid);

        // ===== Считывание книги (2 ридера) =====
        private void OnBookTagTake(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagTake), tag); return; }
            if (_screen == Screen.S3_WaitBookTake) HandleTake(tag);
        }

        private void OnBookTagReturn(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTagReturn), tag); return; }
            if (_screen == Screen.S5_WaitBookReturn) HandleReturn(tag);
        }

        // запасной общий обработчик (если нужно)
        private void OnBookTag(string tag)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnBookTag), tag); return; }
            if (_screen == Screen.S3_WaitBookTake) HandleTake(tag);
            else if (_screen == Screen.S5_WaitBookReturn) HandleReturn(tag);
        }

        // --- Выдача (3 -> 6/7/8) ---
        private void HandleTake(string bookTag)
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
                    _ardu?.OpenBin();
                    lblSuccess.Text = "Книга выдана";
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                var records = _svc.FindByInvOrTag(bookTag);
                if (records == null || records.Length == 0)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                var rec = records[0];
                var byTag = _svc.Find910ByTag(rec, bookTag);
                if (byTag == null || byTag.Length == 0)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                var f910 = byTag[0];
                string status = f910.GetSubFieldText('a', 0) ?? string.Empty;
                bool canIssue = string.IsNullOrEmpty(status) || status == STATUS_IN_STOCK;
                if (!canIssue)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag, TIMEOUT_SEC_NO_TAG);
                    return;
                }

                _ardu?.OpenBin();

                _svc.Set910StatusAndWrite(
                    record: rec,
                    newStatus: STATUS_ISSUED,
                    inventory: null,
                    tag: bookTag,
                    place: null,
                    actualize: true);

                lblSuccess.Text = "Книга выдана";
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch
            {
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // --- Возврат (5 -> 6/7/9) ---
        private void HandleReturn(string bookTag)
        {
            try
            {
                if (SIM_MODE)
                {
                    if (bookTag.IndexOf("BAD", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Switch(Screen.S7_BookRejected, panelNoTag);
                        var hop = new Timer { Interval = 2000 };
                        hop.Tick += (s, e) => { hop.Stop(); hop.Dispose(); Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE); };
                        hop.Start();
                        return;
                    }
                    if (bookTag.IndexOf("FULL", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                        return;
                    }
                    _ardu?.OpenBin();
                    lblSuccess.Text = "Книга принята";
                    Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
                    return;
                }

                var records = _svc.FindByInvOrTag(bookTag);
                if (records == null || records.Length == 0)
                {
                    Switch(Screen.S7_BookRejected, panelNoTag);
                    var hop = new Timer { Interval = 2000 };
                    hop.Tick += (s, e) => { hop.Stop(); hop.Dispose(); Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE); };
                    hop.Start();
                    return;
                }

                bool hasSpace = _ardu != null ? _ardu.HasSpace() : true;
                if (!hasSpace)
                {
                    Switch(Screen.S9_NoSpace, panelOverflow, TIMEOUT_SEC_NO_SPACE);
                    return;
                }

                var rec = records[0];
                _svc.Set910StatusAndWrite(rec, STATUS_IN_STOCK, null, bookTag, null, true);

                _ardu?.OpenBin();
                lblSuccess.Text = "Книга принята";
                Switch(Screen.S6_Success, panelSuccess, TIMEOUT_SEC_SUCCESS);
            } catch (Exception ex)
            {
                lblError.Text = "Ошибка возврата: " + ex.Message;
                Switch(Screen.S8_CardFail, panelError, TIMEOUT_SEC_ERROR);
            }
        }

        // ===== Хелперы UI =====
        private void SetUiTexts()
        {
            lblTitleMenu.Text = "Библиотека\nФилиал №1";
            btnTakeBook.Text = "📕 Взять книгу";
            btnReturnBook.Text = "📗 Вернуть книгу";

            lblWaitCardTake.Text = "Приложите карту читателя для выдачи";
            lblWaitCardReturn.Text = "Приложите карту читателя для возврата";
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

        private void AddBackButtonForSim()
        {
            if (!SIM_MODE) return;
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
                if (c is Panel p) p.Controls.Add(back);
        }

        private void btnToMenu_Click(object sender, EventArgs e) => Switch(Screen.S1_Menu, panelMenu);
    }
}
