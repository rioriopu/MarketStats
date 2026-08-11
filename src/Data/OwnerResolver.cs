using System.Linq;

namespace MarketStats.Data
{
    /// <summary>
    /// リテイナーの持ち主を、複数の手法を突き合わせて割り出す。
    ///
    /// ひとつひとつは決め手に欠ける手がかりでも、種類の違うものが同じ人物を指せば確度が上がる。
    /// 逆に、手がかりが 1 種類しかない場合や候補が割れている場合は結論を出さない。
    ///
    /// 使う手法:
    ///   1. 自分のリテイナー           … 確定
    ///   2. 手動設定                   … 確定
    ///   3. 出品データのオーナー識別子 … 確定（サーバーが送ってくれる場合のみ）
    ///   4. 冒険者名刺                 … 確定
    ///   5. 製作者署名の偏り           … 強い（自作品を並べている出品者に有効）
    ///   6. あなたの販売履歴との相関   … 中（買い手が確実に分かっている）
    ///   7. マーケット購入履歴との相関 … 中（買い占め・転売に有効）
    ///   8. チャットでの言及           … 補助
    /// </summary>
    public static class OwnerResolver
    {
        /// <summary>相関を見る既定の時間幅（時間）。</summary>
        private const int DefaultWindowHours = 24;

        public static int Update(
            ListingStore listings,
            MarketHistoryStore purchases,
            SaleStore sales,
            RetainerRegistry registry,
            IdentityStore identities,
            int windowHours = DefaultWindowHours)
        {
            var profiles = registry.Snapshot();
            if (profiles.Count == 0) return 0;

            var allListings = listings.Snapshot()
                .Where(l => l.RetainerId != 0)
                .GroupBy(l => l.RetainerId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var window = (long)Math.Max(1, windowHours) * 3600L;
            var updated = 0;

            foreach (var profile in profiles)
            {
                var evidence = new List<OwnerEvidence>();

                CollectCertainEvidence(profile, identities, evidence);

                if (allListings.TryGetValue(profile.RetainerId, out var retainerListings))
                {
                    CollectArtisanEvidence(retainerListings, identities, evidence);
                    CollectCorrelationEvidence(retainerListings, purchases, sales, window, evidence);
                }

                CollectChatEvidence(profile, evidence);

                var conclusion = OwnerEvidenceEvaluator.Evaluate(evidence);
                if (registry.ApplyConclusion(profile.RetainerId, conclusion, evidence)) updated++;
            }

            return updated;
        }

        /// <summary>確定できる手がかり（自分のリテイナー、手動設定、識別子、名刺）。</summary>
        private static void CollectCertainEvidence(
            RetainerProfile profile, IdentityStore identities, List<OwnerEvidence> evidence)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (profile.IsMine && !string.IsNullOrEmpty(profile.OwnerName))
                evidence.Add(new OwnerEvidence
                {
                    Kind = EvidenceKind.SelfRetainer,
                    OwnerName = profile.OwnerName!,
                    Weight = 1000,
                    Description = "自分のリテイナーです",
                    Unix = now,
                });

            if (profile.ManuallySet && !string.IsNullOrEmpty(profile.OwnerName))
                evidence.Add(new OwnerEvidence
                {
                    Kind = EvidenceKind.Manual,
                    OwnerName = profile.OwnerName!,
                    Weight = 1000,
                    Description = "持ち主を手動で設定しました",
                    Unix = now,
                });

            if (profile.OwnerContentId == 0) return;

            var identity = identities.Resolve(profile.OwnerContentId);

            // 対応表に無くても、ゲームが持っている各種リスト（フレンド / FC / LS / コンテンツで
            // 一緒になった人 など）に載っていれば、その場で名前を引ける。
            if (identity == null || identity.Source == IdentitySource.Inferred)
            {
                if (Game.IdentityCollector.TryLookupByContentId(profile.OwnerContentId, out _, out _))
                    identity = identities.Resolve(profile.OwnerContentId);
            }

            if (identity == null || identity.Source == IdentitySource.Inferred) return;

            evidence.Add(new OwnerEvidence
            {
                Kind = identity.Source == IdentitySource.CharaCard
                    ? EvidenceKind.CharaCard
                    : EvidenceKind.OwnerIdMapping,
                OwnerName = identity.Name,
                ContentId = profile.OwnerContentId,
                Weight = 1000,
                Description = identity.Source == IdentitySource.CharaCard
                    ? "冒険者名刺で確認しました"
                    : "出品データのオーナー識別子が対応表と一致しました",
                Unix = identity.LastSeenUnix,
            });
        }

        /// <summary>
        /// 製作者署名の偏りを見る。
        /// あるリテイナーの出品のほとんどが同じ人の製作品なら、その人が持ち主である可能性が高い
        /// （他人の製作品ばかりを集めて売るケースは少ないため）。
        /// </summary>
        private static void CollectArtisanEvidence(
            List<ListingRecord> listings, IdentityStore identities, List<OwnerEvidence> evidence)
        {
            var signed = listings.Where(l => l.ArtisanContentId != 0).ToList();
            if (signed.Count < 3) return;

            var top = signed
                .GroupBy(l => l.ArtisanContentId)
                .OrderByDescending(g => g.Count())
                .First();

            var ratio = (double)top.Count() / signed.Count;
            if (ratio < 0.8) return;

            var identity = identities.Resolve(top.Key);
            if (identity == null || string.IsNullOrWhiteSpace(identity.Name)) return;

            // 名前が推定でしかない場合は、この手がかりも弱める。
            var weight = identity.Source == IdentitySource.Inferred ? 30 : 70;

            evidence.Add(new OwnerEvidence
            {
                Kind = EvidenceKind.ArtisanConsistency,
                OwnerName = identity.Name,
                ContentId = top.Key,
                Weight = weight,
                Description =
                    $"署名のある出品 {signed.Count} 件のうち {top.Count()} 件が {identity.Name} の製作品です",
                Unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        /// <summary>
        /// 「出品が現れた直前に、同じ商品を買っていた人」を探す。
        /// 買って売るタイプの出品者に有効。
        /// </summary>
        private static void CollectCorrelationEvidence(
            List<ListingRecord> listings,
            MarketHistoryStore purchases,
            SaleStore sales,
            long window,
            List<OwnerEvidence> evidence)
        {
            var ownSales = sales.Snapshot().Where(s => s.HasBuyer).ToList();

            // 名前 → (一致件数, 数量一致, 最短の時間差)
            var market = new Dictionary<string, (int Matches, bool Quantity, long BestDelta)>(
                StringComparer.OrdinalIgnoreCase);
            var own = new Dictionary<string, (int Matches, bool Quantity, long BestDelta)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var listing in listings)
            {
                var listedAt = listing.EffectiveListedUnix;
                if (listedAt <= 0) continue;

                foreach (var purchase in purchases.ForItem(listing.ItemId))
                {
                    if (string.IsNullOrWhiteSpace(purchase.BuyerName)) continue;
                    if (purchase.Hq != listing.Hq) continue;

                    var delta = listedAt - purchase.UnixTime;
                    if (delta < 0 || delta > window) continue;

                    Tally(market, purchase.BuyerName, delta, purchase.Quantity, listing.Quantity);
                }

                foreach (var sale in ownSales)
                {
                    if (sale.ItemId != listing.ItemId || sale.Hq != listing.Hq) continue;

                    var delta = listedAt - sale.UnixTime;
                    if (delta < 0 || delta > window) continue;

                    Tally(own, sale.BuyerName, delta, sale.Quantity, listing.Quantity);
                }
            }

            AddCorrelation(evidence, own, EvidenceKind.OwnSaleCorrelation, "あなたの販売履歴", 30);
            AddCorrelation(evidence, market, EvidenceKind.MarketHistoryCorrelation, "マーケットの購入履歴", 22);
        }

        private static void Tally(
            Dictionary<string, (int Matches, bool Quantity, long BestDelta)> table,
            string name, long delta, long purchasedQuantity, long listedQuantity)
        {
            table.TryGetValue(name, out var entry);

            var quantityMatch = entry.Quantity ||
                                (listedQuantity > 0 && purchasedQuantity > 0 &&
                                 (purchasedQuantity == listedQuantity ||
                                  purchasedQuantity % listedQuantity == 0));

            var bestDelta = entry.Matches == 0 ? delta : Math.Min(entry.BestDelta, delta);
            table[name] = (entry.Matches + 1, quantityMatch, bestDelta);
        }

        private static void AddCorrelation(
            List<OwnerEvidence> evidence,
            Dictionary<string, (int Matches, bool Quantity, long BestDelta)> table,
            EvidenceKind kind,
            string sourceLabel,
            int perMatchWeight)
        {
            if (table.Count == 0) return;

            var best = table.OrderByDescending(kv => kv.Value.Matches).First();

            // 1 件だけの一致は偶然に埋もれるので採らない。
            if (best.Value.Matches < 2) return;

            // 数量が噛み合っていないものは、時間が近いだけの偶然の可能性が高い。
            if (!best.Value.Quantity && best.Value.Matches < 4) return;

            var weight = Math.Min(90, best.Value.Matches * perMatchWeight);
            if (best.Value.Quantity) weight += 20;
            if (best.Value.BestDelta < 3600) weight += 10;

            evidence.Add(new OwnerEvidence
            {
                Kind = kind,
                OwnerName = best.Key,
                Weight = weight,
                Description =
                    $"{sourceLabel}: {best.Key} の購入直後に {best.Value.Matches} 件の出品" +
                    (best.Value.Quantity ? "（数量も符合）" : string.Empty),
                Unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        /// <summary>チャットで本人がリテイナー名に言及していた場合の手がかり。</summary>
        private static void CollectChatEvidence(RetainerProfile profile, List<OwnerEvidence> evidence)
        {
            if (profile.ChatMentions.Count == 0) return;

            var top = profile.ChatMentions
                .GroupBy(m => m.SpeakerName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .First();

            evidence.Add(new OwnerEvidence
            {
                Kind = EvidenceKind.ChatMention,
                OwnerName = top.Key,
                Weight = Math.Min(60, 30 * top.Count()),
                Description = $"チャットで {top.Key} がこのリテイナー名に言及しています（{top.Count()} 回）",
                Unix = top.Max(m => m.Unix),
            });
        }
    }
}
