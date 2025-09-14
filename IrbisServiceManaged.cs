using System;
using System.Configuration;
using System.Linq;
using System.Text;

using ManagedClient;

using MarcRecord = ManagedClient.IrbisRecord;
using RecordField = ManagedClient.RecordField;

namespace LibraryTerminal
{
    public sealed class IrbisServiceManaged : IDisposable
    {
        private ManagedClient64 _client;
        private string _currentDb;

        public string CurrentLogin { get; private set; }
        public int LastReaderMfn { get; private set; }

        // === Подключение ===
        public void Connect()
        {
            var cs = ConfigurationManager.AppSettings["connection-string"]
                     ?? "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;db=IBIS;";
            Connect(cs);
        }

        public void Connect(string connectionString)
        {
            try { _client?.Disconnect(); } catch { }
            _client = new ManagedClient64();

            _client.ParseConnectionString(connectionString);

            try
            {
                _client.Connect();
            } catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"IRBIS: не удалось подключиться. {ExplainConn(connectionString)} Error: {ex.Message}", ex);
            }

            _client.Timeout = 20000; // при необходимости подстрой

            if (!_client.Connected)
                throw new InvalidOperationException("IRBIS: не удалось подключиться.");

            CurrentLogin = _client.Username;
            _currentDb = _client.Database;
        }

        private static string ExplainConn(string cs)
        {
            // только для сообщения об ошибке
            string host = "?", db = "?", user = "?"; int port = 0;
            foreach (var part in cs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                var v = kv[1].Trim();
                if (k == "host") host = v;
                else if (k == "port" && int.TryParse(v, out var p)) port = p;
                else if (k == "user") user = v;
                else if (k == "db" || k == "database") db = v;
            }
            return $"Host={host}, Port={port}, User={user}, Db={db}.";
        }

        public void UseDatabase(string dbName)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет активного подключения.");
            _client.Database = dbName;
            _currentDb = dbName;
        }

        // === Нормализация идентификаторов (RFID/UID/EPC/инв.) ===
        private static string NormalizeId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim()
                 .Replace(" ", "")
                 .Replace("-", "")
                 .Replace(":", "");
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            return s.ToUpperInvariant();
        }

        // === Вспомогательные ===
        private string GetMaskMrg()
        {
            try { return _client?.Settings?.Get<string>("Private", "MaskMrg", "09") ?? "09"; } catch { return "09"; }
        }

        private static void LogIrbis(string msg)
        {
            try
            {
                System.IO.File.AppendAllText("irbis.log",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}",
                    Encoding.UTF8);
            } catch { }
        }

        public string DebugDump910(MarcRecord rec, string rfid)
        {
            if (rec == null) return "(rec=null)";
            var key = NormalizeId(rfid);
            var block = rec.Fields.GetField("910")
                .FirstOrDefault(f => NormalizeId(f.GetFirstSubFieldText('h')) == key);
            if (block == null) return "910 not found";
            var a = block.GetFirstSubFieldText('a') ?? "";
            var b = block.GetFirstSubFieldText('b') ?? "";
            var d = block.GetFirstSubFieldText('d') ?? "";
            var h = block.GetFirstSubFieldText('h') ?? "";
            return $"MFN={rec.Mfn} 910: a={a} b={b} d={d} h={h}";
        }

        private MarcRecord FindOne(string expression)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет подключения.");
            var records = _client.SearchRead(expression);
            return (records != null && records.Length > 0) ? records[0] : null;
        }

        private MarcRecord ReadRecord(int mfn)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет подключения.");
            return _client.ReadRecord(mfn);
        }

        private bool WriteRecordSafe(MarcRecord record)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет подключения.");
            // без удержания блокировки, с актуализацией словаря
            _client.WriteRecord(record, /*needLock*/ false, /*ifUpdate*/ true);
            return true;
        }

        private string FormatRecord(string format, int mfn)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет подключения.");
            return (_client.FormatRecord(format, mfn) ?? string.Empty)
                .Replace("\r", "").Replace("\n", "");
        }

        private T WithDatabase<T>(string db, Func<T> action)
        {
            var saved = _client.Database;
            try { _client.Database = db; return action(); }
            finally { _client.Database = saved; }
        }

        // === API для UI ===

        /// <summary>Проверка карты читателя по UID. True — найден.</summary>
        public bool ValidateCard(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            // нормализация
            uid = NormalizeId(uid);

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            string listRaw = ConfigurationManager.AppSettings["ExprReaderByUidList"];
            if (!string.IsNullOrWhiteSpace(listRaw))
            {
                var patterns = listRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim());
                foreach (var pat in patterns)
                {
                    string expr = string.Format(pat, uid);
                    var rec = FindOne(expr);
                    if (rec != null) { LastReaderMfn = rec.Mfn; return true; }
                }
                return false;
            }

            string fmt = ConfigurationManager.AppSettings["ExprReaderByUid"] ?? "\"RI={0}\"";
            var recSingle = FindOne(string.Format(fmt, uid));
            if (recSingle != null) { LastReaderMfn = recSingle.Mfn; return true; }

            return false;
        }

        /// <summary>Поиск книги по инв./шифру/штрихкоду/метке.</summary>
        public IrbisRecord FindOneByInvOrTag(string value)
        {
            if (_client == null) throw new InvalidOperationException("IRBIS не подключён");
            value = NormalizeId(value);
            if (string.IsNullOrWhiteSpace(value)) return null;

            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
            return WithDatabase(booksDb, () => {
                // 1) По шифру (I=)
                var rec = _client.SearchReadOneRecord("\"I={0}\"", value);
                if (rec != null) return rec;

                // 2) По инв./штрихкоду (IN=)
                rec = _client.SearchReadOneRecord("\"IN={0}\"", value);
                if (rec != null) return rec;

                // 3) По RFID книги (требует индекса RF= на 910^h)
                rec = _client.SearchReadOneRecord("\"RF={0}\"", value);
                if (rec != null) return rec;

                // 4) Фолбэк: перебираем кандидатов и фильтруем 910^h по нормализованному ключу
                var cands = _client.SearchRead("\"IN={0}\"", value);
                if (cands == null || cands.Length == 0)
                    cands = _client.SearchRead("\"I={0}\"", value);

                if (cands != null && cands.Length > 0)
                {
                    foreach (var r in cands)
                    {
                        var hit = r.Fields.GetField("910")
                                   .FirstOrDefault(f => NormalizeId(f.GetFirstSubFieldText('h')) == value);
                        if (hit != null) return r;
                    }
                }
                return null;
            });
        }

        /// <summary>Найти книгу строго по RFID (обычно RF= в IBIS; для возврата — HIN= в RDR используется в другом методе).</summary>
        public MarcRecord FindOneByBookRfid(string rfid)
        {
            rfid = NormalizeId(rfid);
            if (string.IsNullOrWhiteSpace(rfid)) return null;

            // По умолчанию ищем в каталоге книг
            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
            string fmt = ConfigurationManager.AppSettings["ExprBookByRfid"];

            // Если явно задано выражение — уважаем его, но работаем в нужной БД.
            if (!string.IsNullOrWhiteSpace(fmt))
            {
                return WithDatabase(booksDb, () => FindOne(string.Format(fmt, rfid)));
            }

            // Типовая стратегия: пробуем RF= в IBIS
            return WithDatabase(booksDb, () =>
                _client.SearchReadOneRecord("\"RF={0}\"", rfid)
            );
        }

        /// <summary>Сменить 910^a по RFID (910^h), ничего не добавляя.</summary>
        public bool UpdateBook910StatusByRfidStrict(
            MarcRecord record,
            string rfidKey,
            string newStatus,
            string _unused = null)
        {
            if (record == null || string.IsNullOrWhiteSpace(rfidKey)) return false;

            rfidKey = NormalizeId(rfidKey);
            var before = DebugDump910(record, rfidKey);

            var target = record.Fields
                .GetField("910")
                .FirstOrDefault(f => NormalizeId(f.GetFirstSubFieldText('h')) == rfidKey);
            if (target == null) { LogIrbis($"Update910 FAIL: 910^h={rfidKey} not found"); return false; }

            var sfa = target.SubFields.FirstOrDefault(s => s.Code == 'a');
            var sfh = target.SubFields.FirstOrDefault(s => s.Code == 'h');
            if (sfa == null || sfh == null) { LogIrbis("Update910 FAIL: subfields a/h missing"); return false; }

            sfa.Text = newStatus ?? "";
            var ok = WriteRecordSafe(record);

            var after = DebugDump910(record, rfidKey);
            LogIrbis($"Update910 {(ok ? "OK" : "FAIL")}: {before}  ->  {after}");
            return ok;
        }

        /// <summary>Добавить повторение поля 40 в RDR при выдаче.</summary>
        public bool AppendRdr40OnIssue(
            int readerMfn,
            MarcRecord bookRec,
            string rfidHex,
            string maskMrg,
            string login,
            string catalogDbName)
        {
            if (readerMfn <= 0 || bookRec == null || string.IsNullOrWhiteSpace(rfidHex)) return false;

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            var rdr = ReadRecord(readerMfn);
            if (rdr == null) return false;

            rfidHex = NormalizeId(rfidHex);

            var ex910 = bookRec.Fields.GetField("910")
                            .FirstOrDefault(f => NormalizeId(f.GetFirstSubFieldText('h')) == rfidHex);

            string shelfmark = bookRec.FM("903") ?? "";
            string inv = ex910?.GetFirstSubFieldText('b') ?? "";
            string placeK = ex910?.GetFirstSubFieldText('d') ?? "";

            string bookDb = catalogDbName ?? bookRec.Database ?? (ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS");
            string brief = WithDatabase(bookDb, () => FormatRecord("@brief", bookRec.Mfn)) ?? "";

            string date = DateTime.Now.ToString("yyyyMMdd");
            string time = DateTime.Now.ToString("HHmmss");
            string dateDue = DateTime.Now.AddDays(30).ToString("yyyyMMdd");

            var f40 = new RecordField("40")
                .AddSubField('a', shelfmark)
                .AddSubField('b', inv)
                .AddSubField('c', brief)
                .AddSubField('k', placeK)
                .AddSubField('v', maskMrg ?? "")
                .AddSubField('d', date)
                .AddSubField('1', time)
                .AddSubField('e', dateDue)
                .AddSubField('f', "******")
                .AddSubField('g', bookDb)
                .AddSubField('h', rfidHex)
                .AddSubField('i', string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login);

            rdr.Fields.Add(f40);
            var ok = WriteRecordSafe(rdr);
            LogIrbis($"AppendRdr40 MFN={readerMfn} RFID={rfidHex} ok={ok}");
            return ok;
        }

        /// <summary>Закрыть поле 40 при возврате (ищем по HIN в RDR).</summary>
        public bool CompleteRdr40OnReturn(string rfidHex, string maskMrg, string login)
        {
            rfidHex = NormalizeId(rfidHex);
            if (string.IsNullOrWhiteSpace(rfidHex)) return false;

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            string expr = string.Format(ConfigurationManager.AppSettings["ExprReaderByItemRfid"] ?? "\"HIN={0}\"", rfidHex);
            var rdr = FindOne(expr);
            if (rdr == null) return false;

            var f40 = rdr.Fields
                .GetField("40")
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText('h'), rfidHex, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.GetFirstSubFieldText('f'), "******", StringComparison.OrdinalIgnoreCase));
            if (f40 == null) return false;

            // ^C — убрать brief
            f40.RemoveSubField('c');

            // ^R — место возврата (если включено)
            if ((ConfigurationManager.AppSettings["UseSubfieldR_ReturnPlace"] ?? "false")
                .Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                var rVal = maskMrg ?? "";
                var sfr = f40.SubFields.FirstOrDefault(sf => sf.Code == 'r');
                if (sfr == null) f40.AddSubField('r', rVal); else sfr.Text = rVal;
            }

            // ^2 — время возврата
            var nowTime = DateTime.Now.ToString("HHmmss");
            var sf2 = f40.SubFields.FirstOrDefault(sf => sf.Code == '2');
            if (sf2 == null) f40.AddSubField('2', nowTime); else sf2.Text = nowTime;

            // ^F — фактическая дата возврата
            var nowDate = DateTime.Now.ToString("yyyyMMdd");
            var sff = f40.SubFields.FirstOrDefault(sf => sf.Code == 'f');
            if (sff == null) f40.AddSubField('f', nowDate); else sff.Text = nowDate;

            // ^I — ответственное лицо
            var iVal = string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login;
            var sfi = f40.SubFields.FirstOrDefault(sf => sf.Code == 'i');
            if (sfi == null) f40.AddSubField('i', iVal); else sfi.Text = iVal;

            var ok = WriteRecordSafe(rdr);
            LogIrbis($"CompleteRdr40 RFID={rfidHex} ok={ok}");
            return ok;
        }

        /// <summary>Полный сценарий выдачи по RFID, возвращает brief.</summary>
        public string IssueByRfid(string bookRfid)
        {
            bookRfid = NormalizeId(bookRfid);
            if (LastReaderMfn <= 0) throw new InvalidOperationException("Сначала вызови ValidateCard() для читателя.");
            if (string.IsNullOrWhiteSpace(bookRfid)) throw new ArgumentNullException(nameof(bookRfid));

            var book = FindOneByBookRfid(bookRfid);
            if (book == null) throw new InvalidOperationException("Книга по RFID не найдена.");

            if (!UpdateBook910StatusByRfidStrict(book, bookRfid, "1"))
                throw new InvalidOperationException("Не удалось обновить статус книги (910^A=1).");

            var maskMrg = GetMaskMrg();
            var dbName = book.Database ?? (ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS");
            if (!AppendRdr40OnIssue(LastReaderMfn, book, bookRfid, maskMrg, CurrentLogin, dbName))
                throw new InvalidOperationException("Не удалось добавить поле 40 читателю.");

            return WithDatabase(dbName, () => FormatRecord("@brief", book.Mfn));
        }

        /// <summary>Полный сценарий возврата по RFID, возвращает brief.</summary>
        public string ReturnByRfid(string bookRfid)
        {
            bookRfid = NormalizeId(bookRfid);
            if (string.IsNullOrWhiteSpace(bookRfid)) throw new ArgumentNullException(nameof(bookRfid));

            if (!CompleteRdr40OnReturn(bookRfid, GetMaskMrg(), CurrentLogin))
                throw new InvalidOperationException("Не удалось обновить поле 40 в записи читателя.");

            var book = FindOneByBookRfid(bookRfid);
            if (book == null) throw new InvalidOperationException("Книга по RFID не найдена.");

            if (!UpdateBook910StatusByRfidStrict(book, bookRfid, "0"))
                throw new InvalidOperationException("Не удалось обновить статус книги (910^A=0).");

            var dbName = book.Database ?? (ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS");
            return WithDatabase(dbName, () => FormatRecord("@brief", book.Mfn));
        }

        public void Dispose()
        {
            try { _client?.Disconnect(); } catch { }
            _client = null;
        }
    }
}
