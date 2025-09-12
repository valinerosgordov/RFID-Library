using System;
using System.Configuration;
using System.Linq;

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
            _client.Connect();

            if (!_client.Connected)
                throw new InvalidOperationException("IRBIS: не удалось подключиться.");

            CurrentLogin = _client.Username;
            _currentDb = _client.Database;
        }

        public void UseDatabase(string dbName)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет активного подключения.");
            _client.Database = dbName;
            _currentDb = dbName;
        }

        // === Вспомогательные ===
        private string GetMaskMrg()
        {
            try { return _client?.Settings?.Get<string>("Private", "MaskMrg", "09") ?? "09"; } catch { return "09"; }
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

        /// <summary>Смоук-тест: версия сервера, БД, max MFN, проба @brief.</summary>
        public string TestConnection()
        {
            using (var test = new ManagedClient64())
            {
                var cs = ConfigurationManager.AppSettings["connection-string"]
                         ?? "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;db=IBIS;";
                test.ParseConnectionString(cs);
                test.Connect();

                var ver = test.GetVersion();
                int max = test.GetMaxMfn();
                string db = test.Database;

                string brief = max > 1 ? test.FormatRecord("@brief", 1) : "(пусто)";
                brief = (brief ?? "").Replace("\r", "").Replace("\n", "");
                return $"OK: IRBIS {ver?.Version}, org={ver?.Organization}, db={db}, maxMFN={max}, brief(1)='{brief}'";
            }
        }

        /// <summary>Проверка карты читателя по UID. True — найден.</summary>
        public bool ValidateCard(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            uid = uid.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

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

        /// <summary>Поиск книги по инв./ШК (конфигурируемый префикс).</summary>
        public MarcRecord FindOneByInvOrTag(string key)
        {
            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
            UseDatabase(booksDb);
            string exprInvFmt = ConfigurationManager.AppSettings["ExprBookByInv"] ?? "\"IN={0}\"";
            return FindOne(string.Format(exprInvFmt, key));
        }

        /// <summary>Найти книгу строго по RFID (HIN=).</summary>
        public MarcRecord FindOneByBookRfid(string rfid)
        {
            if (string.IsNullOrWhiteSpace(rfid)) return null;
            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
            UseDatabase(booksDb);
            string exprRfidFmt = ConfigurationManager.AppSettings["ExprBookByRfid"] ?? "\"HIN={0}\"";
            return FindOne(string.Format(exprRfidFmt, rfid));
        }

        /// <summary>Сменить 910^a по RFID (910^h), ничего не добавляя.</summary>
        public bool UpdateBook910StatusByRfidStrict(
            MarcRecord record,
            string rfidKey,
            string newStatus,
            string _unused = null)
        {
            if (record == null || string.IsNullOrWhiteSpace(rfidKey)) return false;

            var target = record.Fields
                .GetField("910")
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText('h'), rfidKey, StringComparison.OrdinalIgnoreCase));
            if (target == null) return false;

            var sfa = target.SubFields.FirstOrDefault(s => s.Code == 'a');
            var sfh = target.SubFields.FirstOrDefault(s => s.Code == 'h');
            if (sfa == null || sfh == null) return false;

            sfa.Text = newStatus ?? "";
            return WriteRecordSafe(record);
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

            var ex910 = bookRec.Fields.GetField("910")
                            .FirstOrDefault(f => f.GetFirstSubFieldText('h') == rfidHex);

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
            return WriteRecordSafe(rdr);
        }

        /// <summary>Закрыть поле 40 при возврате (ищем по HIN в RDR).</summary>
        public bool CompleteRdr40OnReturn(string rfidHex, string maskMrg, string login)
        {
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

            f40.RemoveSubField('c');

            if ((ConfigurationManager.AppSettings["UseSubfieldR_ReturnPlace"] ?? "false")
                .Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                var rVal = maskMrg ?? "";
                var sfr = f40.SubFields.FirstOrDefault(sf => sf.Code == 'r');
                if (sfr == null) f40.AddSubField('r', rVal); else sfr.Text = rVal;
            }

            var nowTime = DateTime.Now.ToString("HHmmss");
            var sf2 = f40.SubFields.FirstOrDefault(sf => sf.Code == '2');
            if (sf2 == null) f40.AddSubField('2', nowTime); else sf2.Text = nowTime;

            var nowDate = DateTime.Now.ToString("yyyyMMdd");
            var sff = f40.SubFields.FirstOrDefault(sf => sf.Code == 'f');
            if (sff == null) f40.AddSubField('f', nowDate); else sff.Text = nowDate;

            var iVal = string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login;
            var sfi = f40.SubFields.FirstOrDefault(sf => sf.Code == 'i');
            if (sfi == null) f40.AddSubField('i', iVal); else sfi.Text = iVal;

            return WriteRecordSafe(rdr);
        }

        /// <summary>Полный сценарий выдачи по RFID, возвращает brief.</summary>
        public string IssueByRfid(string bookRfid)
        {
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
