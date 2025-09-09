using System;
using System.Linq;

namespace LibraryTerminal
{
    public enum TagKind { Book, Card, Unknown }

    public sealed class EpcInfo
    {
        public TagKind Kind { get; set; }
        public int LibraryCode { get; set; }   // 0..65535
        public uint Serial { get; set; }       // 0..268435455
        public string RawHex { get; set; }     // 24 hex chars
    }

    public static class EpcParser
    {
        // Старшие 48 бит (6 байт) фиксированы под вашу схему EPC-96
        private static readonly byte[] Header = { 0x30, 0x4D, 0xB7, 0x5F, 0x19, 0x60 };

        public static EpcInfo Parse(string epcHex)
        {
            if (string.IsNullOrWhiteSpace(epcHex) || epcHex.Length != 24) return null;

            byte[] bytes;
            try
            {
                bytes = Enumerable.Range(0, 12)
                    .Select(i => Convert.ToByte(epcHex.Substring(i * 2, 2), 16))
                    .ToArray();
            } catch { return null; }

            // Проверяем «шапку»
            for (int i = 0; i < 6; i++)
                if (bytes[i] != Header[i]) return null;

            // Младшие 48 бит
            ulong low48 = 0;
            for (int i = 6; i < 12; i++)
                low48 = (low48 << 8) | bytes[i];

            int library = (int)((low48 >> 32) & 0xFFFF);
            int kindNibble = (int)((low48 >> 28) & 0xF);
            uint serial = (uint)(low48 & 0x0FFFFFFF);

            TagKind kind;
            if (kindNibble == 0x0) kind = TagKind.Book;
            else if (kindNibble == 0xF) kind = TagKind.Card;
            else kind = TagKind.Unknown;

            return new EpcInfo
            {
                Kind = kind,
                LibraryCode = library,
                Serial = serial,
                RawHex = epcHex
            };
        }
    }
}
