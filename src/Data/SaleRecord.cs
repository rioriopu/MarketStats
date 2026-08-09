using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>
    /// リテイナー1件分の売却レコード。ゲーム内の売却履歴はリテイナーごとに最大20件しか
    /// 保持されないため、取り込んだものをこの形でプラグイン側に永続化する。
    /// </summary>
    public sealed class SaleRecord
    {
        /// <summary>売れたアイテムの ItemId。</summary>
        public uint ItemId { get; set; }

        /// <summary>HQ かどうか。</summary>
        public bool Hq { get; set; }

        /// <summary>この取引で売れた個数（1枠分）。</summary>
        public uint Quantity { get; set; }

        /// <summary>この取引の合計金額（ゲーム内履歴に表示される金額）。</summary>
        public ulong TotalGil { get; set; }

        /// <summary>売却時刻（UnixTime 秒, UTC 基準）。</summary>
        public long UnixTime { get; set; }

        /// <summary>購入者のキャラクター名。マネキン販売時は空になることがある。</summary>
        public string BuyerName { get; set; } = string.Empty;

        /// <summary>マネキン（ハウス内展示）経由の販売か。</summary>
        public bool OnMannequin { get; set; }

        /// <summary>売却したリテイナーの名前。</summary>
        public string RetainerName { get; set; } = string.Empty;

        /// <summary>リテイナーの所有キャラクターの ContentId。</summary>
        public ulong OwnerContentId { get; set; }

        /// <summary>リテイナーの所有キャラクター名（表示用）。</summary>
        public string OwnerName { get; set; } = string.Empty;

        /// <summary>売却が行われたワールド名（自分のホームワールド）。</summary>
        public string OwnerWorld { get; set; } = string.Empty;

        /// <summary>ローカルタイムでの売却日時。</summary>
        [JsonIgnore]
        public DateTime LocalTime => DateTimeOffset.FromUnixTimeSeconds(UnixTime).LocalDateTime;

        /// <summary>1個あたりの単価（端数は切り捨て）。</summary>
        [JsonIgnore]
        public ulong UnitPrice => Quantity == 0 ? TotalGil : TotalGil / Quantity;

        /// <summary>
        /// 重複排除用キー。ゲーム側の履歴には取引 ID が無いため、内容が完全一致するものを
        /// 同一取引とみなす。ただし「同じ秒に同じ内容の取引が複数件」（99個×10枠の同時購入など）
        /// もあり得るので、<see cref="SaleStore"/> ではキー単位の件数を突き合わせてマージする。
        /// </summary>
        [JsonIgnore]
        public string DedupeKey =>
            $"{OwnerContentId:X}|{RetainerName}|{UnixTime}|{ItemId}|{(Hq ? 1 : 0)}|{Quantity}|{TotalGil}|{BuyerName}";

        /// <summary>購入者名が取得できているか（マネキン販売等では空になる）。</summary>
        [JsonIgnore]
        public bool HasBuyer => !string.IsNullOrWhiteSpace(BuyerName);
    }
}
