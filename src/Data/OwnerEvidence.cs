using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>持ち主を割り出すための手がかりの種類。</summary>
    public enum EvidenceKind
    {
        /// <summary>自分のリテイナー（確定）。</summary>
        SelfRetainer,

        /// <summary>手動で設定した（確定）。</summary>
        Manual,

        /// <summary>冒険者名刺で確認した（確定）。</summary>
        CharaCard,

        /// <summary>出品データのオーナー識別子が対応表と一致した（確定）。</summary>
        OwnerIdMapping,

        /// <summary>出品の製作者署名が特定の人物に偏っている（強い手がかり）。</summary>
        ArtisanConsistency,

        /// <summary>自分の販売履歴の購入者と、出品の出現が一致する。</summary>
        OwnSaleCorrelation,

        /// <summary>マーケットの購入履歴と、出品の出現が一致する。</summary>
        MarketHistoryCorrelation,

        /// <summary>チャットで本人がリテイナー名に言及していた。</summary>
        ChatMention,
    }

    /// <summary>持ち主を割り出すための手がかり 1 件。</summary>
    public sealed class OwnerEvidence
    {
        public EvidenceKind Kind { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public ulong ContentId { get; set; }

        /// <summary>この手がかりの重み。</summary>
        public int Weight { get; set; }

        public string Description { get; set; } = string.Empty;
        public long Unix { get; set; }

        /// <summary>これ 1 つで確定と言えるか。</summary>
        [JsonIgnore]
        public bool IsDecisive => Kind is EvidenceKind.SelfRetainer or EvidenceKind.Manual
            or EvidenceKind.CharaCard or EvidenceKind.OwnerIdMapping;

        [JsonIgnore]
        public string KindLabel => Kind switch
        {
            EvidenceKind.SelfRetainer => "自分のリテイナー",
            EvidenceKind.Manual => "手動設定",
            EvidenceKind.CharaCard => "冒険者名刺",
            EvidenceKind.OwnerIdMapping => "出品者の識別子",
            EvidenceKind.ArtisanConsistency => "製作者署名",
            EvidenceKind.OwnSaleCorrelation => "あなたの販売履歴",
            EvidenceKind.MarketHistoryCorrelation => "マーケットの購入履歴",
            EvidenceKind.ChatMention => "チャットでの言及",
            _ => "その他",
        };
    }

    /// <summary>手がかりを突き合わせた結論。</summary>
    public sealed class OwnerConclusion
    {
        public string? OwnerName { get; set; }

        /// <summary>0〜100 の確度。確定情報がある場合は 100。</summary>
        public int Confidence { get; set; }

        public bool IsCertain { get; set; }

        /// <summary>採用した名前を支持する手がかり。</summary>
        public List<OwnerEvidence> Supporting { get; set; } = new();

        /// <summary>別人を指している手がかりがある場合の次点。</summary>
        public string? RunnerUp { get; set; }

        public int RunnerUpScore { get; set; }

        /// <summary>結論を出さなかった理由（出せた場合は null）。</summary>
        public string? Inconclusive { get; set; }

        public string ConfidenceLabel => IsCertain ? "確定"
            : Confidence >= 80 ? "高"
            : Confidence >= 55 ? "中"
            : "低";
    }

    /// <summary>
    /// 複数の手がかりを突き合わせて、リテイナーの持ち主を判断する。
    ///
    /// 単独では弱い手がかりでも、種類の違う手がかりが同じ人物を指していれば確度は上がる。
    /// 逆に、ひとつの手がかりだけで断定はしない（誤った名前を出すのが一番困るため）。
    /// </summary>
    public static class OwnerEvidenceEvaluator
    {
        /// <summary>結論として採用する最低確度。</summary>
        public const int MinimumConfidence = 55;

        public static OwnerConclusion Evaluate(IReadOnlyList<OwnerEvidence> evidence)
        {
            var conclusion = new OwnerConclusion();

            if (evidence.Count == 0)
            {
                conclusion.Inconclusive = "手がかりがありません";
                return conclusion;
            }

            // 確定できる手がかりがあれば、それを最優先する。
            var decisive = evidence
                .Where(e => e.IsDecisive && !string.IsNullOrWhiteSpace(e.OwnerName))
                .OrderByDescending(e => (int)e.Kind == (int)EvidenceKind.SelfRetainer)
                .ThenByDescending(e => e.Unix)
                .FirstOrDefault();

            if (decisive != null)
            {
                conclusion.OwnerName = decisive.OwnerName;
                conclusion.IsCertain = true;
                conclusion.Confidence = 100;
                conclusion.Supporting = evidence
                    .Where(e => string.Equals(e.OwnerName, decisive.OwnerName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return conclusion;
            }

            // 名前ごとに重みを合計する。
            var byName = evidence
                .Where(e => !string.IsNullOrWhiteSpace(e.OwnerName))
                .GroupBy(e => e.OwnerName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Name = g.Key,
                    Score = g.Sum(e => e.Weight),
                    Kinds = g.Select(e => e.Kind).Distinct().Count(),
                    Items = g.ToList(),
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            if (byName.Count == 0)
            {
                conclusion.Inconclusive = "手がかりに名前がありません";
                return conclusion;
            }

            var best = byName[0];

            // 種類の異なる手がかりが揃っているほど信用できる。
            var bonus = best.Kinds switch
            {
                >= 3 => 25,
                2 => 12,
                _ => 0,
            };

            var score = best.Score + bonus;
            var confidence = Math.Clamp(score / 2, 0, 95);

            if (byName.Count > 1)
            {
                conclusion.RunnerUp = byName[1].Name;
                conclusion.RunnerUpScore = byName[1].Score;

                // 候補が割れているなら断定しない。
                if (best.Score - byName[1].Score < 30)
                {
                    conclusion.Inconclusive =
                        $"候補が割れています（{best.Name} と {byName[1].Name}）";
                    conclusion.Supporting = best.Items;
                    conclusion.Confidence = confidence;
                    return conclusion;
                }

                // 差がある場合でも、次点の分だけ確度を落とす。
                confidence = Math.Max(0, confidence - byName[1].Score / 4);
            }

            // 手がかりが 1 種類だけなら、単独では断定しない。
            if (best.Kinds < 2 && best.Items.Count < 3)
            {
                conclusion.Inconclusive = "手がかりが 1 種類だけです";
                conclusion.Supporting = best.Items;
                conclusion.Confidence = confidence;
                return conclusion;
            }

            if (confidence < MinimumConfidence)
            {
                conclusion.Inconclusive = "確度が足りません";
                conclusion.Supporting = best.Items;
                conclusion.Confidence = confidence;
                return conclusion;
            }

            conclusion.OwnerName = best.Name;
            conclusion.Confidence = confidence;
            conclusion.Supporting = best.Items;
            return conclusion;
        }
    }
}
