using System.IO;
using System.Linq;
using System.Text;

namespace MarketStats.Data
{
    /// <summary>
    /// SQLite のファイルを読むだけの最小実装。
    ///
    /// 他のプラグイン（PlayerTrack など）が貯めた記録を取り込むために使う。
    /// 既存のライブラリは既知の脆弱性を抱えていたため、読み取りに必要な部分だけを自前で用意した。
    ///
    /// 対応しているのは「テーブルを頭から読む」ことだけで、書き込み・検索・結合はしない。
    /// 未対応の作りだった場合は、無理に読まずに空を返す。
    /// </summary>
    public static class SqliteReader
    {
        private const string Magic = "SQLite format 3\0";

        /// <summary>ページの種類。</summary>
        private const byte TableInterior = 0x05;
        private const byte TableLeaf = 0x0D;

        /// <summary>読み込んだ 1 行。列名から値を引く。</summary>
        public sealed class Row
        {
            private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

            public void Set(string column, object? value) => _values[column] = value;

            public object? Get(string column) => _values.TryGetValue(column, out var v) ? v : null;

            public string GetText(string column) => Get(column) as string ?? string.Empty;

            public long GetLong(string column) => Get(column) switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                _ => 0,
            };

            /// <summary>符号付きで格納された識別子を、そのままのビットで取り出す。</summary>
            public ulong GetId(string column) => unchecked((ulong)GetLong(column));
        }

        /// <summary>指定したテーブルを読む。読めない場合は空を返す。</summary>
        public static List<Row> ReadTable(string path, string tableName, int maxRows = 100_000)
        {
            var rows = new List<Row>();

            try
            {
                // 開いている最中でも読めるよう、共有して開く。
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var header = new byte[100];
                if (stream.Read(header, 0, 100) != 100) return rows;

                if (Encoding.ASCII.GetString(header, 0, 16) != Magic) return rows;

                var pageSize = ReadUInt16(header, 16);
                var actualPageSize = pageSize == 1 ? 65536 : pageSize;
                if (actualPageSize < 512) return rows;

                // 1 ページ目にテーブルの定義が入っている。
                var schema = ReadPageRows(stream, actualPageSize, 1, new List<string>(), maxRows);

                // sqlite_master: type, name, tbl_name, rootpage, sql
                var target = schema.FirstOrDefault(cells =>
                    cells.Count >= 4 &&
                    cells[0] as string == "table" &&
                    string.Equals(cells[1] as string, tableName, StringComparison.OrdinalIgnoreCase));

                if (target == null) return rows;

                var rootPage = Convert.ToInt64(target[3]);
                var columns = ParseColumns(target[4] as string ?? string.Empty);
                if (columns.Count == 0 || rootPage <= 0) return rows;

                foreach (var cells in ReadPageRows(stream, actualPageSize, rootPage, columns, maxRows))
                {
                    var row = new Row();
                    for (var i = 0; i < columns.Count && i < cells.Count; i++)
                        row.Set(columns[i], cells[i]);
                    rows.Add(row);
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"データベースの読み取りに失敗しました: {e.Message}");
            }

            return rows;
        }

        /// <summary>CREATE TABLE 文から列名を取り出す。</summary>
        private static List<string> ParseColumns(string sql)
        {
            var columns = new List<string>();

            var open = sql.IndexOf('(');
            var close = sql.LastIndexOf(')');
            if (open < 0 || close <= open) return columns;

            var body = sql[(open + 1)..close];
            var depth = 0;
            var current = new StringBuilder();
            var parts = new List<string>();

            foreach (var c in body)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;

                if (c == ',' && depth == 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) parts.Add(current.ToString());

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;

                // 制約の定義（PRIMARY KEY (...) など）は列ではない。
                var upper = trimmed.ToUpperInvariant();
                if (upper.StartsWith("PRIMARY ") || upper.StartsWith("UNIQUE ") ||
                    upper.StartsWith("CHECK") || upper.StartsWith("FOREIGN ") ||
                    upper.StartsWith("CONSTRAINT "))
                    continue;

                var name = trimmed.Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0]
                    .Trim('"', '`', '[', ']', '\'');

                if (name.Length > 0) columns.Add(name);
            }

            return columns;
        }

        /// <summary>ページを辿って、行の値を集める。</summary>
        private static List<List<object?>> ReadPageRows(
            FileStream stream, int pageSize, long pageNumber, List<string> columns, int maxRows)
        {
            var result = new List<List<object?>>();
            var visited = new HashSet<long>();
            var queue = new Queue<long>();
            queue.Enqueue(pageNumber);

            while (queue.Count > 0 && result.Count < maxRows)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current)) continue;   // 壊れたファイルで無限に回らないように

                var page = ReadPage(stream, pageSize, current);
                if (page == null) continue;

                // 1 ページ目だけは先頭 100 バイトがファイル全体のヘッダ。
                var offset = current == 1 ? 100 : 0;
                var type = page[offset];

                if (type != TableLeaf && type != TableInterior) continue;

                var cellCount = ReadUInt16(page, offset + 3);
                var headerSize = type == TableInterior ? 12 : 8;
                var pointerArray = offset + headerSize;

                // 内部ページなら子ページを辿る。
                if (type == TableInterior)
                {
                    var rightMost = ReadUInt32(page, offset + 8);
                    if (rightMost > 0) queue.Enqueue(rightMost);
                }

                for (var i = 0; i < cellCount; i++)
                {
                    var pointerOffset = pointerArray + i * 2;
                    if (pointerOffset + 2 > page.Length) break;

                    var cellOffset = ReadUInt16(page, pointerOffset);
                    if (cellOffset <= 0 || cellOffset >= page.Length) continue;

                    if (type == TableInterior)
                    {
                        var child = ReadUInt32(page, cellOffset);
                        if (child > 0) queue.Enqueue(child);
                        continue;
                    }

                    var cells = ReadLeafCell(stream, pageSize, page, cellOffset, columns.Count);
                    if (cells != null) result.Add(cells);

                    if (result.Count >= maxRows) break;
                }
            }

            return result;
        }

        /// <summary>リーフページのセル 1 つ分を読む。</summary>
        private static List<object?>? ReadLeafCell(
            FileStream stream, int pageSize, byte[] page, int offset, int expectedColumns)
        {
            try
            {
                var cursor = offset;
                var payloadSize = ReadVarint(page, ref cursor);
                var rowId = ReadVarint(page, ref cursor);

                // ページ内に収まる分。あふれる場合は続きが別ページにある。
                var usable = pageSize;
                var maxLocal = usable - 35;

                byte[] payload;

                if (payloadSize <= maxLocal)
                {
                    payload = new byte[payloadSize];
                    Array.Copy(page, cursor, payload, 0, (int)payloadSize);
                }
                else
                {
                    // あふれた分は連結ページから読み継ぐ。
                    var minLocal = (usable - 12) * 32 / 255 - 23;
                    var localSize = minLocal + (payloadSize - minLocal) % (usable - 4);
                    if (localSize > maxLocal) localSize = minLocal;

                    payload = new byte[payloadSize];
                    Array.Copy(page, cursor, payload, 0, (int)localSize);

                    var nextPage = ReadUInt32(page, cursor + (int)localSize);
                    var written = localSize;

                    while (nextPage > 0 && written < payloadSize)
                    {
                        var overflow = ReadPage(stream, pageSize, nextPage);
                        if (overflow == null) break;

                        var chunk = Math.Min(payloadSize - written, usable - 4);
                        Array.Copy(overflow, 4, payload, (int)written, (int)chunk);
                        written += chunk;
                        nextPage = ReadUInt32(overflow, 0);
                    }
                }

                return DecodeRecord(payload, rowId, expectedColumns);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>レコード本体を、列の値に分解する。</summary>
        private static List<object?> DecodeRecord(byte[] payload, long rowId, int expectedColumns)
        {
            var values = new List<object?>();
            var cursor = 0;
            var headerSize = ReadVarint(payload, ref cursor);
            var headerEnd = (int)headerSize;

            var types = new List<long>();
            while (cursor < headerEnd && cursor < payload.Length)
                types.Add(ReadVarint(payload, ref cursor));

            var body = headerEnd;

            foreach (var type in types)
            {
                switch (type)
                {
                    case 0:
                        // 主キーの列は本体に入らず、行 ID がその値になる。
                        values.Add(values.Count == 0 ? rowId : null);
                        break;
                    case 1: values.Add((long)(sbyte)payload[body]); body += 1; break;
                    case 2: values.Add((long)ReadSigned(payload, body, 2)); body += 2; break;
                    case 3: values.Add(ReadSigned(payload, body, 3)); body += 3; break;
                    case 4: values.Add(ReadSigned(payload, body, 4)); body += 4; break;
                    case 5: values.Add(ReadSigned(payload, body, 6)); body += 6; break;
                    case 6: values.Add(ReadSigned(payload, body, 8)); body += 8; break;
                    case 7:
                        values.Add(BitConverter.Int64BitsToDouble(ReadSigned(payload, body, 8)));
                        body += 8;
                        break;
                    case 8: values.Add(0L); break;
                    case 9: values.Add(1L); break;
                    default:
                        if (type < 12) { values.Add(null); break; }

                        var length = (int)((type - (type % 2 == 0 ? 12 : 13)) / 2);
                        if (body + length > payload.Length) { values.Add(null); break; }

                        if (type % 2 == 0)
                        {
                            var blob = new byte[length];
                            Array.Copy(payload, body, blob, 0, length);
                            values.Add(blob);
                        }
                        else
                        {
                            values.Add(Encoding.UTF8.GetString(payload, body, length));
                        }

                        body += length;
                        break;
                }
            }

            while (values.Count < expectedColumns) values.Add(null);
            return values;
        }

        private static byte[]? ReadPage(FileStream stream, int pageSize, long pageNumber)
        {
            if (pageNumber <= 0) return null;

            var offset = (pageNumber - 1) * pageSize;
            if (offset < 0 || offset + pageSize > stream.Length) return null;

            var buffer = new byte[pageSize];
            stream.Seek(offset, SeekOrigin.Begin);

            var read = 0;
            while (read < pageSize)
            {
                var chunk = stream.Read(buffer, read, pageSize - read);
                if (chunk <= 0) break;
                read += chunk;
            }

            return read == pageSize ? buffer : null;
        }

        private static long ReadVarint(byte[] data, ref int cursor)
        {
            long value = 0;

            for (var i = 0; i < 8; i++)
            {
                if (cursor >= data.Length) return value;

                var b = data[cursor++];
                value = (value << 7) | (byte)(b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }

            if (cursor < data.Length) value = (value << 8) | data[cursor++];
            return value;
        }

        private static long ReadSigned(byte[] data, int offset, int length)
        {
            long value = 0;
            for (var i = 0; i < length; i++) value = (value << 8) | data[offset + i];

            // 最上位ビットが立っていれば負の数として扱う。
            var bits = length * 8;
            if (bits < 64 && (value & (1L << (bits - 1))) != 0)
                value -= 1L << bits;

            return value;
        }

        private static int ReadUInt16(byte[] data, int offset) =>
            (data[offset] << 8) | data[offset + 1];

        private static long ReadUInt32(byte[] data, int offset) =>
            ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) |
            ((long)data[offset + 2] << 8) | data[offset + 3];
    }
}
