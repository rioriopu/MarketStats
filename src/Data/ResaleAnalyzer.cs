using System.Linq;

namespace MarketStats.Data
{
    /// <summary>「この購入者が買った物を再出品しているのでは」という候補。確定ではない。</summary>
    public sealed class ResaleCandidate
    {
        /// <summary>出品リテイナーのオーナー ContentId（ゲームから読めた場合）。</summary>
        public ulong OwnerContentId { get; set; }

        /// <summary>オーナーが取れないとき（Universalis 由来）のリテイナー ID。</summary>
        public ulong RetainerId { get; set; }

        public string RetainerName { get; set; } = string.Empty;

        /// <summary>身元が解決できた場合のキャラクター名。</summary>
        public string? OwnerName { get; set; }

        public uint ItemId { get; set; }
        public bool Hq { get; set; }
        public string ItemName { get; set; } = string.Empty;

        public int Score { get; set; }

        /// <summary>この候補が何回の購入イベントと結びついたか。</summary>
        public int MatchedPurchases { get; set; }

        public long ListedUnix { get; set; }
        public long UnitPrice { get; set; }
        public long TotalQuantity { get; set; }
        public int ListingCount { get; set; }
        public string WorldName { get; set; } = string.Empty;

        public List<string> Reasons { get; } = new();

        public DateTime ListedLocal => DateTimeOffset.FromUnixTimeSeconds(ListedUnix).LocalDateTime;

        /// <summary>スコアから求めた確信度の表示。</summary>
        public string ConfidenceText => Score switch
        {
            >= 140 => "高",
            >= 90 => "中",
            _ => "低",
        };
    }

    /// <summary>
    /// 売却履歴（誰が何をいつ買ったか）と、観測した出品を突き合わせて
    /// 「買った直後に同じ物を出品し始めた人」を推定する。
    ///
    /// ゲームにもマーケット API にも「出品者のキャラクター名」は存在しないため、
    /// これはあくまで状況証拠の積み上げによる推定であり、確定ではない。
    /// </summary>
    public static class ResaleAnalyzer
    {
        /// <summary>候補として表示する最低スコア。</summary>
        public const int MinimumScore = 50;

        /// <summary>この点数を超え、かつ複数回一致した候補は身元推定として記録する。</summary>
        public const int InferenceScore = 140;

        public static List<ResaleCandidate> Analyze(
            IEnumerable<BuyerItemStat> items,
            ListingStore listings,
            IdentityStore identities,
            int windowHours)
        {
            var window = (long)Math.Max(1, windowHours) * 3600L;
            var candidates = new Dictionary<string, ResaleCandidate>();

            foreach (var item in items)
            {
                var itemListings = listings.ForItem(item.ItemId)
                    .Where(l => l.Hq == item.Hq)
                    .ToList();
                if (itemListings.Count == 0) continue;

                foreach (var session in item.Sessions)
                {
                    var purchaseTime = session.EndUnix;
                    var purchaseUnitPrice = session.TotalQuantity == 0
                        ? 0
                        : (long)(session.TotalGil / (ulong)session.TotalQuantity);

                    foreach (var listing in itemListings)
                    {
                        var listedAt = listing.EffectiveListedUnix;
                        var delta = listedAt - purchaseTime;

                        // 購入より前からある出品は対象外。
                        if (delta < 0 || delta > window) continue;

                        var key = listing.OwnerContentId != 0
                            ? $"o:{listing.OwnerContentId}:{item.ItemId}:{(item.Hq ? 1 : 0)}"
                            : $"r:{listing.RetainerId}:{item.ItemId}:{(item.Hq ? 1 : 0)}";

                        if (!candidates.TryGetValue(key, out var candidate))
                        {
                            candidate = new ResaleCandidate
                            {
                                OwnerContentId = listing.OwnerContentId,
                                RetainerId = listing.RetainerId,
                                RetainerName = listing.RetainerName,
                                ItemId = item.ItemId,
                                Hq = item.Hq,
                                ItemName = item.ItemName,
                                ListedUnix = listedAt,
                                UnitPrice = listing.UnitPrice,
                                WorldName = listing.WorldName,
                            };
                            candidates[key] = candidate;
                        }

                        candidate.ListingCount++;
                        candidate.TotalQuantity += listing.Quantity;
                        if (listedAt < candidate.ListedUnix) candidate.ListedUnix = listedAt;

                        // 時間の近さ
                        var timeScore = delta switch
                        {
                            < 3600 => 40,
                            < 6 * 3600 => 30,
                            < 24 * 3600 => 20,
                            _ => 10,
                        };
                        candidate.Score += timeScore;
                        AddReason(candidate, delta < 3600
                            ? "購入直後（1時間以内）に出品"
                            : $"購入から {FormatDelta(delta)} 後に出品");

                        // 数量パターンの一致（99個 × N枠 で買って 99個 で出している等）
                        if (session.Uniform && listing.Quantity == session.UnitQuantity)
                        {
                            candidate.Score += 25;
                            AddReason(candidate, $"1枠あたりの数量が購入時と一致（{listing.Quantity:N0}個）");
                        }

                        // 買値より高く出している
                        if (purchaseUnitPrice > 0 && listing.UnitPrice > purchaseUnitPrice)
                        {
                            candidate.Score += 15;
                            AddReason(candidate,
                                $"買値 {purchaseUnitPrice:N0} より高い {listing.UnitPrice:N0} で出品");
                        }

                        candidate.MatchedPurchases++;
                    }
                }
            }

            foreach (var candidate in candidates.Values)
            {
                // 複数回の購入と結びついた場合は一気に確度が上がる。
                if (candidate.MatchedPurchases >= 2)
                {
                    candidate.Score += 40 * (candidate.MatchedPurchases - 1);
                    AddReason(candidate, $"{candidate.MatchedPurchases} 回の購入と時系列が一致");
                }

                if (candidate.OwnerContentId != 0)
                {
                    var identity = identities.Resolve(candidate.OwnerContentId);
                    if (identity != null && identity.IsConfirmed)
                        candidate.OwnerName = identity.Name;
                }
            }

            return candidates.Values
                .Where(c => c.Score >= MinimumScore)
                .OrderByDescending(c => c.Score)
                .ToList();
        }

        private static void AddReason(ResaleCandidate candidate, string reason)
        {
            if (!candidate.Reasons.Contains(reason)) candidate.Reasons.Add(reason);
        }

        private static string FormatDelta(long seconds) =>
            seconds < 3600 ? $"{seconds / 60}分"
            : seconds < 86400 ? $"{seconds / 3600}時間"
            : $"{seconds / 86400}日";
    }
}
