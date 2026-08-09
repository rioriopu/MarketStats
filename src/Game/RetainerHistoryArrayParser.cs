using System.Globalization;
using System.Linq;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// 売却履歴ウィンドウが使う UI 配列（ItemDetail）から履歴レコードを復元する保険用パーサー。
    ///
    /// パケットフックと違って型情報が無いため、
    ///   数値側: 7 要素ごとに [0]=合計金額 [3]=HQ [4]=売却時刻(UnixTime) [5]=ItemId
    ///   文字列側: 5 要素ごとに 数量 と 購入者名 が含まれる
    /// という並びを「妥当性チェックが通る位相」を探して読み取る。
    /// 誤検出は偽のログを残してしまうため、少しでも怪しい場合は何も返さない方針にしている。
    /// </summary>
    internal static class RetainerHistoryArrayParser
    {
        private const int NumberStride = 7;
        private const int StringStride = 5;

        // FFXIV: 新生エオルゼアのサービス開始日。これより古い売却時刻はあり得ない。
        private static readonly long EarliestUnix =
            new DateTimeOffset(2013, 8, 24, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        private readonly struct NumericEntry
        {
            public NumericEntry(uint itemId, ulong price, bool hq, long unixTime)
            {
                ItemId = itemId;
                Price = price;
                Hq = hq;
                UnixTime = unixTime;
            }

            public uint ItemId { get; }
            public ulong Price { get; }
            public bool Hq { get; }
            public long UnixTime { get; }
        }

        public static List<SaleRecord> Parse(IReadOnlyList<int> numbers, IReadOnlyList<string> strings)
        {
            var numeric = FindLongestNumericRun(numbers);
            if (numeric.Count < 1) return new List<SaleRecord>();

            var quantities = FindQuantities(strings, numeric.Count, out var buyers);
            if (quantities == null) return new List<SaleRecord>();

            var result = new List<SaleRecord>(numeric.Count);
            for (var i = 0; i < numeric.Count; i++)
            {
                var n = numeric[i];
                var qty = quantities[i];
                if (qty == 0) return new List<SaleRecord>();

                result.Add(new SaleRecord
                {
                    ItemId = n.ItemId,
                    Hq = n.Hq,
                    Quantity = qty,
                    TotalGil = n.Price,
                    UnixTime = n.UnixTime,
                    BuyerName = buyers![i],
                    OnMannequin = string.IsNullOrEmpty(buyers[i]),
                });
            }

            return result;
        }

        /// <summary>数値側から「7 要素ごとのレコードとして妥当な最長の並び」を探す。</summary>
        private static List<NumericEntry> FindLongestNumericRun(IReadOnlyList<int> numbers)
        {
            var best = new List<NumericEntry>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (var offset = 0; offset + NumberStride <= numbers.Count; offset++)
            {
                var run = new List<NumericEntry>();
                for (var i = offset; i + NumberStride <= numbers.Count; i += NumberStride)
                {
                    if (!TryRead(numbers, i, now, out var entry)) break;
                    run.Add(entry);
                }

                if (run.Count > best.Count) best = run;
            }

            return best;
        }

        private static bool TryRead(IReadOnlyList<int> numbers, int index, long now, out NumericEntry entry)
        {
            entry = default;

            var price = numbers[index];
            var hq = numbers[index + 3];
            var timestamp = numbers[index + 4];
            var itemId = numbers[index + 5];

            if (price <= 0 || itemId <= 0) return false;
            if (hq is < 0 or > 1) return false;
            if (timestamp < EarliestUnix || timestamp > now + 86400) return false;

            // 実在しない ItemId ならレコードの並びを取り違えている。
            if (!Plugin.Items.IsBuilt) return false;
            if (Plugin.Items.GetName((uint)itemId).StartsWith('#')) return false;

            entry = new NumericEntry((uint)itemId, (ulong)price, hq == 1, timestamp);
            return true;
        }

        /// <summary>
        /// 文字列側から、数値レコードと同じ件数だけ「数量」「購入者名」が並ぶ位相を探す。
        /// </summary>
        private static uint[]? FindQuantities(
            IReadOnlyList<string> strings, int recordCount, out string[]? buyers)
        {
            buyers = null;

            for (var offset = 0; offset + StringStride * recordCount <= strings.Count; offset++)
            {
                var qty = new uint[recordCount];
                var buyerNames = new string[recordCount];
                var ok = true;

                for (var r = 0; r < recordCount && ok; r++)
                {
                    var start = offset + r * StringStride;
                    uint found = 0;
                    var buyer = string.Empty;

                    for (var f = 0; f < StringStride; f++)
                    {
                        var value = strings[start + f];
                        if (string.IsNullOrWhiteSpace(value)) continue;

                        if (found == 0 && TryParseQuantity(value, out var q))
                        {
                            found = q;
                            continue;
                        }

                        if (buyer.Length == 0 && LooksLikeCharacterName(value))
                            buyer = value.Trim();
                    }

                    if (found == 0)
                    {
                        ok = false;
                        break;
                    }

                    qty[r] = found;
                    buyerNames[r] = buyer;
                }

                if (!ok) continue;

                buyers = buyerNames;
                return qty;
            }

            return null;
        }

        private static bool TryParseQuantity(string value, out uint quantity)
        {
            quantity = 0;
            var trimmed = value.Trim().Replace(",", string.Empty);
            if (trimmed.Length == 0 || trimmed.Length > 5) return false;
            if (!trimmed.All(char.IsAsciiDigit)) return false;
            if (!uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)) return false;
            if (q is 0 or > 99999) return false;

            quantity = q;
            return true;
        }

        /// <summary>FFXIV のキャラクター名として妥当な文字列か（ラテン文字＋一部記号のみ）。</summary>
        private static bool LooksLikeCharacterName(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length is < 3 or > 32) return false;
            if (!char.IsAsciiLetterUpper(trimmed[0])) return false;
            if (!trimmed.Contains(' ')) return false;

            return trimmed.All(c =>
                char.IsAsciiLetter(c) || c is ' ' or '\'' or '-');
        }
    }
}
