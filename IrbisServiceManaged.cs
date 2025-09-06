using System;
using System.Configuration;
using System.Collections.Generic;
using System.Linq;

using ManagedClient; // IrbisRecord, RecordField, ManagedClient64

namespace LibraryTerminal
{
    public class IrbisServiceManaged : IDisposable
    {
        private ManagedClient64 _client;
        private string _currentDb;

        // =========================
        //   БАЗОВЫЕ ОПЕРАЦИИ
        // =========================

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

            // постараемся вернуть предыдущую БД (если была), игнорируя ошибки
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

        // =========================
        //   ПОИСК КНИГ
        // =========================

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

        public IrbisRecord[] FindByInvOrTag(string value)
        {
            var one = FindOneByInvOrTag(value);
            return one != null ? new[] { one } : new IrbisRecord[0];
        }

        public RecordField[] Read910(IrbisRecord record)
        {
            if (record == null) return new RecordField[0];
            return record.Fields.GetField("910").ToArray();
        }

        /// <summary>Повторения 910 с конкретной радиометкой (^h = tag).</summary>
        public RecordField[] Find910ByTag(IrbisRecord record, string tag)
        {
            if (record == null || string.IsNullOrEmpty(tag)) return new RecordField[0];
            return record.Fields
                         .GetField("910")
                         .GetField('h', tag)
                         .ToArray();
        }

        /// <summary>Доступные к выдаче: 910 с ^h = tag и ^a = "0".</summary>
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
                var arrH = record.Fields
                                 .GetField("910")
                                 .GetField('h', tag)
                                 .ToArray();
                if (arrH.Length > 0) target = arrH[0];
            }

            if (target == null && !string.IsNullOrEmpty(inventory))
            {
                // 910 с ^b=inventory
                var arrB = record.Fields
                                 .GetField("910")
                                 .GetField('b', inventory)
                                 .ToArray();
                if (arrB.Length > 0) target = arrB[0];
            }

            if (target == null)
            {
                // fallback: первый 910
                var all910 = record.Fields.GetField("910").ToArray();
                if (all910.Length > 0) target = all910[0];
            }

            if (target == null)
                throw new Exception("В записи нет поля 910 для изменения статуса.");

            // меняем 910^a
            target.ReplaceSubField('a', newStatus, true);

            // при необходимости — место хранения ^d
            if (!string.IsNullOrEmpty(place))
                target.ReplaceSubField('d', place, true);

            // сохраняем запись
            _client.WriteRecord(record, false, true);

            if (actualize)
            {
                // перечитаем свежую версию и обновим поля в переданном объекте
                var fresh = _client.ReadRecord(record.Mfn);
                record.Fields.Clear();
                record.Fields.AddRange(fresh.Fields);
            }
        }

        // =========================
        //   АВТОРИЗАЦИЯ ПО КАРТЕ
        // =========================

        /// <summary>
        /// true, если UID карты разрешён (Whitelist) или найден в БД читателей (Irbis).
        /// </summary>
        public bool ValidateCard(string uid)
        {
            if (_client == null) throw new InvalidOperationException("IRBIS не подключён");
            if (string.IsNullOrWhiteSpace(uid)) return false;

            // нормализуем UID (оставим только буквы/цифры)
            uid = new string(uid.Trim().Where(char.IsLetterOrDigit).ToArray());

            // Режим авторизации: Whitelist или Irbis
            var mode = (ConfigurationManager.AppSettings["AuthMode"] ?? "Whitelist").Trim();

            if (mode.Equals("Whitelist", StringComparison.OrdinalIgnoreCase))
            {
                var csv = ConfigurationManager.AppSettings["AllowedCards"] ?? "";

                // разберём CSV в список
                var parts = csv
                    .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();

                // если список пуст — разрешаем всех (для стенда)
                if (parts.Count == 0) return true;

                // используем HashSet вместо ToHashSet()
                var set = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
                return set.Contains(uid);
            }
            else // Irbis
            {
                var rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
                var pattern = ConfigurationManager.AppSettings["CardSearchExpr"] ?? "\"RFID={0}\"";

                // аккуратно переключимся в БД читателей
                _client.PushDatabase(rdrDb);
                try
                {
                    var rec = _client.SearchReadOneRecord(pattern, uid);
                    return rec != null;
                }
                finally
                {
                    // вернёмся в предыдущую БД
                    _client.PopDatabase();
                    if (!string.IsNullOrEmpty(_currentDb))
                    {
                        try { _client.PushDatabase(_currentDb); } catch { }
                    }
                }
            }
        }

        // =========================
        //   ЖИЗНЕННЫЙ ЦИКЛ
        // =========================

        public void Dispose()
        {
            try
            {
                if (_client != null)
                {
                    try { _client.Disconnect(); } catch { }
                    _client.Dispose();
                }
            } catch { }
            finally
            {
                _client = null;
                _currentDb = null;
            }
        }
    }
}
