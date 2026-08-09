using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace MarketStats
{
    [Serializable]
    public class PluginConfig : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        // ---- 取り込み ----

        /// <summary>売却履歴パケットのフックによる自動取り込みを有効にするか。</summary>
        public bool EnableHookCapture { get; set; } = true;

        /// <summary>売却履歴ウィンドウ（RetainerHistory）の表示内容からの取り込みを有効にするか。</summary>
        public bool EnableAddonCapture { get; set; } = true;

        /// <summary>新しい売却を取り込んだ時にチャットへ通知するか。</summary>
        public bool NotifyNewSales { get; set; } = false;

        // ---- 保持期間 ----

        /// <summary>通常の購入者のログ保持日数。0 は無期限。</summary>
        public int RetentionDays { get; set; } = 7;

        /// <summary>お気に入り登録した購入者のログ保持日数。0 は無期限。</summary>
        public int FavoriteRetentionDays { get; set; } = 30;

        // ---- 集計 ----

        /// <summary>
        /// 同一購入者・同一アイテムの取引を「まとめ買い1回」として束ねる時間幅（秒）。
        /// 例: 99個 × 10枠 をまとめて 990個 と表示するための窓。
        /// </summary>
        public int SessionWindowSeconds { get; set; } = 300;

        /// <summary>マネキン販売（購入者名なし）も集計に含めるか。</summary>
        public bool IncludeMannequinSales { get; set; } = true;

        /// <summary>現在ログイン中のキャラクターの売上だけを表示するか。</summary>
        public bool FilterCurrentCharacterOnly { get; set; } = false;

        // ---- Lodestone ----

        /// <summary>Lodestone を開くときの地域。jp / na / eu / fr / de</summary>
        public string LodestoneRegion { get; set; } = "jp";

        /// <summary>Lodestone 検索時に自分のデータセンターで絞り込むか。</summary>
        public bool LodestoneFilterByDataCenter { get; set; } = true;

        // ---- Universalis（任意機能・外部通信） ----

        /// <summary>Universalis へのアクセスを許可するか（既定 OFF）。</summary>
        public bool EnableUniversalis { get; set; } = false;

        /// <summary>Universalis の照会範囲。空ならログイン中キャラのデータセンター。</summary>
        public string UniversalisScope { get; set; } = string.Empty;

        // ---- UI ----

        /// <summary>起動時にウィンドウを開くか。</summary>
        public bool AutoOpenOnLoad { get; set; } = false;

        /// <summary>売却履歴ウィンドウを開いたときに自動でこのウィンドウも開くか。</summary>
        public bool OpenOnRetainerHistory { get; set; } = false;

        /// <summary>デバッグ表示（取り込み診断）を出すか。</summary>
        public bool DebugMode { get; set; } = false;

        [JsonIgnore]
        private static IDalamudPluginInterface? _pi;

        public void Init(IDalamudPluginInterface pi) => _pi = pi;

        public void Save() => _pi?.SavePluginConfig(this);
    }
}
