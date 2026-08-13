using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private string _playerSearch = string.Empty;
        private int _playerSort;
        private bool _playerConfirmedOnly = true;
        private ulong _selectedPlayer;

        private static readonly string[] PlayerSortLabels =
        {
            "よく見かける順",
            "最近見かけた順",
            "名前順",
            "登録が新しい順",
        };

        /// <summary>
        /// 記録しているキャラクターの一覧。
        ///
        /// 出品者を名前で出すには「識別子とキャラクター名の対応」が要る。
        /// ここでは、その対応をどれだけ集められているかを確認し、検索できるようにする。
        /// </summary>
        private void DrawPlayersTab()
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "見かけたキャラクターを記録しています。ここに載っている相手なら、" +
                "マーケットの出品者として名前を出せます。改名やワールド移動があっても、同じ人物として追い続けます。");

            ImGui.Spacing();

            ImGui.SetNextItemWidth(240);
            var search = _playerSearch;
            if (ImGui.InputTextWithHint("##player_search", "名前 / 識別子で絞り込み", ref search, 64))
                _playerSearch = search;
            AttachTooltip("過去の名前でも探せます。");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(170);
            var sort = _playerSort;
            if (ImGui.Combo("##player_sort", ref sort, PlayerSortLabels, PlayerSortLabels.Length))
                _playerSort = sort;

            ImGui.SameLine();
            ImGui.Checkbox("確定のみ", ref _playerConfirmedOnly);
            AttachTooltip("推定で名前を当てているものを除きます。");

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted,
                $"記録 {Plugin.Identities.Count:N0} 人（確定 {Plugin.Identities.ConfirmedCount:N0} 人 / " +
                $"アカウント判明 {Plugin.Identities.AccountKnownCount:N0} 人）");

            ImGui.Separator();

            var players = Filter();
            if (players.Count == 0)
            {
                ImGui.TextColored(ColorMuted,
                    Plugin.Identities.Count == 0
                        ? "まだ記録がありません。街などでプレイヤーの近くにいると自動で集まります。"
                        : "条件に合うキャラクターがいません。");
                return;
            }

            var listHeight = MathF.Max(140f, ImGui.GetContentRegionAvail().Y * 0.55f);
            if (ImGui.BeginChild("##player_list", new System.Numerics.Vector2(0, listHeight), true))
                DrawPlayerTable(players);
            ImGui.EndChild();

            ImGui.Spacing();
            DrawPlayerDetail();
        }

        private List<OwnerIdentity> Filter()
        {
            var players = string.IsNullOrWhiteSpace(_playerSearch)
                ? Plugin.Identities.All
                : Plugin.Identities.SearchByAnyName(_playerSearch.Trim());

            // 識別子でも探せるようにする。
            if (players.Count == 0 && TryParseId(_playerSearch, out var id))
            {
                var byId = Plugin.Identities.Resolve(id);
                players = byId == null ? new List<OwnerIdentity>() : new List<OwnerIdentity> { byId };
            }

            if (_playerConfirmedOnly)
                players = players.Where(p => p.IsConfirmed).ToList();

            return _playerSort switch
            {
                1 => players.OrderByDescending(p => p.LastSeenUnix).ToList(),
                2 => players.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                3 => players.OrderByDescending(p => p.FirstSeenUnix).ToList(),
                _ => players.OrderByDescending(p => p.SeenCount).ThenByDescending(p => p.LastSeenUnix).ToList(),
            };
        }

        private void DrawPlayerTable(List<OwnerIdentity> players)
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##players", 6, flags)) return;

            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("ワールド", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("識別子", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("遭遇", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("出所", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("最終確認", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var player in players.Take(500))
            {
                ImGui.TableNextRow();
                ImGui.PushID(player.ContentId.ToString());

                ImGui.TableNextColumn();
                var selected = _selectedPlayer == player.ContentId;
                if (ImGui.Selectable("##row", selected, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedPlayer = player.ContentId;
                ImGui.SameLine(0, 0);

                ImGui.TextColored(player.IsConfirmed ? ColorLink : ColorAccent,
                    player.Source == IdentitySource.Inferred ? $"{player.Name}（推定）" : player.Name);

                if (player.History.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorMuted, "*");
                    AttachTooltip("以前は別の名前でした。行を選ぶと履歴が見られます。");
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, ResolveWorld(player.WorldId));

                ImGui.TableNextColumn();
                DrawCopyableId(player.ContentId, $"pid_{player.ContentId}");

                ImGui.TableNextColumn();
                ImGui.Text(player.SeenCount > 0 ? $"{player.SeenCount:N0}" : "-");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, DescribeSource(player.Source));

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted,
                    player.LastSeenUnix == 0
                        ? "-"
                        : DateTimeOffset.FromUnixTimeSeconds(player.LastSeenUnix).LocalDateTime.ToString("M/d HH:mm"));

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private void DrawPlayerDetail()
        {
            if (_selectedPlayer == 0)
            {
                ImGui.TextColored(ColorMuted, "キャラクターを選ぶと、詳しい情報が表示されます。");
                return;
            }

            var player = Plugin.Identities.Resolve(_selectedPlayer);
            if (player == null)
            {
                ImGui.TextColored(ColorMuted, "選択したキャラクターの情報が見つかりませんでした。");
                return;
            }

            ImGui.TextColored(ColorAccent, player.Name);
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, $"@ {ResolveWorld(player.WorldId)}");

            ImGui.SameLine();
            if (ImGui.SmallButton("Lodestone")) LodestoneOpen(player.Name);

            ImGui.SameLine();
            if (ImGui.SmallButton("詳しく調べる"))
                OpenProbeForContentId(player.ContentId);

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted,
                $"識別子 0x{player.ContentId:X}" +
                (player.AccountId != 0 ? $" / アカウント 0x{player.AccountId:X}" : string.Empty) +
                (player.LodestoneId > 0 ? $" / Lodestone {player.LodestoneId}" : string.Empty));

            if (player.FirstSeenUnix > 0)
                ImGui.TextColored(ColorMuted,
                    $"初めて見かけた: {DateTimeOffset.FromUnixTimeSeconds(player.FirstSeenUnix).LocalDateTime:yyyy/M/d HH:mm}" +
                    $" / 遭遇 {player.SeenCount:N0} 回");

            // 同じアカウントの別キャラクター
            if (player.AccountId != 0)
            {
                var alts = Plugin.Identities.SameAccount(player.AccountId, player.ContentId);
                if (alts.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(ColorFavorite, "同じアカウントの別キャラクター:");
                    foreach (var alt in alts.Take(10))
                    {
                        ImGui.Bullet();
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"{alt.Name}##alt_{alt.ContentId}"))
                            _selectedPlayer = alt.ContentId;
                        ImGui.SameLine();
                        ImGui.TextColored(ColorMuted, $"@ {ResolveWorld(alt.WorldId)}");
                    }
                }
            }

            // 名前・ワールドの履歴
            if (player.History.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted, "以前の名前 / ワールド:");
                foreach (var record in player.History.AsEnumerable().Reverse().Take(10))
                    ImGui.BulletText(
                        $"{record.Name} @ {ResolveWorld(record.WorldId)}（{record.UntilLocal:yyyy/M/d} まで）");
            }

            // この人物のリテイナー
            var retainers = Plugin.Retainers.Snapshot()
                .Where(p => p.OwnerContentId == player.ContentId ||
                            string.Equals(p.OwnerName, player.Name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.GuessedOwnerName, player.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (retainers.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted, "この人物のリテイナー:");
                foreach (var retainer in retainers.Take(10))
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"{retainer.RetainerName}##pret_{retainer.RetainerId}"))
                    {
                        _selectedRetainerId = retainer.RetainerId;
                        _requestedTab = Tab.Retainers;
                    }
                }
            }
        }

        private static string ResolveWorld(ushort worldId)
        {
            if (worldId == 0) return "不明";

            try
            {
                var world = Plugin.DataManager
                    .GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault(worldId);
                var name = world?.Name.ExtractText();
                return string.IsNullOrEmpty(name) ? $"#{worldId}" : name;
            }
            catch
            {
                return $"#{worldId}";
            }
        }
    }
}
