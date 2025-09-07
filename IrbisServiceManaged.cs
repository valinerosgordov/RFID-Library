using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Configuration; // для чтения appSettings

namespace LibraryTerminal
{
    public sealed class IrbisServiceManaged : IDisposable
    {
        // ==== Зависимости и конфигурация ======================================
        private readonly IIrbisClient _client;             // клиент IRBIS
        private readonly ILogger _log;                      // логгер
        private readonly Config _cfg;                       // конфигурация

        // Опционально: синхронизация доступа, если сервис используется из разных потоков
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // Текущая БД (информативно; выбор БД делается внутри Push/Pop)
        private string _currentDb;

        // --- Основной конструктор (DI) ---
        public IrbisServiceManaged(IIrbisClient client, ILogger log, Config cfg)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (log == null) throw new ArgumentNullException("log");
            if (cfg == null) throw new ArgumentNullException("cfg");

            _client = client;
            _log = log;
            _cfg = cfg;
        }

        // --- Параметрless-конструктор (совместимость с существующим кодом) ---
        public IrbisServiceManaged()
            : this(CreateRealClient(), new DefaultLogger(), LoadConfigFromApp())
        { }

        // ==== Конфигурация =====================================================
        public sealed class Config
        {
            public Config()
            {
                ReadersDb = "RDR";      // база читателей
                BooksDb = "IBIS";       // база книг

                ReaderCardSearchExpr = "RI={0}"; // поиск по RFID-UID читателя
                BookByTagSearchExpr = "IN={0}";  // поиск книги по RFID-метке (подполе h)
                BookByInvSearchExpr = "I={0}";   // поиск книги по инвентарному номеру (подполе b)

                BookStatusField = 910;
                StatusCodeSubfield = 'a';
                InventorySubfield = 'b';
                PlaceSubfield = 'd';
                TagSubfield = 'h';

                UseWhitelist = false;
                Whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AllowAllOnEmptyWhitelist = false;

                ConnectionString = null;
            }

            // Названия баз
            public string ReadersDb { get; set; }
            public string BooksDb { get; set; }

            // Шаблоны поисковых выражений
            public string ReaderCardSearchExpr { get; set; }
            public string BookByTagSearchExpr { get; set; }
            public string BookByInvSearchExpr { get; set; }

            // Настройка поля 910 (экземпляры)
            public int BookStatusField { get; set; }
            public char StatusCodeSubfield { get; set; }
            public char InventorySubfield { get; set; }
            public char PlaceSubfield { get; set; }
            public char TagSubfield { get; set; }

            // Поведение валидации карт
            public bool UseWhitelist { get; set; }
            public HashSet<string> Whitelist { get; set; }
            public bool AllowAllOnEmptyWhitelist { get; set; }

            // Подключение
            public string ConnectionString { get; set; }
        }

        // Простой интерфейс логгера
        public interface ILogger
        {
            void Info(string message);
            void Warn(string message);
            void Error(string message, Exception ex);
            void Debug(string message);
        }

        // Минимальный интерфейс клиента IRBIS
        public interface IIrbisClient : IDisposable
        {
            void Connect(string connectionString);
            void Disconnect();

            void PushDatabase(string dbName);
            void PopDatabase();

            void NoOp(); // проверка соединения

            int[] Search(string irbisQuery);
            IrbisRecord ReadRecord(int mfn);
            void WriteRecord(IrbisRecord record, bool lockRecord, bool actualize);
        }

        // ===== Реализации по умолчанию (можешь заменить на свои) ==============
        private static IIrbisClient CreateRealClient()
        {
            // TODO: верни здесь твой реальный клиент IRBIS (например, Irbis64Client ...)
            // Пока стоит заглушка — сборка пройдёт, но на рантайме бросит исключение при вызове.
            return new DefaultIrbisClient();
        }

        private sealed class DefaultLogger : ILogger
        {
            public void Info(string message) { System.Diagnostics.Debug.WriteLine("[INFO] " + message); }
            public void Warn(string message) { System.Diagnostics.Debug.WriteLine("[WARN] " + message); }
            public void Debug(string message) { System.Diagnostics.Debug.WriteLine("[DBG ] " + message); }
            public void Error(string message, Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ERR ] " + message + ": " + (ex == null ? "" : ex.Message));
            }
        }

        private sealed class DefaultIrbisClient : IIrbisClient
        {
            public void Connect(string connectionString) { throw new NotImplementedException("Подставь реальный IIrbisClient"); }
            public void Disconnect() { }
            public void PushDatabase(string dbName) { }
            public void PopDatabase() { }
            public void NoOp() { }
            public int[] Search(string irbisQuery) { throw new NotImplementedException("Подставь реальный IIrbisClient"); }
            public IrbisRecord ReadRecord(int mfn) { throw new NotImplementedException("Подставь реальный IIrbisClient"); }
            public void WriteRecord(IrbisRecord record, bool lockRecord, bool actualize) { throw new NotImplementedException("Подставь реальный IIrbisClient"); }
            public void Dispose() { }
        }

        private static Config LoadConfigFromApp()
        {
            var cfg = new Config();
            try
            {
                var a = ConfigurationManager.AppSettings;
                if (a["ReadersDb"] != null) cfg.ReadersDb = a["ReadersDb"];
                if (a["BooksDb"] != null) cfg.BooksDb = a["BooksDb"];
                if (a["ReaderCardSearchExpr"] != null) cfg.ReaderCardSearchExpr = a["ReaderCardSearchExpr"];
                if (a["BookByTagSearchExpr"] != null) cfg.BookByTagSearchExpr = a["BookByTagSearchExpr"];
                if (a["BookByInvSearchExpr"] != null) cfg.BookByInvSearchExpr = a["BookByInvSearchExpr"];
                if (a["UseWhitelist"] != null) cfg.UseWhitelist = "true".Equals(a["UseWhitelist"], StringComparison.OrdinalIgnoreCase);
                if (a["AllowAllOnEmptyWhitelist"] != null) cfg.AllowAllOnEmptyWhitelist = "true".Equals(a["AllowAllOnEmptyWhitelist"], StringComparison.OrdinalIgnoreCase);
                if (a["ConnectionString"] != null) cfg.ConnectionString = a["ConnectionString"];
            } catch { }
            return cfg;
        }

        // Запись IRBIS (упрощённо)
        public sealed class IrbisRecord
        {
            public int Mfn { get; set; }
            public List<Field> Fields { get; private set; }

            public IrbisRecord() { Fields = new List<Field>(); }

            public Field FM(int tag) { return Fields.FirstOrDefault(f => f.Tag == tag); }
            public IEnumerable<Field> FMs(int tag) { return Fields.Where(f => f.Tag == tag); }
        }

        public sealed class Field
        {
            public int Tag { get; set; }
            public List<SubField> SubFields { get; private set; }

            public Field() { SubFields = new List<SubField>(); }

            public string Get(char code)
            {
                var sf = SubFields.FirstOrDefault(s => s.Code == code);
                return sf != null ? sf.Value : null;
            }
            public SubField Ensure(char code)
            {
                var sf = SubFields.FirstOrDefault(x => x.Code == code);
                if (sf == null)
                {
                    sf = new SubField { Code = code, Value = string.Empty };
                    SubFields.Add(sf);
                }
                return sf;
            }
        }

        public sealed class SubField { public char Code; public string Value; }

        // ==== Подключение и проверка ===========================================
        public void Connect()
        {
            if (string.IsNullOrWhiteSpace(_cfg.ConnectionString))
                throw new InvalidOperationException("ConnectionString не задан");

            _log.Info("Подключение к IRBIS...");
            _client.Connect(_cfg.ConnectionString);
            _client.NoOp();
            _currentDb = null;
            _log.Info("Подключено.");
        }

        // Совместимость: Connect(string) — как в старом коде
        public void Connect(string connectionString)
        {
            _cfg.ConnectionString = connectionString;
            Connect();
        }

        // Совместимость: UseDatabase — в новой версии лишний, оставляем как no-op
        public void UseDatabase(string db)
        {
            _currentDb = db; // только информативно
        }

        public void Disconnect()
        {
            _log.Info("Отключение от IRBIS...");
            _client.Disconnect();
            _log.Info("Отключено.");
        }

        // ==== Поиск книг =======================================================
        public IrbisRecord FindBookByRfidTag(string tag)
        {
            return FindOneInDb(_cfg.BooksDb, string.Format(_cfg.BookByTagSearchExpr, Escape(tag)));
        }

        public IrbisRecord FindBookByInventory(string inventory)
        {
            return FindOneInDb(_cfg.BooksDb, string.Format(_cfg.BookByInvSearchExpr, Escape(inventory)));
        }

        // ==== Обновление статуса экземпляра (поле 910) =========================
        public bool Set910StatusByTag(IrbisRecord record, string tagValue, string newStatus, string newPlace)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (string.IsNullOrWhiteSpace(tagValue)) throw new ArgumentException("Пустой tagValue", "tagValue");

            var target910 = record.FMs(_cfg.BookStatusField)
                .FirstOrDefault(f => string.Equals(f.Get(_cfg.TagSubfield), tagValue, StringComparison.OrdinalIgnoreCase));

            if (target910 == null)
            {
                _log.Warn(string.Format("910 с h={0} не найден (MFN={1}).", tagValue, record.Mfn));
                return false;
            }

            target910.Ensure(_cfg.StatusCodeSubfield).Value = newStatus;
            if (!string.IsNullOrWhiteSpace(newPlace))
                target910.Ensure(_cfg.PlaceSubfield).Value = newPlace;

            return WriteRecordSafe(record);
        }

        // ==== Валидация карты ==================================================
        public bool ValidateCard(string rawUid)
        {
            var uid = NormalizeUid(rawUid);
            if (string.IsNullOrEmpty(uid))
            {
                _log.Warn("UID карты пуст после нормализации.");
                return false;
            }

            if (_cfg.UseWhitelist)
            {
                if (_cfg.Whitelist.Count == 0)
                {
                    _log.Warn("Whitelist пуст.");
                    return _cfg.AllowAllOnEmptyWhitelist;
                }
                return _cfg.Whitelist.Contains(uid);
            }

            var query = string.Format(_cfg.ReaderCardSearchExpr, Escape(uid));
            var rec = FindOneInDb(_cfg.ReadersDb, query);
            return rec != null;
        }

        // ==== ОБРАТНАЯ СОВМЕСТИМОСТЬ СО СТАРЫМ API ============================
        // Старый метод: поиск и по инвентарному, и по RFID-метке. Возвращаем массив 0/1.
        public IrbisRecord[] FindByInvOrTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new IrbisRecord[0];
            var byTag = FindBookByRfidTag(value);
            if (byTag != null) return new[] { byTag };
            var byInv = FindBookByInventory(value);
            if (byInv != null) return new[] { byInv };
            return new IrbisRecord[0];
        }

        // Старый метод: найти все 910 по tag (h)
        public Field[] Find910ByTag(IrbisRecord record, string tag)
        {
            if (record == null || string.IsNullOrWhiteSpace(tag)) return new Field[0];
            return record.FMs(_cfg.BookStatusField)
                         .Where(f => string.Equals(f.Get(_cfg.TagSubfield), tag, StringComparison.OrdinalIgnoreCase))
                         .ToArray();
        }

        // Старый метод: изменить статус и записать (совместимость со старым MainForm)
        public bool Set910StatusAndWrite(IrbisRecord record, string newStatus, string inventory, string tag, string place, bool actualize)
        {
            if (!string.IsNullOrWhiteSpace(tag))
                return Set910StatusByTag(record, tag, newStatus, string.IsNullOrWhiteSpace(place) ? null : place);

            _log.Warn("Set910StatusAndWrite: не задан tag — обновление пропущено");
            return false;
        }

        // ==== Внутренние методы ===============================================
        private IrbisRecord FindOneInDb(string db, string query)
        {
            _mutex.Wait();
            try
            {
                _client.PushDatabase(db);
                try
                {
                    _log.Debug(string.Format("Поиск в БД '{0}': {1}", db, query));
                    var mfns = _client.Search(query);
                    if (mfns == null || mfns.Length == 0) return null;
                    if (mfns.Length > 1)
                        _log.Warn(string.Format("Поиск вернул {0} записей; берём первую.", mfns.Length));

                    return _client.ReadRecord(mfns[0]);
                }
                finally
                {
                    _client.PopDatabase();
                }
            } catch (Exception ex)
            {
                _log.Error(string.Format("FindOneInDb неудачно (db={0}).", db), ex);
                return null;
            }
            finally
            {
                _mutex.Release();
            }
        }

        private bool WriteRecordSafe(IrbisRecord record)
        {
            _mutex.Wait();
            try
            {
                _client.WriteRecord(record, true, true); // блокировка и актуализация
                return true;
            } catch (Exception ex)
            {
                _log.Error(string.Format("WriteRecord неудачно (MFN={0}).", record != null ? record.Mfn.ToString() : "null"), ex);
                return false;
            }
            finally
            {
                _mutex.Release();
            }
        }

        private static string NormalizeUid(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var trimmed = raw.Trim().ToUpperInvariant();
            var chars = new List<char>(trimmed.Length);
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (ch != ' ' && ch != ':' && ch != '-') chars.Add(ch);
            }
            return new string(chars.ToArray());
        }

        private static string Escape(string value)
        {
            return value == null ? string.Empty : value.Replace("\"", "\\\"");
        }

        // ==== Освобождение ресурсов ============================================
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_client != null) _client.Dispose(); } catch { }
            _mutex.Dispose();
        }
    }
}
