using System.Linq;

namespace MarketStats.Data
{
    /// <summary>マーケット全体での購入者 1 人分の集計。</summary>
    public sealed class MarketBuyerStat
    {
        public string BuyerName { get; set; } = string.Empty;

        /// <summary>買った総数。</summary>
        public long TotalQuantity { get; set; }

        /// <summary>取引の件数。</summary>
        public int TransactionCount { get; set; }

        /// <summary>まとめ買い（近い時刻の連続購入）の回数。</summary>
        public int SessionCount { get; set; }

        public long TotalGil { get; set; }
        public long FirstUnix { get; set; }
        public long LastUnix { get; set; }

        /// <summary>買ったアイテムの種類数。</summary>
        public int DistinctItems { get; set; }

        /// <summary>このうち、自分のリテイナーから買った数。</summary>
        public long FromMeQuantity { get; set; }

        /// <summary>ワールド（分かる場合）。</summary>
        public string WorldName { get; set; } = string.Empty;

        /// <summary>アイテム別の内訳。</summary>
        public List<MarketBuyerItemStat> Items { get; } = new();

        /// <summary>この購入者が持ち主だと推定されているリテイナー。</summary>
        public List<string> LinkedRetainers { get; } = new();

        public DateTime LastLocal => DateTimeOffset.FromUnixTimeSeconds(LastUnix).LocalDateTime;

        /// <summary>1 回のまとめ買いあたりの平均個数。買い占めの規模感。</summary>
        public double AveragePerSession => SessionCount == 0 ? 0 : (double)TotalQuantity / SessionCount;
    }

    /// <summary>購入者 × アイテムの集計（マーケット全体）。</summary>
    public sealed class MarketBuyerItemStat
    {
        public uint ItemId { get; set; }
        public bool Hq { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public long TotalQuantity { get; set; }
        public int TransactionCount { get; set; }
        public long TotalGil { get; set; }
        public long LastUnix { get; set; }
        public long FromMeQuantity { get; set; }

        public DateTime LastLocal => DateTimeOffset.FromUnixTimeSeconds(LastUnix).LocalDateTime;
    }

    /// <summary>
    /// マーケット全体の購入者を集計する。
    ///
    /// 出品者（誰が売っているか）は隠されているが、購入者（誰が買ったか）は
    /// マーケットの購入履歴として公開されている。つまり「買い占めている人」は
    /// 推定ではなく確実に名前で分かる。
    /// 自分のリテイナーから買った分だけでなく、他人の店での購入も含めて集計する。
    /// </summary>
    public static class BuyerAnalytics
    {
        public static List<MarketBuyerStat> Build(
            MarketHistoryStore purchases,
            SaleStore sales,
            RetainerRegistry retainers,
            int sessionWindowSeconds,
            long sinceUnix = 0)
        {
            // マーケットの公開履歴と、自分の販売履歴を 1 つにまとめる。
            // 自分の販売分は「自分から買われた」印を付けておく。
            var entries = new List<(MarketPurchase Purchase, bool FromMe)>();

            foreach (var itemId in purchases.ItemIds())
            foreach (var purchase in purchases.ForItem(itemId))
            {
                if (purchase.UnixTime < sinceUnix) continue;
                if (string.IsNullOrWhiteSpace(purchase.BuyerName)) continue;
                entries.Add((purchase, false));
            }

            foreach (var sale in sales.Snapshot())
            {
                if (sale.UnixTime < sinceUnix || !sale.HasBuyer) continue;

                entries.Add((new MarketPurchase
                {
                    ItemId = sale.ItemId,
                    Hq = sale.Hq,
                    BuyerName = sale.BuyerName,
                    Quantity = sale.Quantity,
                    UnitPrice = (long)sale.UnitPrice,
                    UnixTime = sale.UnixTime,
                    WorldName = sale.OwnerWorld,
                }, true));
            }

            if (entries.Count == 0) return new List<MarketBuyerStat>();

            // 同じ取引が両方に入っている場合があるので取り除く
            // （自分の販売履歴の分は購入履歴側にも載る）。
            //
            // 単価は税の扱いで食い違うことがあるため、突き合わせには使わない。
            // 同じ人が同じ秒に同じ物を同じ数だけ買っていれば、同一の取引とみなす。
            var deduped = entries
                .GroupBy(e => (e.Purchase.ItemId, e.Purchase.UnixTime,
                               e.Purchase.BuyerName.ToLowerInvariant(), e.Purchase.Quantity))
                .Select(g => g.OrderByDescending(e => e.FromMe).First())
                .ToList();

            var result = new List<MarketBuyerStat>();

            foreach (var buyerGroup in deduped.GroupBy(e => e.Purchase.BuyerName, StringComparer.OrdinalIgnoreCase))
            {
                var stat = new MarketBuyerStat
                {
                    BuyerName = buyerGroup.Key,
                    FirstUnix = long.MaxValue,
                };

                foreach (var itemGroup in buyerGroup.GroupBy(e => (e.Purchase.ItemId, e.Purchase.Hq)))
                {
                    var item = new MarketBuyerItemStat
                    {
                        ItemId = itemGroup.Key.ItemId,
                        Hq = itemGroup.Key.Hq,
                        ItemName = Plugin.Items.GetName(itemGroup.Key.ItemId),
                    };

                    foreach (var (purchase, fromMe) in itemGroup)
                    {
                        item.TotalQuantity += purchase.Quantity;
                        item.TotalGil += purchase.Quantity * purchase.UnitPrice;
                        item.TransactionCount++;
                        if (purchase.UnixTime > item.LastUnix) item.LastUnix = purchase.UnixTime;
                        if (fromMe) item.FromMeQuantity += purchase.Quantity;

                        if (string.IsNullOrEmpty(stat.WorldName) && !string.IsNullOrEmpty(purchase.WorldName))
                            stat.WorldName = purchase.WorldName;
                    }

                    stat.Items.Add(item);
                    stat.TotalQuantity += item.TotalQuantity;
                    stat.TotalGil += item.TotalGil;
                    stat.TransactionCount += item.TransactionCount;
                    stat.FromMeQuantity += item.FromMeQuantity;
                    if (item.LastUnix > stat.LastUnix) stat.LastUnix = item.LastUnix;
                }

                stat.DistinctItems = stat.Items.Count;
                stat.SessionCount = CountSessions(buyerGroup.Select(e => e.Purchase), sessionWindowSeconds);
                stat.FirstUnix = buyerGroup.Min(e => e.Purchase.UnixTime);
                stat.Items.Sort((a, b) => b.TotalQuantity.CompareTo(a.TotalQuantity));

                // この人物が持ち主だと分かっている／推定されているリテイナー
                foreach (var profile in retainers.Snapshot())
                {
                    var owner = profile.OwnerName ?? profile.GuessedOwnerName;
                    if (string.Equals(owner, stat.BuyerName, StringComparison.OrdinalIgnoreCase))
                        stat.LinkedRetainers.Add(profile.RetainerName);
                }

                result.Add(stat);
            }

            result.Sort((a, b) => b.TotalQuantity.CompareTo(a.TotalQuantity));
            return result;
        }

        /// <summary>近い時刻の購入をまとめて 1 回と数える。</summary>
        private static int CountSessions(IEnumerable<MarketPurchase> purchases, int windowSeconds)
        {
            var ordered = purchases
                .GroupBy(p => p.ItemId)
                .SelectMany(g => CountSessionsForItem(g.OrderBy(p => p.UnixTime).ToList(), windowSeconds))
                .Count();

            return ordered;
        }

        private static IEnumerable<int> CountSessionsForItem(List<MarketPurchase> ordered, int windowSeconds)
        {
            long last = 0;
            var index = 0;

            foreach (var purchase in ordered)
            {
                if (last == 0 || purchase.UnixTime - last > windowSeconds)
                {
                    index++;
                    yield return index;
                }
                last = purchase.UnixTime;
            }
        }
    }
}
