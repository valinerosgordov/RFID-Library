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

        // === Низкоуровневые ===
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
            // запись с блокировкой и актуализацией
            _client.WriteRecord(record, true, true);
            return true;
        }

        private string FormatRecord(string format, int mfn)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет подключения.");
            // brief без переводов строк
            return (_client.FormatRecord(format, mfn) ?? string.Empty)
                .Replace("\r", "").Replace("\n", "");
        }

        // === Публичное API, которое зовёт MainForm ===

        /// <summary>Проверка карты читателя по UID. True — найден.</summary>
        public bool ValidateCard(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            // нормализация как в UI
            uid = uid.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            // 1) новый вариант: список выражений через ';'
            string listRaw = ConfigurationManager.AppSettings["ExprReaderByUidList"];
            if (!string.IsNullOrWhiteSpace(listRaw))
            {
                var patterns = listRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim());
                foreach (var pat in patterns)
                {
                    string expr = string.Format(pat, uid);   // pat уже с &quot;...&quot; в конфиге
                    var rec = FindOne(expr);
                    if (rec != null) { LastReaderMfn = rec.Mfn; return true; }
                }
                return false;
            }

            // 2) старый одиночный ключ — на всякий случай
            string fmt = ConfigurationManager.AppSettings["ExprReaderByUid"] ?? "\"RI={0}\"";
            var recSingle = FindOne(string.Format(fmt, uid));
            if (recSingle != null) { LastReaderMfn = recSingle.Mfn; return true; }

            return false;
        }


        /// <summary>Поиск книги по инвентарному/ШК/RFID в каталоге.</summary>
        public MarcRecord FindOneByInvOrTag(string key)
        {
            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
            UseDatabase(booksDb);

            // В каталоге используем IN= (покрывает инвентарный, штрих-код и RFID)
            string exprInvFmt = ConfigurationManager.AppSettings["ExprBookByInv"] ?? "\"IN={0}\"";
            return FindOne(string.Format(exprInvFmt, key));
        }

        /// <summary>
        /// Строгое обновление статуса экземпляра (910^a) по RFID (910^h).
        /// Не создаёт новых повторений/подполей.
        /// </summary>
        public bool UpdateBook910StatusByRfidStrict(
            MarcRecord record,
            string rfidKey,        // 910^h
            string newStatus,      // 910^a
            string _unused = null  // 910^d не трогаем — оставлено для совместимости сигнатуры
        )
        {
            if (record == null || string.IsNullOrWhiteSpace(rfidKey)) return false;

            const string fld = "910";
            const char sa = 'a';
            const char sh = 'h';

            var target = record.Fields
                .GetField(fld)
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText(sh), rfidKey, StringComparison.OrdinalIgnoreCase));

            if (target == null) return false;

            var sfa = target.SubFields.FirstOrDefault(s => s.Code == sa);
            var sfh = target.SubFields.FirstOrDefault(s => s.Code == sh);
            if (sfa == null || sfh == null) return false;

            sfa.Text = newStatus ?? "";
            return WriteRecordSafe(record);
        }

        /// <summary>Добавить повторение поля 40 в записи читателя при выдаче.</summary>
        public bool AppendRdr40OnIssue(
            int readerMfn,
            MarcRecord bookRec,
            string rfidHex,
            string maskMrg,
            string login,
            string catalogDbName
        )
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

            // brief получаем в каталожной БД
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

        /// <summary>
        /// Закрыть «висящее» поле 40 при возврате:
        /// ищем читателя по HIN=RFID (в RDR) и редактируем то 40, где ^H=RFID и ^F="******".
        /// </summary>
        public bool CompleteRdr40OnReturn(string rfidHex, string maskMrg, string login)
        {
            if (string.IsNullOrWhiteSpace(rfidHex)) return false;

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            // В RDR поиск по HIN=rfid-метка (книги на руках)
            string expr = string.Format(ConfigurationManager.AppSettings["ExprReaderByItemRfid"] ?? "\"HIN={0}\"", rfidHex);
            var rdr = FindOne(expr);
            if (rdr == null) return false;

            var f40 = rdr.Fields
                .GetField("40")
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText('h'), rfidHex, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.GetFirstSubFieldText('f'), "******", StringComparison.OrdinalIgnoreCase));
            if (f40 == null) return false;

            // ^C — удалить
            f40.RemoveSubField('c');

            // ^R — опционально (по умолчанию не пишем)
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

            return WriteRecordSafe(rdr);
        }

        // Вспомогательный: безопасная смена БД и выполнение действия
        private T WithDatabase<T>(string db, Func<T> action)
        {
            var saved = _client.Database;
            try { _client.Database = db; return action(); }
            finally { _client.Database = saved; }
        }

        public void Dispose()
        {
            try { _client?.Disconnect(); } catch { }
            _client = null;
        }
    }
}
