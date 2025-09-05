using System;
using System.Linq;

using ManagedClient;

namespace LibraryTerminal
{
    public class IrbisServiceManaged : IDisposable
    {
        private ManagedClient64 _client;
        private string _currentDb;

        public void Connect(string conn)
        {
            _client = new ManagedClient64 { Timeout = 20000 };
            _client.ParseConnectionString(conn);
            _client.Connect();

            // если в строке был DB=..., выберем её
            var parts = conn.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.StartsWith("DB=", StringComparison.OrdinalIgnoreCase))
                {
                    UseDatabase(p.Substring(3));
                    break;
                }
            }
        }

        public void UseDatabase(string db)
        {
            if (_client == null) throw new InvalidOperationException("IRBIS не подключён");
            try { _client.PopDatabase(); } catch { }
            _client.PushDatabase(db);
            _currentDb = db;

            var max = _client.GetMaxMfn();
            if (max <= 0) throw new Exception("База '" + db + "' недоступна или пуста");
        }

        public void Ping()
        {
            if (_client == null) throw new InvalidOperationException("IRBIS не подключён");
            _client.NoOp();
            var max = _client.GetMaxMfn();
            if (max < 0) throw new Exception("IRBIS вернул некорректный MaxMfn");
        }

        /// <summary>
        /// Найти ОДНУ запись по шифру (I=...) или по инв./метке (IN=...).
        /// </summary>
        public IrbisRecord FindOneByInvOrTag(string value)
        {
            if (_client == null) throw new InvalidOperationException("IRBIS не подключён");
            if (string.IsNullOrWhiteSpace(value)) return null;

            // сначала по шифру I=
            var rec = _client.SearchReadOneRecord("\"I={0}\"", value);
            if (rec != null) return rec;

            // затем по IN=
            rec = _client.SearchReadOneRecord("\"IN={0}\"", value);
            return rec; // может быть null — это нормально
        }

        /// <summary>
        /// Старый совместимый метод: вернёт массив (0 или 1 запись).
        /// </summary>
        public IrbisRecord[] FindByInvOrTag(string value)
        {
            var one = FindOneByInvOrTag(value);
            return one != null ? new[] { one } : new IrbisRecord[0];
        }

        /// <summary>
        /// Все повторения поля 910.
        /// </summary>
        public RecordField[] Read910(IrbisRecord record)
        {
            if (record == null) return new RecordField[0];
            return record.Fields.GetField("910").ToArray();
        }

        /// <summary>
        /// Повторения 910 с конкретной радиометкой (^h = tag).
        /// </summary>
        public RecordField[] Find910ByTag(IrbisRecord record, string tag)
        {
            if (record == null || string.IsNullOrEmpty(tag)) return new RecordField[0];
            return record.Fields
                         .GetField("910")
                         .GetField('h', tag)
                         .ToArray();
        }

        /// <summary>
        /// Доступные к выдаче экземпляры: 910 с ^h = tag и ^a = "0".
        /// </summary>
        public RecordField[] FindAvailable910ByTag(IrbisRecord record, string tag)
        {
            if (record == null || string.IsNullOrEmpty(tag)) return new RecordField[0];
            return record.Fields
                         .GetField("910")
                         .GetField('h', tag)
                         .GetField('a', "0")
                         .ToArray();
        }

        /// <summary>
        /// Установить статус 910^a и записать запись обратно.
        /// Целевое 910 выбираем по ^h=tag или по ^b=inventory (что задано).
        /// </summary>
        public void Set910StatusAndWrite(
            IrbisRecord record,
            string newStatus,
            string inventory,
            string tag,
            string place,
            bool actualize)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (_client == null) throw new InvalidOperationException("IRBIS не подключён");

            RecordField target = null;

            if (!string.IsNullOrEmpty(tag))
            {
                // 910 с ^h=tag (первое совпадение)
                var arr = record.Fields
                                .GetField("910")
                                .GetField('h', tag)
                                .ToArray();
                if (arr.Length > 0) target = arr[0];
            }

            if (target == null && !string.IsNullOrEmpty(inventory))
            {
                // 910 с ^b=inventory
                var arr = record.Fields
                                .GetField("910")
                                .GetField('b', inventory)
                                .ToArray();
                if (arr.Length > 0) target = arr[0];
            }

            if (target == null)
            {
                // fallback: возьмём первый 910 (на крайний случай)
                var all910 = record.Fields.GetField("910").ToArray();
                if (all910.Length > 0) target = all910[0];
            }

            if (target == null)
                throw new Exception("В записи нет поля 910 для изменения статуса.");

            // меняем 910^a
            target.ReplaceSubField('a', newStatus, true);

            // по необходимости — место хранения ^d
            if (!string.IsNullOrEmpty(place))
                target.ReplaceSubField('d', place, true);

            // сохраняем запись
            _client.WriteRecord(record, false, true);

            if (actualize)
            {
                // перечитаем и обновим коллекцию полей (без присвоения)
                var fresh = _client.ReadRecord(record.Mfn);
                record.Fields.Clear();
                record.Fields.AddRange(fresh.Fields);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_client != null)
                {
                    try { _client.Disconnect(); } catch { }
                    _client.Dispose();
                }
            }
            catch { }
            finally
            {
                _client = null;
                _currentDb = null;
            }
        }
    }
}
