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
