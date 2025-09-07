using System;
using System.Configuration;
using System.Collections.Generic;
using System.Linq;
using ManagedClient; // IrbisRecord, RecordField, ManagedClient64

namespace LibraryTerminal
{
    /// <summary>
    /// Сервис доступа к ИРБИС на базе ManagedClient64.
    /// Совместим с вызовами из MainForm старой версии.
    /// </summary>
    public sealed class IrbisServiceManaged : IDisposable
    {
        private ManagedClient64 _client;
        private string _currentDb;

        public IrbisServiceManaged() { }

        public bool IsConnected {
            get {
                try { return _client != null && _client.Connected; } catch { return false; }
            }
        }

        // =========================
        //   Подключение
        // =========================

        /// <summary>
        /// Подключение явной строкой подключения
        /// (пример: host=127.0.0.1;port=6666;user=MASTER;password=MASTERKEY;DB=IBIS;).
        /// </summary>
        public bool Connect(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return false;

            try
            {
                if (_client != null)
                {
                    try { _client.Disconnect(); } catch { }
                    try { _client.Dispose(); } catch { }
                    _client = null;
                }

                _client = new ManagedClient64();
                _client.ParseConnectionString(connectionString);
                _client.Connect();
                return _client.Connected;
            } catch
            {
                return false;
            }
        }

        /// <summary>
        /// Удобная перегрузка: берёт строку подключения из app.config (appSettings["ConnectionString"])
        /// и вызывает Connect(string).
        /// </summary>
        public bool Connect()
        {
            string cs = ConfigurationManager.AppSettings["ConnectionString"];
            return Connect(cs);
        }

        /// <summary>
        /// Выбор БД (проверка доступности). Делает Push + проверку + Pop.
        /// </summary>
        public bool UseDatabase(string db)
        {
            if (!IsConnected) return false;
            if (string.IsNullOrWhiteSpace(db)) return false;

            try
            {
                _client.PushDatabase(db);
                try
                {
                    var max = _client.GetMaxMfn();
                    if (max < 0) return false;
                    _currentDb = db;
                    return true;
                }
                finally
                {
                    _client.PopDatabase();
                }
            } catch
            {
                return false;
            }
        }

        // =========================
        //   Авторизация по карте (RDR)
        // =========================

        /// <summary>
        /// Проверка наличия читателя по UID карты (RI={uid}).
        /// </summary>
        public bool ValidateCard(string uid)
        {
            if (!IsConnected) return false;
            if (string.IsNullOrWhiteSpace(uid)) return false;

            uid = NormalizeUid(uid);
            if (string.IsNullOrEmpty(uid)) return false;

            try
            {
                var rec = _client.SearchReadOneRecord("\"RI={0}\"", uid);
                return rec != null;
            } catch
            {
                return false;
            }
        }

        // =========================
        //   Книги / IBIS
        // =========================

        /// <summary>
        /// Поиск книги ТОЛЬКО по RFID-метке (IN={tag}).
        /// </summary>
        public IrbisRecord FindBookByRfidTag(string tag)
        {
            if (!IsConnected) return null;
            if (string.IsNullOrWhiteSpace(tag)) return null;

            try
            {
                return _client.SearchReadOneRecord("\"IN={0}\"", tag);
            } catch
            {
                return null;
            }
        }

        /// <summary>
        /// Строгое обновление статуса экземпляра (поле 910) по RFID-метке (подполе h).
        /// НИЧЕГО НЕ СОЗДАЁМ: если нужных подполей нет — возвращаем false.
        /// Меняем 910^a; 910^d — только если уже существует и передано новое значение.
        /// </summary>
        public bool Set910StatusByTag(IrbisRecord record, string tagValue, string newStatus, string newPlace)
        {
            if (!IsConnected) return false;
            if (record == null) return false;
            if (string.IsNullOrWhiteSpace(tagValue)) return false;

            try
            {
                // найти нужное повторение 910 по ^h
                var target = record.Fields
                                   .GetField("910")
                                   .FirstOrDefault(f =>
                                       string.Equals(f.GetSubFieldText('h', 0) ?? "",
                                                     tagValue,
                                                     StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    return false;

                // 910^a должен существовать
                var hasA = target.SubFields.FirstOrDefault(sf => sf.Code == 'a') != null;
                if (!hasA) return false;
                target.ReplaceSubField('a', newStatus, /*createIfMissing*/ false);

                // 910^d обновляем только если он есть и передано значение
                if (!string.IsNullOrWhiteSpace(newPlace))
                {
                    var hasD = target.SubFields.FirstOrDefault(sf => sf.Code == 'd') != null;
                    if (hasD)
                        target.ReplaceSubField('d', newPlace, /*createIfMissing*/ false);
                }

                _client.WriteRecord(record, false, true); // без блокировки, с актуализацией
                return true;
            } catch
            {
                return false;
            }
        }

        // ===== Совместимость со старым MainForm (шлюзы) ========================

        /// <summary>
        /// Старое имя — теперь ищем только по RFID-метке.
        /// </summary>
        public IrbisRecord FindOneByInvOrTag(string value)
        {
            return FindBookByRfidTag(value);
        }

        /// <summary>
        /// Старый вариант, ожидающий массив (0/1 элемент).
        /// </summary>
        public IrbisRecord[] FindByInvOrTag(string value)
        {
            var one = FindBookByRfidTag(value);
            return one != null ? new[] { one } : new IrbisRecord[0];
        }

        /// <summary>
        /// Старый метод смены статуса 910 и записи.
        /// Теперь строго правим только существующие подполя; inventory/actualize игнорируем.
        /// </summary>
        public bool Set910StatusAndWrite(IrbisRecord record, string newStatus, string inventory, string tag, string place, bool actualize)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            return Set910StatusByTag(record, tag, newStatus, place);
        }

        // =========================
        //   RDR/40 — фиксация выдачи/возврата
        // =========================

        /// <summary>
        /// ДОБАВИТЬ повторение поля 40 у читателя (по RI={cardUid}) “сырым” caret-текстом.
        /// Пример строки: ^A...^B...^C...^K...^V...^D20240525^1...^E20240624^F******^G...^H{rfid}^I...
        /// </summary>
        public bool AddRdr40LoanByCard(string cardUid, string field40Raw)
        {
            if (!IsConnected) return false;
            if (string.IsNullOrWhiteSpace(cardUid)) return false;
            if (string.IsNullOrWhiteSpace(field40Raw)) return false;

            try
            {
                var uid = NormalizeUid(cardUid);
                if (string.IsNullOrEmpty(uid)) return false;

                var reader = _client.SearchReadOneRecord("\"RI={0}\"", uid);
                if (reader == null) return false;

                reader.AddField("40", field40Raw);
                _client.WriteRecord(reader, false, true);
                return true;
            } catch
            {
                return false;
            }
        }

        /// <summary>
        /// ЗАКРЫТЬ выдачу: найти у читателя повторение 40 по ^H&lt;RFID книги&gt;,
        /// удалить его и добавить новое повторение «сырым» caret-текстом.
        /// </summary>
        public bool CloseRdr40LoanByCard(string cardUid, string bookTag, string newField40Raw)
        {
            if (!IsConnected) return false;
            if (string.IsNullOrWhiteSpace(cardUid)) return false;
            if (string.IsNullOrWhiteSpace(bookTag)) return false;
            if (string.IsNullOrWhiteSpace(newField40Raw)) return false;

            try
            {
                var uid = NormalizeUid(cardUid);
                if (string.IsNullOrEmpty(uid)) return false;

                var reader = _client.SearchReadOneRecord("\"RI={0}\"", uid);
                if (reader == null) return false;

                var old40 = reader.Fields
                                  .GetField("40")
                                  .FirstOrDefault(f =>
                                      string.Equals(f.GetSubFieldText('H', 0) ?? "",
                                                    bookTag,
                                                    StringComparison.OrdinalIgnoreCase));

                if (old40 != null)
                    reader.Fields.Remove(old40);

                reader.AddField("40", newField40Raw);
                _client.WriteRecord(reader, false, true);
                return true;
            } catch
            {
                return false;
            }
        }

        // =========================
        //   Служебные
        // =========================

        private static string NormalizeUid(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var trimmed = raw.Trim().ToUpperInvariant();
            var buf = new List<char>(trimmed.Length);
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (ch != ' ' && ch != ':' && ch != '-') buf.Add(ch);
            }
            return new string(buf.ToArray());
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
            } catch { }
            finally
            {
                _client = null;
                _currentDb = null;
            }
        }
    }
}
