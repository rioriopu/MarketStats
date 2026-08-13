using System.Linq;

namespace MarketStats.Data
{
    /// <summary>他人を確定できる経路と、その成果。</summary>
    public sealed class IdentificationRoute
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>この経路で確定できた人数。</summary>
        public int Count { get; set; }

        /// <summary>確定と言い切れる経路か（推定ではないか）。</summary>
        public bool IsCertain { get; set; }

        /// <summary>いま使える状態か。</summary>
        public bool Available { get; set; } = true;

        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// 「他人のキャラクターを確定できているか」を経路ごとに整理する。
    ///
    /// 出品データに含まれるはずの持ち主の識別子は、現在サーバーから送られてこない。
    /// そのため、確定に使えるのは別の経路から得た識別子だけになる。
    /// どの経路がどれだけ効いているのかを見えるようにして、
    /// 足りない部分を補えるようにする。
    /// </summary>
    public static class IdentificationReport
    {
        public static List<IdentificationRoute> Build()
        {
            var identities = Plugin.Identities.All;
            var routes = new List<IdentificationRoute>();

            // --- 確定できる経路 ---

            routes.Add(new IdentificationRoute
            {
                Name = "冒険者名刺",
                Description = "識別子を指定してサーバーに問い合わせ、名前を得る",
                Count = identities.Count(i => i.Source == IdentitySource.CharaCard),
                IsCertain = true,
                Note = "1 件ずつ手動で照会します。最も確実です。",
            });

            routes.Add(new IdentificationRoute
            {
                Name = "周囲で見かけた",
                Description = "同じ場所にいたキャラクターから識別子と名前を読み取る",
                Count = identities.Count(i => i.Source == IdentitySource.ObjectTable),
                IsCertain = true,
                Note = "街に居るだけで貯まります。相手を見かける必要があります。",
            });

            routes.Add(new IdentificationRoute
            {
                Name = "フレンド / FC / LS / パーティ",
                Description = "ゲームが持っているメンバーリストから読み取る",
                Count = identities.Count(i =>
                    i.Source is IdentitySource.Friend or IdentitySource.FreeCompany
                        or IdentitySource.Linkshell or IdentitySource.Party),
                IsCertain = true,
                Note = "関わりのある相手に限られます。",
            });

            routes.Add(new IdentificationRoute
            {
                Name = "製作者署名",
                Description = "アイテムに刻まれた製作者の識別子から名前を辿る",
                Count = identities.Count(i => i.Source == IdentitySource.MarketBoard),
                IsCertain = true,
                Note = "製作者は確定できます。ただし「製作者＝出品者」は別の話です。",
            });

            // --- 使えなくなった経路 ---

            var ownerIdListings = Plugin.Listings.Snapshot().Count(l => l.OwnerContentId != 0);

            routes.Add(new IdentificationRoute
            {
                Name = "出品データの持ち主識別子",
                Description = "マーケットの出品に含まれる、持ち主の識別子",
                Count = ownerIdListings,
                IsCertain = true,
                Available = ownerIdListings > 0,
                Note = ownerIdListings > 0
                    ? "取得できています。これがあれば出品者を直接特定できます。"
                    : "サーバーから送られてきていません（以前は取得できていました）。"
                      + "そのため、出品者を直接特定することはできません。",
            });

            // --- 状況証拠（確定ではない） ---

            routes.Add(new IdentificationRoute
            {
                Name = "状況からの推定",
                Description = "購入の直後に出品が現れた、製作者が偏っている、などの積み重ね",
                Count = Plugin.Retainers.Snapshot().Count(p =>
                    !p.IsOwnerCertain && !string.IsNullOrEmpty(p.GuessedOwnerName)),
                IsCertain = false,
                Note = "確定ではありません。外れることがあるため、既定では名前を伏せています。",
            });

            return routes;
        }

        /// <summary>持ち主を確定できているリテイナーの数。</summary>
        public static (int Certain, int Guessed, int Unknown) RetainerSummary()
        {
            var profiles = Plugin.Retainers.Snapshot().Where(p => !p.IsMine).ToList();

            var certain = profiles.Count(p => p.IsOwnerCertain);
            var guessed = profiles.Count(p => !p.IsOwnerCertain && !string.IsNullOrEmpty(p.GuessedOwnerName));

            return (certain, guessed, profiles.Count - certain - guessed);
        }
    }
}
