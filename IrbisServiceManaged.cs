using System;
using System.Configuration;
using System.Linq;

using ManagedClient;

// Алиасы, чтобы код был привычным
using MarcRecord = ManagedClient.IrbisRecord;
using RecordField = ManagedClient.RecordField;

namespace LibraryTerminal
{
    /// <summary>
    /// Обёртка над ManagedClient64 для ИРБИС: подключение/поиск/чтение/запись,
    /// операции выдачи/возврата (поле 910 и поле 40).
    /// Полностью совместима с вызовами из MainForm.
    /// </summary>
    public sealed class IrbisServiceManaged : IDisposable
    {
        private ManagedClient64 _client;
        private string _currentDb;

        public string CurrentLogin { get; private set; }
        public int LastReaderMfn { get; private set; }

        // === Подключение ===

        /// <summary>
        /// Подключение из appSettings["connection-string"] ИЛИ из строки по умолчанию.
        /// </summary>
        public void Connect()
        {
            var cs = ConfigurationManager.AppSettings["connection-string"]
                     ?? "host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;db=IBIS;";
            Connect(cs);
        }

        /// <summary>
        /// Подключение к серверу по переданной connection string.
        /// Совместимо с вызовами _svc.Connect(conn) из MainForm.
        /// </summary>
        public void Connect(string connectionString)
        {
            _client = new ManagedClient64();
            _client.ParseConnectionString(connectionString);
            _client.Connect();

            if (!_client.Connected)
                throw new InvalidOperationException("IRBIS: не удалось подключиться.");

            CurrentLogin = _client.Username;
            _currentDb = _client.Database; // IBIS по умолчанию
        }

        public void UseDatabase(string dbName)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет активного подключения.");

            _client.Database = dbName;
            _currentDb = dbName;
        }

        // === Вспомогательные низкоуровневые операции ===

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

            _client.WriteRecord(record, false, true); // ifUpdate=true
            return true;
        }

        private string FormatRecord(string format, int mfn)
        {
            if (_client == null || !_client.Connected)
                throw new InvalidOperationException("IRBIS: нет подключения.");
            return _client.FormatRecord(format, mfn);
        }

        private static string ParseLoginFromConnString(string cs)
        {
            if (string.IsNullOrWhiteSpace(cs)) return null;
            foreach (var part in cs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("user", StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim();
            }
            return null;
        }

        // === Публичное API для MainForm ===

        /// <summary>
        /// Проверка карты читателя по UID. true — авторизован.
        /// MFN найденного читателя сохраняется в LastReaderMfn.
        /// </summary>
        public bool ValidateCard(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            uid = uid.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            string fmt = ConfigurationManager.AppSettings["ExprReaderByUid"] ?? "\"RI={0}\"";
            string expr = string.Format(fmt, uid);

            var rec = FindOne(expr);
            if (rec != null)
            {
                LastReaderMfn = rec.Mfn;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Найти книгу по инвентарному/штрихкоду или по RFID-метке.
        /// </summary>
        public MarcRecord FindOneByInvOrTag(string key)
        {
            string booksDb = ConfigurationManager.AppSettings["BooksDb"] ?? "IBIS";
            UseDatabase(booksDb);

            // По инвентарному/штрихкоду
            string exprInvFmt = ConfigurationManager.AppSettings["ExprBookByInv"] ?? "\"INV={0}\"";
            var byInv = FindOne(string.Format(exprInvFmt, key));
            if (byInv != null) return byInv;

            // По RFID (HIN или другой префикс из конфига)
            string exprRfidFmt = ConfigurationManager.AppSettings["ExprBookByRfid"] ?? "\"HIN={0}\"";
            var byRfid = FindOne(string.Format(exprRfidFmt, key));
            return byRfid;
        }

        /// <summary>
        /// СТРОГОЕ обновление статуса экземпляра (поле 910) по RFID (подполе ^h).
        /// Не создаёт новых повторений/подполей. Меняет 910^a и, при наличии, 910^d.
        /// </summary>
        public bool UpdateBook910StatusByRfidStrict(
            MarcRecord record,
            string rfidKey,        // 910^h
            string newStatus,      // 910^a
            string newDateOrNull   // 910^d: менять только если уже есть
        )
        {
            if (record == null || string.IsNullOrWhiteSpace(rfidKey)) return false;

            string fld = ConfigurationManager.AppSettings["HoldingsField"] ?? "910";
            char sa = (ConfigurationManager.AppSettings["HoldingsSubStatus"] ?? "a")[0];
            char sh = (ConfigurationManager.AppSettings["HoldingsSubRfid"] ?? "h")[0];
            char sd = 'd';

            var target = record.Fields
                .GetField(fld)
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText(sh), rfidKey, StringComparison.OrdinalIgnoreCase));

            if (target == null) return false;

            var sfa = target.SubFields.FirstOrDefault(s => s.Code == sa);
            var sfh = target.SubFields.FirstOrDefault(s => s.Code == sh);
            if (sfa == null || sfh == null) return false;

            sfa.Text = newStatus ?? "";
            if (!string.IsNullOrWhiteSpace(newDateOrNull))
            {
                var sfd = target.SubFields.FirstOrDefault(s => s.Code == sd);
                if (sfd != null) sfd.Text = newDateOrNull;
            }

            return WriteRecordSafe(record);
        }

        /// <summary>
        /// Добавить повторение поля 40 в записи читателя при ВЫДАЧЕ.
        /// Совместим с вызовом из MainForm.
        /// </summary>
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

            string shelfmark = bookRec.FM("903") ?? "";
            var ex910 = bookRec.Fields.GetField("910")
                            .FirstOrDefault(f => f.GetFirstSubFieldText('h') == rfidHex);

            string inv = ex910?.GetFirstSubFieldText('b') ?? "";
            string placeK = ex910?.GetFirstSubFieldText('d') ?? "";
            string brief = FormatRecord("@brief", bookRec.Mfn) ?? "";

            string date = DateTime.Now.ToString("yyyyMMdd");
            string time = DateTime.Now.ToString("HHmmss");
            string dateDue = DateTime.Now.AddDays(30).ToString("yyyyMMdd");
            string z = Guid.NewGuid().ToString("N");

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
                .AddSubField('g', catalogDbName ?? bookRec.Database ?? "")
                .AddSubField('h', rfidHex)
                .AddSubField('i', string.IsNullOrWhiteSpace(login) ? (CurrentLogin ?? "") : login)
                .AddSubField('z', z);

            // ВАЖНО: у IrbisRecord нет AddField(RecordField)! Добавляем через коллекцию:
            rdr.Fields.Add(f40);

            return WriteRecordSafe(rdr);
        }

        /// <summary>
        /// Закрыть «висящее» повторение поля 40 при ВОЗВРАТЕ:
        /// ищем читателя по HIN=RFID и редактируем то 40, где ^H=RFID и ^F="******".
        /// Совместим с вызовом из MainForm.
        /// </summary>
        public bool CompleteRdr40OnReturn(string rfidHex, string maskMrg, string login)
        {
            if (string.IsNullOrWhiteSpace(rfidHex)) return false;

            string rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
            UseDatabase(rdrDb);

            var rdr = FindOne($"\"HIN={rfidHex}\"");
            if (rdr == null) return false;

            var f40 = rdr.Fields
                .GetField("40")
                .FirstOrDefault(f =>
                    string.Equals(f.GetFirstSubFieldText('h'), rfidHex, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.GetFirstSubFieldText('f'), "******", StringComparison.OrdinalIgnoreCase));

            if (f40 == null) return false;

            // ^C — удалить
            f40.RemoveSubField('c');

            // ^R — место возврата
            var rVal = maskMrg ?? "";
            var sfr = f40.SubFields.FirstOrDefault(sf => sf.Code == 'r');
            if (sfr == null) f40.AddSubField('r', rVal); else sfr.Text = rVal;

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

        public void Dispose()
        {
            try { _client?.Disconnect(); } catch { /* ignore */ }
            _client = null;
        }
    }
}
