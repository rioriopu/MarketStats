using System.Linq;

namespace MarketStats.Data
{
    /// <summary>
    /// リテイナーの持ち主（オーナー）を割り出す推定エンジン。
    ///
    /// マーケットは「誰が売っているか」を公開しないが、「誰が買ったか」は購入履歴として公開している。
    /// この非対称性を突いて、
    ///   「あるリテイナーが商品を出した直前に、同じ商品を買っていた人は誰か」
    /// を突き合わせ、繰り返し一致する人物をオーナーとして推定する。
    ///
    /// 買って売るタイプ（転売・買い占め）の出品者に対して特に有効で、
    /// 逆に自分で作った物だけを売る人には効きにくい（その場合は製作者署名の方が手がかりになる）。
    /// </summary>
    public static class RetainerOwnerGuesser
    {
        /// <summary>推定として採用する最低スコア。</summary>
        public const int MinimumScore = 60;

        private sealed class Candidate
        {
            public int Score;
            public int Matches;
            public readonly List<string> Reasons = new();
            public long LatestMatchUnix;
        }

        /// <summary>推定を更新する。更新できたリテイナー数を返す。</summary>
        public static int Update(
            ListingStore listings,
            MarketHistoryStore history,
            SaleStore sales,
            RetainerRegistry registry,
            int windowHours)
        {
            var window = (long)Math.Max(1, windowHours) * 3600L;
            var all = listings.Snapshot().Where(l => l.RetainerId != 0).ToList();
            if (all.Count == 0) return 0;

            // リテイナー → 候補者 → スコア
            var scores = new Dictionary<ulong, Dictionary<string, Candidate>>();

            // 自分の販売履歴は「購入者が確実に分かっている」ので、材料として質が高い。
            var ownSales = sales.Snapshot()
                .Where(s => s.HasBuyer)
                .GroupBy(s => s.ItemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var itemGroup in all.GroupBy(l => l.ItemId))
            {
                var purchases = history.ForItem(itemGroup.Key);
                ownSales.TryGetValue(itemGroup.Key, out var salesForItem);

                if (purchases.Count == 0 && (salesForItem == null || salesForItem.Count == 0))
                    continue;

                foreach (var listing in itemGroup)
                {
                    var listedAt = listing.EffectiveListedUnix;
                    if (listedAt <= 0) continue;

                    foreach (var purchase in purchases)
                    {
                        if (string.IsNullOrWhiteSpace(purchase.BuyerName)) continue;
                        if (purchase.Hq != listing.Hq) continue;

                        var delta = listedAt - purchase.UnixTime;
                        if (delta < 0 || delta > window) continue;

                        Accumulate(scores, listing, purchase.BuyerName, delta,
                            purchase.Quantity, "マーケットの購入履歴", purchase.UnixTime);
                    }

                    if (salesForItem == null) continue;

                    foreach (var sale in salesForItem)
                    {
                        if (sale.Hq != listing.Hq) continue;

                        var delta = listedAt - sale.UnixTime;
                        if (delta < 0 || delta > window) continue;

                        Accumulate(scores, listing, sale.BuyerName, delta,
                            sale.Quantity, "あなたの販売履歴", sale.UnixTime, bonus: 10);
                    }
                }
            }

            var updated = 0;

            foreach (var (retainerId, candidates) in scores)
            {
                if (candidates.Count == 0) continue;

                var ranked = candidates.OrderByDescending(c => c.Value.Score).ToList();
                var best = ranked[0];
                if (best.Value.Score < MinimumScore) continue;

                var reasons = new List<string>(best.Value.Reasons)
                {
                    $"{best.Value.Matches} 件の出品が、この人物の購入直後に現れています",
                };

                // 2 番手と僅差なら、その旨も残しておく（鵜呑みにしないため）。
                if (ranked.Count > 1)
                {
                    var second = ranked[1];
                    if (best.Value.Score - second.Value.Score < 30)
                        reasons.Add($"他に {second.Key} も候補です（差が小さく確度は低め）");
                }

                registry.SetGuess(retainerId, best.Key, best.Value.Score, reasons);
                updated++;

                // オーナーの ContentId が判明しているリテイナーなら、
                // 識別子と名前の対応としても覚えておく。
                var profile = registry.Resolve(retainerId);
                if (profile is { OwnerContentId: not 0 } && best.Value.Score >= MinimumScore + 60)
                    Plugin.Identities.RecordInference(profile.OwnerContentId, best.Key, best.Value.Score);
            }

            return updated;
        }

        private static void Accumulate(
            Dictionary<ulong, Dictionary<string, Candidate>> scores,
            ListingRecord listing,
            string buyerName,
            long delta,
            long purchasedQuantity,
            string sourceLabel,
            long purchaseUnix,
            int bonus = 0)
        {
            if (!scores.TryGetValue(listing.RetainerId, out var candidates))
            {
                candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
                scores[listing.RetainerId] = candidates;
            }

            if (!candidates.TryGetValue(buyerName, out var candidate))
            {
                candidate = new Candidate();
                candidates[buyerName] = candidate;
            }

            var score = delta switch
            {
                < 3600 => 40,
                < 6 * 3600 => 28,
                < 24 * 3600 => 18,
                _ => 10,
            } + bonus;

            // 数量の符合。買った数と出している数が噛み合うほど確からしい。
            if (purchasedQuantity > 0 && listing.Quantity > 0)
            {
                if (purchasedQuantity == listing.Quantity)
                {
                    score += 30;
                    AddReason(candidate, $"買った数と出品数が一致（{listing.Quantity:N0}個）");
                }
                else if (purchasedQuantity > listing.Quantity && purchasedQuantity % listing.Quantity == 0)
                {
                    score += 15;
                    AddReason(candidate, $"買った数が出品数の倍数（{purchasedQuantity:N0} → {listing.Quantity:N0}個ずつ）");
                }
            }

            candidate.Score += score;
            candidate.Matches++;
            if (purchaseUnix > candidate.LatestMatchUnix) candidate.LatestMatchUnix = purchaseUnix;

            AddReason(candidate, delta < 3600
                ? $"購入から 1 時間以内に出品（{sourceLabel}）"
                : $"購入から {FormatDelta(delta)} 後に出品（{sourceLabel}）");
        }

        private static void AddReason(Candidate candidate, string reason)
        {
            if (candidate.Reasons.Count >= 6) return;
            if (!candidate.Reasons.Contains(reason)) candidate.Reasons.Add(reason);
        }

        private static string FormatDelta(long seconds) =>
            seconds < 3600 ? $"{seconds / 60}分"
            : seconds < 86400 ? $"{seconds / 3600}時間"
            : $"{seconds / 86400}日";
    }
}
