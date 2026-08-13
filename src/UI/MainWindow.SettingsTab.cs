using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Game;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private static readonly string[] RetentionLabels = { "1日", "3日", "5日", "1週間", "無期限" };
        private static readonly int[] RetentionValues = { 1, 3, 5, 7, 0 };

        private static readonly string[] FavoriteRetentionLabels = { "2週間", "1ヶ月", "3ヶ月", "無期限" };
        private static readonly int[] FavoriteRetentionValues = { 14, 30, 90, 0 };

        private bool _confirmClear;
        private string _importText = string.Empty;
        private string _importPath = string.Empty;
        private string _importStatus = string.Empty;
        private bool _showImport;

        private List<Data.ExternalSource>? _externalSources;

        /// <summary>
        /// 同じような記録を持っている他のプラグインを探して、そこから直接取り込む。
        /// 手作業での変換や貼り付けをせずに済む。
        /// </summary>
        private void DrawExternalPluginImport()
        {
            ImGui.TextColored(ColorMuted, "他のプラグインの記録から取り込む");
            ImGui.TextWrapped(
                "同じように「誰がどのキャラクターか」を集めているプラグインが手元にあれば、" +
                "その記録をそのまま取り込めます。読み取り専用で開くため、相手のデータは変更しません。");

            ImGui.Spacing();

            if (ImGui.Button("手元のプラグインを探す"))
                _externalSources = Data.PluginDataImporter.Detect();

            if (_externalSources == null) return;

            if (_externalSources.Count == 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorMuted, "対応するプラグインの記録は見つかりませんでした。");
                return;
            }

            ImGui.Spacing();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##external_sources", 4, flags)) return;

            ImGui.TableSetupColumn("プラグイン", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("大きさ", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("最終更新", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("取り込み", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();

            foreach (var source in _externalSources)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(source.PluginName);
                AttachTooltip(source.Path);

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, $"{source.FileSize / 1024:N0} KB");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, source.UpdatedLocal.ToString("M/d HH:mm"));

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"取り込む##ext_{source.PluginName}"))
                {
                    var result = Data.PluginDataImporter.Import(source);
                    _importStatus = $"{source.PluginName}: {result.Summary}" +
                                    (result.Problems.Count > 0
                                        ? "\n" + string.Join("\n", result.Problems)
                                        : string.Empty);
                }
            }

            ImGui.EndTable();
        }

        /// <summary>
        /// 識別子と名前の対応を、外から取り込む／外へ書き出す。
        ///
        /// 他のプラグインが貯めた記録を活かしたり、別のキャラクターへ移したりできる。
        /// </summary>
        private void DrawIdentityImportExport()
        {
            ImGui.Spacing();

            if (ImGui.Button(_showImport ? "対応表の取り込みを閉じる" : "対応表を取り込む / 書き出す"))
                _showImport = !_showImport;
            AttachTooltip(
                "識別子と名前の対応を、外部のデータから取り込めます。\n" +
                "他のプラグインが貯めた記録を活かしたり、別のキャラクターへ移したりできます。");

            if (!_showImport) return;

            ImGui.Indent(10);

            DrawExternalPluginImport();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(ColorMuted, "ファイルや貼り付けから取り込む");
            ImGui.TextWrapped(
                "1 行に 1 人ずつ、次の並びで貼り付けてください（カンマ区切り / タブ区切りのどちらでも可）。\n" +
                "　識別子, 名前, ワールドID（省略可）, LodestoneID（省略可）\n" +
                "見出し行や、識別子が数字でない行は自動的に読み飛ばします。");

            ImGui.Spacing();

            // ファイルから直接読み込む（貼り付けの手間を省く）
            ImGui.SetNextItemWidth(420);
            var path = _importPath;
            if (ImGui.InputTextWithHint("##import_path",
                    "ファイルのパス（例 C:\\path\\to\\identities.csv）", ref path, 512))
                _importPath = path;

            ImGui.SameLine();
            if (ImGui.Button("ファイルから取り込む"))
            {
                var result = Data.IdentityImporter.ImportFile(_importPath);
                _importStatus = result.Summary +
                                (result.Problems.Count > 0 ? "\n" + string.Join("\n", result.Problems) : string.Empty);
            }
            AttachTooltip("エクスプローラーからファイルをドラッグしてパスを貼り付けることもできます。");

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted, "または、内容を直接貼り付けてください:");

            var text = _importText;
            if (ImGui.InputTextMultiline("##import_text", ref text, 200_000,
                    new System.Numerics.Vector2(-1, 120)))
                _importText = text;

            if (ImGui.Button("取り込む"))
            {
                var result = Data.IdentityImporter.Import(_importText);
                _importStatus = result.Summary +
                                (result.Problems.Count > 0 ? "\n" + string.Join("\n", result.Problems) : string.Empty);
            }

            ImGui.SameLine();
            if (ImGui.Button("いまの対応表を書き出す"))
            {
                ImGui.SetClipboardText(Data.IdentityImporter.Export());
                _importStatus = "確定している対応をクリップボードへ書き出しました。";
            }
            AttachTooltip("推定を除いた、確定している対応だけを書き出します。");

            ImGui.SameLine();
            if (ImGui.Button("貼り付け欄を消去"))
            {
                _importText = string.Empty;
                _importStatus = string.Empty;
            }

            if (!string.IsNullOrEmpty(_importStatus))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(_importStatus);
            }

            ImGui.Unindent(10);
        }

        private void DrawSettingsTab()
        {
            var config = Plugin.Config;
            ImGui.Spacing();

            // ---- 取り込み ----
            if (ImGui.CollapsingHeader("取り込み", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent(10);

                var hook = config.EnableHookCapture;
                if (ImGui.Checkbox("売却履歴を自動で取り込む", ref hook))
                {
                    config.EnableHookCapture = hook;
                    config.Save();
                }
                AttachTooltip(
                    "リテイナーの売却履歴が読み込まれたタイミングで自動的に記録します。\n" +
                    "切り替えはプラグインの再読み込み後に反映されます。");

                var addon = config.EnableAddonCapture;
                if (ImGui.Checkbox("売却履歴ウィンドウの表示内容からも取り込む", ref addon))
                {
                    config.EnableAddonCapture = addon;
                    config.Save();
                }
                AttachTooltip(
                    "上の自動取り込みが使えない場合の保険です。\n" +
                    "ゲーム内の「売却履歴」ウィンドウを開いている間だけ読み取ります。");

                var notify = config.NotifyNewSales;
                if (ImGui.Checkbox("新しい売却をチャットに表示する", ref notify))
                {
                    config.NotifyNewSales = notify;
                    config.Save();
                }

                var openWith = config.OpenOnRetainerHistory;
                if (ImGui.Checkbox("売却履歴ウィンドウを開いたら、このウィンドウも開く", ref openWith))
                {
                    config.OpenOnRetainerHistory = openWith;
                    config.Save();
                }

                var autoOpen = config.AutoOpenOnLoad;
                if (ImGui.Checkbox("ログイン時にウィンドウを開く", ref autoOpen))
                {
                    config.AutoOpenOnLoad = autoOpen;
                    config.Save();
                }

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- 取りこぼし対策 ----
            if (ImGui.CollapsingHeader("取りこぼし対策", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent(10);
                ImGui.TextWrapped(
                    "ゲーム内の売却履歴はリテイナーごと 20 件しか残りません。" +
                    "こまめに履歴を開くほど取りこぼしが減ります。");
                ImGui.Spacing();

                var withAr = config.AutoOpenHistoryWithAutoRetainer;
                if (ImGui.Checkbox("AutoRetainer の巡回中に売却履歴を自動取得する", ref withAr))
                {
                    config.AutoOpenHistoryWithAutoRetainer = withAr;
                    config.Save();
                }
                AttachTooltip(
                    "AutoRetainer が各リテイナーの用事を終えたタイミングで、売却履歴を開いて取り込み、すぐ閉じます。\n" +
                    "AutoRetainer を導入していない場合は何も起きません。");

                var onMenu = config.AutoOpenHistoryOnRetainerMenu;
                if (ImGui.Checkbox("リテイナーのメニューを開いたときに自動取得する", ref onMenu))
                {
                    config.AutoOpenHistoryOnRetainerMenu = onMenu;
                    config.Save();
                }
                AttachTooltip(
                    "自分でリテイナーに話しかけたときに、売却履歴を自動で開いて取り込みます。\n" +
                    "AutoRetainer を使っている場合は上の項目だけを有効にしてください（操作が競合することがあります）。");

                var diff = config.EnableSellListDiff;
                if (ImGui.Checkbox("出品リストの差分から売却を検出する", ref diff))
                {
                    config.EnableSellListDiff = diff;
                    config.Save();
                }
                AttachTooltip(
                    "出品リストを開くたびに内容を記録し、消えた出品を「売れた可能性のある取引」として残します。\n" +
                    "購入者名は分かりませんが、履歴が溢れた分の売上も把握できます。");

                var warn = config.WarnHistoryGap;
                if (ImGui.Checkbox("取りこぼしの可能性を警告する", ref warn))
                {
                    config.WarnHistoryGap = warn;
                    config.Save();
                }

                ImGui.Spacing();
                ImGui.TextColored(ColorMuted,
                    $"自動取得: {Plugin.AutoOpen.AutoOpenCount} 回 / 最終結果: {Plugin.AutoOpen.LastResult}");
                ImGui.TextColored(ColorMuted,
                    $"差分検出: {Plugin.SellListWatcher.DetectedCount} 件 / 未確定 {Plugin.Pending.UnconfirmedCount} 件");

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- 再出品の追跡 ----
            if (ImGui.CollapsingHeader("再出品の追跡", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent(10);
                ImGui.TextWrapped(
                    "マーケットの出品情報には出品者のキャラクター名が含まれないため、" +
                    "「買った直後に同じ物を出品し始めた出品者」を状況証拠から推定します。確定情報ではありません。");
                ImGui.Spacing();

                var resale = config.EnableResaleTracking;
                if (ImGui.Checkbox("マーケットボードで見た出品を記録する", ref resale))
                {
                    config.EnableResaleTracking = resale;
                    config.Save();
                }
                AttachTooltip(
                    "自分でマーケットボードを開いたときのデータだけを記録します。" +
                    "プラグインから検索を自動で投げることはありません。");

                var identity = config.EnableIdentityCollection;
                if (ImGui.Checkbox("出品者の名前を解決するための対応表を集める", ref identity))
                {
                    config.EnableIdentityCollection = identity;
                    config.Save();
                }
                AttachTooltip(
                    "周囲に見えたプレイヤーや、フレンド / FC / リンクシェルのメンバーから\n" +
                    "識別子とキャラクター名の対応を集めます。ローカルに保存するだけで外部送信はしません。");

                var menu = config.EnableSellerContextMenu;
                if (ImGui.Checkbox("マーケットの出品を右クリックしたら「出品者を特定する」を出す", ref menu))
                {
                    config.EnableSellerContextMenu = menu;
                    config.Save();
                }
                AttachTooltip(
                    "マーケットボードで、出品されているアイテムの行を右クリックしたときのメニューに項目を追加します。\n" +
                    "対応表に名前があればその場で表示し、無ければ冒険者名刺で 1 件だけ照会します。\n" +
                    "結果はチャットに出ます（その相手があなたから買っていれば、その実績も表示します）。");

                if (config.EnableSellerContextMenu)
                {
                    ImGui.Indent(20);
                    var itemRowOnly = config.SellerMenuOnItemRowOnly;
                    if (ImGui.Checkbox("アイテムの行を右クリックしたときだけ出す", ref itemRowOnly))
                    {
                        config.SellerMenuOnItemRowOnly = itemRowOnly;
                        config.Save();
                    }
                    AttachTooltip(
                        "オフにすると、ウィンドウ枠の右クリックメニュー（「初期位置に戻す」などが並ぶ方）にも出ます。\n" +
                        "アイテムの行を右クリックしても項目が出ない場合は、こちらをオフにしてお試しください。");
                    ImGui.Unindent(20);
                }

                var overlay = config.ShowSellerOverlay;
                if (ImGui.Checkbox("出品一覧の横に出品者の小窓を表示する", ref overlay))
                {
                    config.ShowSellerOverlay = overlay;
                    config.Save();
                }
                AttachTooltip(
                    "マーケットボードでアイテムの出品一覧を開いている間だけ、その横に小窓を出します。\n" +
                    "各行の出品者名と、分からない相手を調べる「特定」ボタンが並びます。");

                var closeCard = config.CloseCharaCardAfterLookup;
                if (ImGui.Checkbox("名刺で出品者を調べたあと、名刺を自動で閉じる", ref closeCard))
                {
                    config.CloseCharaCardAfterLookup = closeCard;
                    config.Save();
                }
                AttachTooltip(
                    "出品者タブの「名刺」ボタンで調べたとき、読み取り後に名刺のウィンドウを閉じます。\n" +
                    "この照会はボタンを押した 1 件だけに対して行われ、まとめて自動照会することはありません。");

                var harvest = config.HarvestCrafterNames;
                if (ImGui.Checkbox("アイテム説明の製作者名から対応表を集める", ref harvest))
                {
                    config.HarvestCrafterNames = harvest;
                    config.Save();
                }
                AttachTooltip(
                    "マーケットで製作品にカーソルを合わせると、説明に製作者名が表示されます。\n" +
                    "そのとき同時にアイテムへ刻まれた製作者の識別子も読めるので、\n" +
                    "名刺を使わずに識別子と名前の対応を集められます。\n" +
                    $"取得数: {Plugin.CrafterNames.HarvestCount} / 直近: {Plugin.CrafterNames.LastHarvest}");

                var chatWatch = config.EnableChatRetainerWatch;
                if (ImGui.Checkbox("チャットでのリテイナー名の言及を手がかりにする", ref chatWatch))
                {
                    config.EnableChatRetainerWatch = chatWatch;
                    config.Save();
                }
                AttachTooltip(
                    "「○○（リテイナー名）に出しています」といった発言があった場合、その発言者を候補として記録します。\n" +
                    "判定に使うのは観測済みのリテイナー名が含まれるかどうかだけで、発言内容は保存しません。\n" +
                    $"検出数: {Plugin.ChatWatcher.MentionCount} / 直近: {Plugin.ChatWatcher.LastMention}");

                var verify = config.VerifyNamesOnLodestone;
                if (ImGui.Checkbox("推定した名前を Lodestone で裏取りする", ref verify))
                {
                    config.VerifyNamesOnLodestone = verify;
                    config.Save();
                }
                AttachTooltip(
                    "推定した名前のキャラクターが自分のデータセンターに実在するかを確認します。\n" +
                    "実在しなければ推定が誤っていると分かります。外部サイトへの通信が発生し、" +
                    "リテイナータブのボタンを押したときだけ照会します。");

                var inference = config.EnableOwnerInference;
                if (ImGui.Checkbox("購入履歴からリテイナーの持ち主を推定する", ref inference))
                {
                    config.EnableOwnerInference = inference;
                    config.Save();
                }
                AttachTooltip(
                    "マーケットの購入履歴には買い手の名前が公開されています。\n" +
                    "「あるリテイナーが商品を出す直前に、同じ商品を買っていた人」を突き合わせて持ち主を推定します。\n" +
                    "結果は「リテイナー」タブで確認できます。確定情報ではありません。");

                var window = config.ResaleWindowHours;
                ImGui.SetNextItemWidth(240);
                if (ImGui.SliderInt("購入から何時間以内の出品を候補にするか", ref window, 1, 168))
                {
                    config.ResaleWindowHours = window;
                    config.Save();
                    _resaleCacheBuyer = null;
                }

                var listingDays = config.ListingRetentionDays;
                ImGui.SetNextItemWidth(240);
                if (ImGui.SliderInt("出品記録の保持日数", ref listingDays, 1, 60))
                {
                    config.ListingRetentionDays = listingDays;
                    config.Save();
                }

                var autoTrack = config.UniversalisAutoTrack;
                if (ImGui.Checkbox("Universalis から出品を定期取得する", ref autoTrack))
                {
                    config.UniversalisAutoTrack = autoTrack;
                    config.Save();
                }
                AttachTooltip(
                    "最近購入されたアイテムの出品状況を、一定間隔で 1 件ずつ取得して追跡材料にします。\n" +
                    "「Universalis 連携」を有効にしている場合のみ動作します。");

                if (config.UniversalisAutoTrack)
                {
                    var interval = config.UniversalisTrackIntervalMinutes;
                    ImGui.SetNextItemWidth(240);
                    if (ImGui.SliderInt("取得間隔（分）", ref interval, 5, 120))
                    {
                        config.UniversalisTrackIntervalMinutes = interval;
                        config.Save();
                    }

                    if (!config.EnableUniversalis)
                        ImGui.TextColored(ColorAccent, "※「Universalis 連携」が無効のため動作しません。");
                }

                ImGui.Spacing();
                ImGui.TextColored(ColorMuted,
                    $"出品記録: {Plugin.Listings.Count:N0} 件 / 対応表: {Plugin.Identities.ConfirmedCount:N0} 人（推定含め {Plugin.Identities.Count:N0} 件）");
                ImGui.TextColored(ColorMuted,
                    $"アカウントの識別子が分かっている人: {Plugin.Identities.AccountKnownCount:N0} 人");
                AttachTooltip(
                    "同じアカウントのキャラクターを結び付けるための情報です。\n" +
                    "その人物を実際に見かけたときに記録されます。\n" +
                    "買い物用のサブキャラで動いている相手でも、本体のキャラクターが分かることがあります。");

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- 保持期間 ----
            if (ImGui.CollapsingHeader("ログの保持期間", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent(10);

                var index = Math.Max(0, Array.IndexOf(RetentionValues, config.RetentionDays));
                ImGui.SetNextItemWidth(140);
                if (ImGui.Combo("通常の購入者##retention", ref index, RetentionLabels, RetentionLabels.Length))
                {
                    config.RetentionDays = RetentionValues[index];
                    config.Save();
                }
                AttachTooltip("この期間を過ぎたログは自動的に削除されます。");

                var favIndex = Math.Max(0, Array.IndexOf(FavoriteRetentionValues, config.FavoriteRetentionDays));
                ImGui.SetNextItemWidth(140);
                if (ImGui.Combo("お気に入りの購入者##favretention", ref favIndex,
                        FavoriteRetentionLabels, FavoriteRetentionLabels.Length))
                {
                    config.FavoriteRetentionDays = FavoriteRetentionValues[favIndex];
                    config.Save();
                }
                AttachTooltip("お気に入り登録した購入者のログは、こちらの期間まで残ります。");

                ImGui.TextColored(ColorMuted,
                    $"現在の保存件数: {Plugin.Store.Count:N0} 件 / お気に入り {Plugin.Favorites.Count} 人");

                if (ImGui.Button("今すぐ古いログを整理"))
                {
                    var removed = Plugin.Store.Prune(config, Plugin.Favorites);
                    Plugin.Store.Save(force: true);
                    _statsDirty = true;
                    Plugin.ChatGui.Print($"[Market Stats] 古いログを {removed} 件削除しました。");
                }

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- 集計 ----
            if (ImGui.CollapsingHeader("集計", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent(10);

                var window = config.SessionWindowSeconds;
                ImGui.SetNextItemWidth(240);
                if (ImGui.SliderInt("まとめ買いとみなす時間幅（秒）", ref window, 30, 1800))
                {
                    config.SessionWindowSeconds = window;
                    config.Save();
                    _statsDirty = true;
                }
                AttachTooltip(
                    "同じ人が同じアイテムを連続して買った場合に、何秒以内なら 1 回のまとめ買いとして数えるかです。\n" +
                    "既定の 300 秒なら「99個 × 10枠」を 1 回として表示します。");

                var mannequin = config.IncludeMannequinSales;
                if (ImGui.Checkbox("購入者名のない取引（マネキン販売等）も表示する", ref mannequin))
                {
                    config.IncludeMannequinSales = mannequin;
                    config.Save();
                    _statsDirty = true;
                }

                var currentOnly = config.FilterCurrentCharacterOnly;
                if (ImGui.Checkbox("ログイン中のキャラクターの売上のみ表示する", ref currentOnly))
                {
                    config.FilterCurrentCharacterOnly = currentOnly;
                    config.Save();
                    _statsDirty = true;
                }

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- Lodestone ----
            if (ImGui.CollapsingHeader("Lodestone"))
            {
                ImGui.Indent(10);

                var regionIndex = Math.Max(0, Array.IndexOf(LodestoneLink.Regions, config.LodestoneRegion));
                ImGui.SetNextItemWidth(120);
                if (ImGui.Combo("地域##lodestone", ref regionIndex,
                        LodestoneLink.Regions, LodestoneLink.Regions.Length))
                {
                    config.LodestoneRegion = LodestoneLink.Regions[regionIndex];
                    config.Save();
                }

                var filterDc = config.LodestoneFilterByDataCenter;
                if (ImGui.Checkbox("自分のデータセンターで絞り込む", ref filterDc))
                {
                    config.LodestoneFilterByDataCenter = filterDc;
                    config.Save();
                }
                AttachTooltip(
                    "売却履歴には購入者のワールドが記録されないため、検索で絞り込みます。\n" +
                    $"現在のデータセンター: {LodestoneLink.GetCurrentDataCenter()}");

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- Universalis ----
            if (ImGui.CollapsingHeader("Universalis 連携（任意）"))
            {
                ImGui.Indent(10);

                ImGui.TextWrapped(
                    "有効にすると、売れたアイテムの現在の出品状況を Universalis から取得できます。" +
                    "外部サイトへの通信が発生します。");

                var enabled = config.EnableUniversalis;
                if (ImGui.Checkbox("Universalis 連携を有効にする", ref enabled))
                {
                    config.EnableUniversalis = enabled;
                    config.Save();
                }

                var scope = config.UniversalisScope;
                ImGui.SetNextItemWidth(200);
                if (ImGui.InputTextWithHint("照会範囲##uniscope", "空欄なら自分のデータセンター", ref scope, 32))
                {
                    config.UniversalisScope = scope;
                    config.Save();
                }
                AttachTooltip("ワールド名 / データセンター名 / リージョン名を指定できます（例: Mana, Japan）。");

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- データ管理 ----
            if (ImGui.CollapsingHeader("データ管理"))
            {
                ImGui.Indent(10);

                if (ImGui.Button("CSV をクリップボードにコピー"))
                {
                    ImGui.SetClipboardText(Plugin.Store.ExportCsv());
                    Plugin.ChatGui.Print("[Market Stats] 売却ログを CSV 形式でコピーしました。");
                }

                ImGui.SameLine();
                if (ImGui.Button("保存フォルダを開く"))
                    LodestoneLink.OpenUrl(Plugin.PluginInterface.GetPluginConfigDirectory());

                DrawIdentityImportExport();

                ImGui.Spacing();
                if (ImGui.Button("出品記録を消去"))
                {
                    Plugin.Listings.Clear();
                    _resaleCacheBuyer = null;
                    Plugin.ChatGui.Print("[Market Stats] 出品記録を消去しました。");
                }
                AttachTooltip("マーケットで観測した出品の記録（再出品の追跡に使うもの）を消します。");

                ImGui.SameLine();
                if (ImGui.Button("対応表を消去"))
                {
                    Plugin.Identities.Clear();
                    Plugin.ChatGui.Print("[Market Stats] 出品者の対応表を消去しました。");
                }
                AttachTooltip("識別子とキャラクター名の対応表を消します。");

                ImGui.SameLine();
                if (ImGui.Button("未確定売却を消去"))
                {
                    Plugin.Pending.Clear();
                    Plugin.ChatGui.Print("[Market Stats] 未確定売却と取りこぼし警告を消去しました。");
                }

                ImGui.Spacing();
                ImGui.Checkbox("すべてのログを削除する（チェックしてから実行）", ref _confirmClear);
                if (_confirmClear)
                {
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.6f, 0.2f, 0.2f, 1f));
                    if (ImGui.Button("実行"))
                    {
                        Plugin.Store.Clear();
                        _statsDirty = true;
                        _confirmClear = false;
                        Plugin.ChatGui.Print("[Market Stats] すべての売却ログを削除しました。");
                    }
                    ImGui.PopStyleColor();
                }

                ImGui.Unindent(10);
                ImGui.Spacing();
            }

            // ---- 診断 ----
            if (ImGui.CollapsingHeader("診断"))
            {
                ImGui.Indent(10);

                ImGui.Text($"自動取り込み: {Plugin.Capture.HookStatus}");
                ImGui.Text($"取り込み件数: 自動 {Plugin.Capture.HookCaptureCount} / ウィンドウ {Plugin.Capture.AddonCaptureCount}");
                ImGui.Text($"最終取り込み: " +
                           (Plugin.Capture.LastCaptureLocal == DateTime.MinValue
                               ? "なし"
                               : Plugin.Capture.LastCaptureLocal.ToString("yyyy-MM-dd HH:mm:ss")));

                if (!Plugin.Capture.HookActive)
                {
                    ImGui.Spacing();
                    ImGui.TextWrapped(
                        "自動取り込みが使えない状態です。ゲームのアップデート直後などに起こります。" +
                        "この場合でも、ゲーム内の売却履歴ウィンドウを開けば取り込みは行われます。");
                }

                ImGui.Spacing();
                ImGui.Text($"直近の右クリックメニュー: {Plugin.SellerMenu.LastMenuAddon}" +
                           (Plugin.SellerMenu.LastMenuLocal == DateTime.MinValue
                               ? string.Empty
                               : $" ({Plugin.SellerMenu.LastMenuLocal:HH:mm:ss})"));
                AttachTooltip(
                    "最後にコンテキストメニューが開いたときのアドオン名です。\n" +
                    "マーケットの出品を右クリックしたあとにここを見ると、" +
                    "どの画面として認識されているかが分かります。");

                ImGui.Spacing();
                if (ImGui.Button("売却履歴ウィンドウの内部データをログへ出力"))
                    Plugin.Capture.DumpArrays();
                AttachTooltip("不具合報告用です。ゲーム内の売却履歴ウィンドウを開いた状態で押してください。");

                var debug = Plugin.Config.DebugMode;
                if (ImGui.Checkbox("デバッグ表示", ref debug))
                {
                    Plugin.Config.DebugMode = debug;
                    Plugin.Config.Save();
                }

                if (Plugin.Config.DebugMode)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(ColorMuted, $"保存件数: {Plugin.Store.Count}");
                    ImGui.TextColored(ColorMuted, $"表示中: {_filtered.Count} 件 / 購入者 {_stats.Count} 人");
                    ImGui.TextColored(ColorMuted,
                        $"リテイナー内訳: {string.Join(", ",
                            _filtered.GroupBy(r => r.RetainerName)
                                     .Select(g => $"{(string.IsNullOrEmpty(g.Key) ? "(不明)" : g.Key)}={g.Count()}"))}");
                }

                ImGui.Unindent(10);
            }
        }
    }
}
