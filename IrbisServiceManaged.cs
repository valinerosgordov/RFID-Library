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

        public bool IsConnected {
            get {
                try { return _client != null && _client.Connected; } catch { return false; }
            }
        }

        // =========================
        //   БАЗОВЫЕ ОПЕРАЦИИ
        // =========================

        public bool Connect(string conn)
        {
            try
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
                return true;
            } catch
            {
                return false;
            }
        }

        public bool UseDatabase(string db)
        {
            if (!IsConnected) return false;
            try { _client.PopDatabase(); } catch { /* пустой стек допустим */ }

            try
            {
                _client.PushDatabase(db);
                _currentDb = db;

                var max = _client.GetMaxMfn();
                return max > 0;
            } catch
            {
                return false;
            }
        }

        public bool Ping()
        {
            if (!IsConnected) return false;
            try
            {
                _client.NoOp();
                var max = _client.GetMaxMfn();
                return max >= 0;
            } catch
            {
                return false;
            }
        }

        // =========================
        //   ПОИСК КНИГ
        // =========================

        public IrbisRecord FindOneByInvOrTag(string value)
        {
            if (!IsConnected) return null;
            if (string.IsNullOrWhiteSpace(value)) return null;

            try
            {
                var rec = _client.SearchReadOneRecord("\"I={0}\"", value);
                if (rec != null) return rec;

                rec = _client.SearchReadOneRecord("\"IN={0}\"", value);
                return rec;
            } catch
            {
                return null;
            }
        }

        public IrbisRecord[] FindByInvOrTag(string value)
        {
            var one = FindOneByInvOrTag(value);
            return one != null ? new[] { one } : new IrbisRecord[0];
        }

        public RecordField[] Read910(IrbisRecord record)
        {
            if (record == null) return new RecordField[0];
            try { return record.Fields.GetField("910").ToArray(); } catch { return new RecordField[0]; }
        }

        public RecordField[] Find910ByTag(IrbisRecord record, string tag)
        {
            if (record == null || string.IsNullOrEmpty(tag)) return new RecordField[0];
            try
            {
                return record.Fields.GetField("910").GetField('h', tag).ToArray();
            } catch
            {
                return new RecordField[0];
            }
        }

        public RecordField[] FindAvailable910ByTag(IrbisRecord record, string tag)
        {
            if (record == null || string.IsNullOrEmpty(tag)) return new RecordField[0];
            try
            {
                return record.Fields.GetField("910")
                                    .GetField('h', tag)
                                    .GetField('a', "0")
                                    .ToArray();
            } catch
            {
                return new RecordField[0];
            }
        }

        public bool Set910StatusAndWrite(
            IrbisRecord record,
            string newStatus,
            string inventory,
            string tag,
            string place,
            bool actualize)
        {
            if (!IsConnected) return false;
            if (record == null) return false;

            try
            {
                RecordField target = null;

                if (!string.IsNullOrEmpty(tag))
                {
                    var arrH = record.Fields.GetField("910").GetField('h', tag).ToArray();
                    if (arrH.Length > 0) target = arrH[0];
                }

                if (target == null && !string.IsNullOrEmpty(inventory))
                {
                    var arrB = record.Fields.GetField("910").GetField('b', inventory).ToArray();
                    if (arrB.Length > 0) target = arrB[0];
                }

                if (target == null)
                {
                    var all910 = record.Fields.GetField("910").ToArray();
                    if (all910.Length > 0) target = all910[0];
                }

                if (target == null) return false;

                target.ReplaceSubField('a', newStatus, true);
                if (!string.IsNullOrEmpty(place))
                    target.ReplaceSubField('d', place, true);

                _client.WriteRecord(record, false, true);

                if (actualize)
                {
                    var fresh = _client.ReadRecord(record.Mfn);
                    record.Fields.Clear();
                    record.Fields.AddRange(fresh.Fields);
                }
                return true;
            } catch
            {
                return false;
            }
        }

        // =========================
        //   АВТОРИЗАЦИЯ ПО КАРТЕ
        // =========================

        public bool ValidateCard(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;
            // не роняем приложение, если нет связи: просто «не авторизован»
            if (!IsConnected) return false;

            // нормализуем UID
            try { uid = new string(uid.Trim().Where(char.IsLetterOrDigit).ToArray()); } catch { return false; }

            var mode = (ConfigurationManager.AppSettings["AuthMode"] ?? "Whitelist").Trim();

            if (mode.Equals("Whitelist", StringComparison.OrdinalIgnoreCase))
            {
                var csv = ConfigurationManager.AppSettings["AllowedCards"] ?? "";
                var parts = csv
                    .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();

                if (parts.Count == 0) return true; // на стенде — пустой список = пускаем всех

                var set = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
                return set.Contains(uid);
            }
            else // Irbis
            {
                var rdrDb = ConfigurationManager.AppSettings["ReadersDb"] ?? "RDR";
                var pattern = ConfigurationManager.AppSettings["CardSearchExpr"] ?? "\"RFID={0}\"";

                try
                {
                    _client.PushDatabase(rdrDb);
                    try
                    {
                        var rec = _client.SearchReadOneRecord(pattern, uid);
                        return rec != null;
                    }
                    finally
                    {
                        _client.PopDatabase();
                        if (!string.IsNullOrEmpty(_currentDb))
                        {
                            try { _client.PushDatabase(_currentDb); } catch { }
                        }
                    }
                } catch
                {
                    return false;
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
