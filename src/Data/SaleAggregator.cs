using System.Linq;

namespace MarketStats.Data
{
    /// <summary>まとめ買い1回分（同一購入者・同一アイテムの、時間的に近接した取引の束）。</summary>
    public sealed class PurchaseSession
    {
        public long StartUnix { get; set; }
        public long EndUnix { get; set; }

        /// <summary>この束に含まれる取引（＝出品枠）の数。</summary>
        public int Slots { get; set; }

        /// <summary>1枠あたりの数量。全枠同じ数量のときだけ意味を持つ。</summary>
        public uint UnitQuantity { get; set; }

        /// <summary>全枠が同じ数量だったか（「99個 × 10枠」と表記できるか）。</summary>
        public bool Uniform { get; set; }

        public long TotalQuantity { get; set; }
        public ulong TotalGil { get; set; }

        public List<SaleRecord> Records { get; } = new();

        public DateTime StartLocal => DateTimeOffset.FromUnixTimeSeconds(StartUnix).LocalDateTime;

        /// <summary>「99個 × 10枠」「合計 990個 (10件)」のような表示文字列。</summary>
        public string QuantityText =>
            Uniform && Slots > 1
                ? $"{UnitQuantity:N0}個 × {Slots}枠"
                : Slots > 1
                    ? $"{Slots}枠 (数量混在)"
                    : $"{TotalQuantity:N0}個";
    }

    /// <summary>購入者 × アイテム（HQ/NQ 別）の集計。</summary>
    public sealed class BuyerItemStat
    {
        public uint ItemId { get; set; }
        public bool Hq { get; set; }
        public string ItemName { get; set; } = string.Empty;

        /// <summary>累計購入数。</summary>
        public long TotalQuantity { get; set; }

        /// <summary>取引件数（出品枠の数）。</summary>
        public int TransactionCount { get; set; }

        /// <summary>まとめ買い回数（時間窓でまとめた回数）。</summary>
        public int SessionCount => Sessions.Count;

        public ulong TotalGil { get; set; }
        public long FirstUnix { get; set; }
        public long LastUnix { get; set; }

        public List<PurchaseSession> Sessions { get; } = new();

        public double AvgUnitPrice => TotalQuantity == 0 ? 0 : (double)TotalGil / TotalQuantity;

        public DateTime LastLocal => DateTimeOffset.FromUnixTimeSeconds(LastUnix).LocalDateTime;
    }

    /// <summary>購入者単位の集計。</summary>
    public sealed class BuyerStat
    {
        public string BuyerName { get; set; } = string.Empty;
        public bool IsFavorite { get; set; }
        public bool IsMannequin { get; set; }

        public long TotalQuantity { get; set; }
        public int TransactionCount { get; set; }
        public int SessionCount { get; set; }
        public ulong TotalGil { get; set; }
        public long FirstUnix { get; set; }
        public long LastUnix { get; set; }

        public List<BuyerItemStat> Items { get; } = new();

        public int DistinctItemCount => Items.Count;

        public DateTime LastLocal => DateTimeOffset.FromUnixTimeSeconds(LastUnix).LocalDateTime;
    }

    public static class SaleAggregator
    {
        /// <summary>マネキン販売など購入者名が取れない取引の表示名。</summary>
        public const string UnknownBuyer = "(購入者不明)";

        /// <summary>売却レコードを購入者ごと・アイテムごとに集計する。</summary>
        public static List<BuyerStat> Build(
            IEnumerable<SaleRecord> records,
            Func<string, bool> isFavorite,
            int sessionWindowSeconds)
        {
            var result = new List<BuyerStat>();

            foreach (var buyerGroup in records.GroupBy(
                         r => r.HasBuyer ? r.BuyerName : UnknownBuyer,
                         StringComparer.OrdinalIgnoreCase))
            {
                var buyer = new BuyerStat
                {
                    BuyerName = buyerGroup.Key,
                    IsMannequin = string.Equals(buyerGroup.Key, UnknownBuyer, StringComparison.Ordinal),
                    FirstUnix = long.MaxValue,
                };
                buyer.IsFavorite = !buyer.IsMannequin && isFavorite(buyer.BuyerName);

                foreach (var itemGroup in buyerGroup.GroupBy(r => (r.ItemId, r.Hq)))
                {
                    var stat = new BuyerItemStat
                    {
                        ItemId = itemGroup.Key.ItemId,
                        Hq = itemGroup.Key.Hq,
                        ItemName = Plugin.Items.GetName(itemGroup.Key.ItemId),
                        FirstUnix = long.MaxValue,
                    };

                    foreach (var rec in itemGroup)
                    {
                        stat.TotalQuantity += rec.Quantity;
                        stat.TotalGil += rec.TotalGil;
                        stat.TransactionCount++;
                        if (rec.UnixTime < stat.FirstUnix) stat.FirstUnix = rec.UnixTime;
                        if (rec.UnixTime > stat.LastUnix) stat.LastUnix = rec.UnixTime;
                    }

                    stat.Sessions.AddRange(BuildSessions(itemGroup, sessionWindowSeconds));
                    stat.Sessions.Sort((a, b) => b.StartUnix.CompareTo(a.StartUnix));

                    buyer.Items.Add(stat);
                    buyer.TotalQuantity += stat.TotalQuantity;
                    buyer.TotalGil += stat.TotalGil;
                    buyer.TransactionCount += stat.TransactionCount;
                    buyer.SessionCount += stat.SessionCount;
                    if (stat.FirstUnix < buyer.FirstUnix) buyer.FirstUnix = stat.FirstUnix;
                    if (stat.LastUnix > buyer.LastUnix) buyer.LastUnix = stat.LastUnix;
                }

                buyer.Items.Sort((a, b) => b.TotalQuantity.CompareTo(a.TotalQuantity));
                if (buyer.FirstUnix == long.MaxValue) buyer.FirstUnix = 0;
                result.Add(buyer);
            }

            result.Sort((a, b) => b.LastUnix.CompareTo(a.LastUnix));
            return result;
        }

        /// <summary>
        /// 同一アイテムの取引を時間窓でまとめて「まとめ買い」の単位にする。
        /// 直前の取引から <paramref name="windowSeconds"/> 以内なら同じ束として扱う。
        /// </summary>
        private static List<PurchaseSession> BuildSessions(
            IEnumerable<SaleRecord> itemRecords, int windowSeconds)
        {
            var sessions = new List<PurchaseSession>();
            var ordered = itemRecords.OrderBy(r => r.UnixTime).ToList();
            if (ordered.Count == 0) return sessions;

            PurchaseSession? current = null;
            long lastUnix = 0;

            foreach (var rec in ordered)
            {
                if (current == null || rec.UnixTime - lastUnix > windowSeconds)
                {
                    current = new PurchaseSession { StartUnix = rec.UnixTime };
                    sessions.Add(current);
                }

                current.Records.Add(rec);
                current.EndUnix = rec.UnixTime;
                current.Slots++;
                current.TotalQuantity += rec.Quantity;
                current.TotalGil += rec.TotalGil;
                lastUnix = rec.UnixTime;
            }

            foreach (var s in sessions)
            {
                var first = s.Records[0].Quantity;
                s.Uniform = s.Records.All(r => r.Quantity == first);
                s.UnitQuantity = first;
            }

            return sessions;
        }
    }
}
