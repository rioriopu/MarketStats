using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketStats.Game
{
    /// <summary>
    /// 自分のキャラクターとリテイナーの識別子を読み出す。
    ///
    /// 自分については「オーナーの ContentId」と「リテイナーの RetainerId」の両方が分かっているので、
    /// マーケットの出品データを検証するときの答え合わせに使える。
    ///   ・自分の出品を検索したときにオーナー ID が入るのか（サーバーが誰にも送っていないのかの切り分け）
    ///   ・RetainerId からオーナーを導ける規則性があるか
    /// </summary>
    public static unsafe class SelfRetainerProbe
    {
        public sealed class SelfRetainer
        {
            public ulong RetainerId { get; set; }
            public string Name { get; set; } = string.Empty;
            public bool SellingItems { get; set; }
        }

        public sealed class SelfInfo
        {
            public ulong ContentId { get; set; }
            public string CharacterName { get; set; } = string.Empty;
            public List<SelfRetainer> Retainers { get; } = new();
            public string? Error { get; set; }
        }

        public static SelfInfo Read()
        {
            var info = new SelfInfo();

            try
            {
                if (Plugin.PlayerState.IsLoaded)
                {
                    info.ContentId = Plugin.PlayerState.ContentId;
                    info.CharacterName = Plugin.PlayerState.CharacterName;
                }

                var manager = RetainerManager.Instance();
                if (manager == null)
                {
                    info.Error = "リテイナー情報を取得できませんでした。";
                    return info;
                }

                if (!manager->IsReady)
                {
                    info.Error = "リテイナー情報がまだ読み込まれていません（一度リテイナーに話しかけてください）。";
                    return info;
                }

                var count = manager->GetRetainerCount();
                for (uint i = 0; i < count; i++)
                {
                    var retainer = manager->GetRetainerBySortedIndex(i);
                    if (retainer == null || retainer->RetainerId == 0) continue;

                    info.Retainers.Add(new SelfRetainer
                    {
                        RetainerId = retainer->RetainerId,
                        Name = retainer->NameString,
                        SellingItems = retainer->MarketItemCount > 0,
                    });
                }
            }
            catch (Exception e)
            {
                info.Error = e.Message;
            }

            return info;
        }

        /// <summary>
        /// 自分のリテイナー同士の ID の関係を調べる。
        ///
        /// 同じ人がまとめて作ったリテイナーの ID が連番に近いなら、
        /// 「ID が近いリテイナーは同じ持ち主」という推測が成り立つ可能性がある。
        /// 逆にバラバラなら、その手は使えないと分かる。
        /// </summary>
        public static string DescribeRetainerIdSpacing(List<SelfRetainer> retainers)
        {
            if (retainers.Count < 2) return "リテイナーが 1 体のみのため比較できません。";

            var sorted = retainers.OrderBy(r => r.RetainerId).ToList();
            var lines = new List<string>();
            var distances = new List<ulong>();

            for (var i = 1; i < sorted.Count; i++)
            {
                var distance = sorted[i].RetainerId - sorted[i - 1].RetainerId;
                distances.Add(distance);
                lines.Add($"{sorted[i - 1].Name} → {sorted[i].Name}: 差 {distance:N0} (0x{distance:X})");
            }

            var max = distances.Max();
            string verdict;

            if (max <= 16)
                verdict = "→ ID がほぼ連番です。近い ID のリテイナーは同じ持ち主の可能性が高いと考えられます。";
            else if (max <= 100000)
                verdict = "→ ID は近い範囲に固まっています。同じ持ち主の目安として使えるかもしれません。";
            else
                verdict = "→ ID はバラバラです。ID の近さから持ち主を推測することはできません。";

            return string.Join("\n", lines) + "\n" + verdict;
        }

        /// <summary>
        /// RetainerId とオーナーの ContentId の関係を調べる。
        /// 上位ビットが共通していれば、そこからオーナーを導ける可能性がある。
        /// </summary>
        public static string DescribeRelation(ulong contentId, ulong retainerId)
        {
            if (contentId == 0 || retainerId == 0) return "比較できません";

            var xor = contentId ^ retainerId;

            // 上位側から何ビット一致しているか
            var sharedHighBits = 0;
            for (var bit = 63; bit >= 0; bit--)
            {
                if ((xor & (1UL << bit)) != 0) break;
                sharedHighBits++;
            }

            var relation = sharedHighBits >= 16
                ? $"上位 {sharedHighBits} ビットが共通（規則性の可能性あり）"
                : $"上位の共通ビットは {sharedHighBits} のみ（無関係と思われる）";

            return $"{relation} / XOR=0x{xor:X16}";
        }
    }
}
