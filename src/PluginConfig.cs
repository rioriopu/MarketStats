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

        // ---- 取りこぼし対策 ----

        /// <summary>AutoRetainer の巡回に合わせて、各リテイナーの売却履歴を自動で開いて取り込む。</summary>
        public bool AutoOpenHistoryWithAutoRetainer { get; set; } = false;

        /// <summary>リテイナーのメニューを開いたときに、売却履歴を自動で開いて取り込む。</summary>
        public bool AutoOpenHistoryOnRetainerMenu { get; set; } = false;

        /// <summary>出品リストの差分から「売れたらしい取引」を検出する。</summary>
        public bool EnableSellListDiff { get; set; } = true;

        /// <summary>履歴が 20 件で溢れて取りこぼした可能性がある場合に警告を残す。</summary>
        public bool WarnHistoryGap { get; set; } = true;

        // ---- 再出品の追跡 ----

        /// <summary>マーケットボードで見た出品を記録し、再出品の候補を推定する。</summary>
        public bool EnableResaleTracking { get; set; } = true;

        /// <summary>周囲のプレイヤーやメンバーリストから ContentId ↔ 名前の対応を集める。</summary>
        public bool EnableIdentityCollection { get; set; } = true;

        /// <summary>購入から何時間以内の出品を「再出品の候補」とみなすか。</summary>
        public int ResaleWindowHours { get; set; } = 24;

        /// <summary>購入履歴と出品タイミングから、リテイナーの持ち主を推定する。</summary>
        public bool EnableOwnerInference { get; set; } = true;

        /// <summary>チャットでリテイナー名に言及した発言を手がかりに使う。</summary>
        public bool EnableChatRetainerWatch { get; set; } = false;

        /// <summary>マーケット関連のフック地点で、届いた生データを取り込んで解析する（診断用）。</summary>
        public bool EnablePacketCapture { get; set; } = false;

        /// <summary>推定した名前が実在するかを Lodestone で確認する（外部通信）。</summary>
        public bool VerifyNamesOnLodestone { get; set; } = false;

        /// <summary>観測した出品の保持日数。</summary>
        public int ListingRetentionDays { get; set; } = 14;

        /// <summary>Universalis から、最近購入されたアイテムの出品を定期的に取得して追跡に使う。</summary>
        public bool UniversalisAutoTrack { get; set; } = false;

        /// <summary>Universalis の自動取得の間隔（分）。</summary>
        public int UniversalisTrackIntervalMinutes { get; set; } = 15;

        /// <summary>冒険者名刺で出品者を調べたあと、名刺のウィンドウを自動で閉じる。</summary>
        public bool CloseCharaCardAfterLookup { get; set; } = true;

        /// <summary>マーケットボードの出品を右クリックしたときに「出品者を特定する」を出す。</summary>
        public bool EnableSellerContextMenu { get; set; } = true;

        /// <summary>マーケットの出品一覧を開いている間、その横に出品者の小窓を表示する。</summary>
        public bool ShowSellerOverlay { get; set; } = true;

        /// <summary>
        /// 「出品者を特定する」を、出品されているアイテムの行を右クリックしたときだけ出す。
        /// false にすると、ウィンドウ枠の右クリックメニューにも出る。
        /// </summary>
        public bool SellerMenuOnItemRowOnly { get; set; } = true;

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
