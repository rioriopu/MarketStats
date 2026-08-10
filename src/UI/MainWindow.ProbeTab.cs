using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Game;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private List<Data.ListingRecord> _probeGameListings = new();
        private List<PacketListing> _probePacketListings = new();
        private string _probeSummary = "「取得して調べる」を押してください。";

        /// <summary>
        /// 出品者を特定できない原因を切り分けるための検証タブ。
        /// ゲーム内部の一覧とパケット、それぞれから何が読めているかを並べて表示する。
        /// </summary>
        private void DrawProbeTab()
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "マーケットボードでアイテムの出品一覧を開いた状態で「取得して調べる」を押すと、" +
                "出品者を割り出すための値が実際に届いているかを確認できます。");

            ImGui.Spacing();

            if (ImGui.Button("取得して調べる"))
                RunProbe();

            ImGui.SameLine();
            if (ImGui.Button("ログにも出力"))
            {
                Plugin.MarketWatcher.DumpListings();
                RunProbe();
            }

            ImGui.SameLine();
            if (ImGui.Button("結果をコピー"))
                ImGui.SetClipboardText(BuildProbeReport());

            ImGui.Spacing();
            ImGui.TextWrapped(_probeSummary);
            ImGui.Separator();

            ImGui.TextColored(ColorAccent, "パケットの構造");
            ImGui.TextColored(ColorMuted, $"実装クラス: {PacketListingProbe.ImplementationTypeName}");
            ImGui.TextColored(ColorMuted,
                $"オーナーID の項目: {(PacketListingProbe.HasOwnerIdProperty ? "あり" : "なし")} / " +
                $"出品者名の項目: {(PacketListingProbe.HasPlayerNameProperty ? "あり" : "なし")}");

            ImGui.Spacing();
            DrawSelfSection();
            ImGui.Spacing();

            if (_probeGameListings.Count == 0 && _probePacketListings.Count == 0)
                return;

            if (ImGui.CollapsingHeader($"ゲーム内部の一覧 ({_probeGameListings.Count} 件)###probe_game",
                    ImGuiTreeNodeFlags.DefaultOpen))
                DrawProbeGameTable();

            if (ImGui.CollapsingHeader($"パケットから読めた値 ({_probePacketListings.Count} 件)###probe_packet",
                    ImGuiTreeNodeFlags.DefaultOpen))
                DrawProbePacketTable();
        }

        /// <summary>
        /// 自分のキャラクターとリテイナーの識別子を並べる。
        /// 答えが分かっている自分のデータを使って、マーケットのデータを検証するための材料。
        /// </summary>
        private void DrawSelfSection()
        {
            if (!ImGui.CollapsingHeader("自分の識別子で答え合わせ###probe_self",
                    ImGuiTreeNodeFlags.DefaultOpen))
                return;

            var self = SelfRetainerProbe.Read();

            if (!string.IsNullOrEmpty(self.Error))
            {
                ImGui.TextColored(ColorMuted, self.Error);
                return;
            }

            ImGui.Text($"自分: {self.CharacterName}");
            ImGui.SameLine();
            ImGui.TextColored(ColorFavorite, $"ContentId = 0x{self.ContentId:X}");
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(self.ContentId.ToString());
            AttachTooltip("クリックでコピーします。");

            if (self.Retainers.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "リテイナーが読み込まれていません。");
                return;
            }

            ImGui.Spacing();
            ImGui.TextWrapped(
                "自分のリテイナーが出品しているアイテムをマーケットで検索し、その行のオーナーIDを見れば、" +
                "「サーバーがそもそもオーナーIDを送っていないのか」がはっきりします。");
            ImGui.Spacing();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##probe_self_table", 4, flags)) return;

            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("RetainerId", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("一覧に居るか", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("ContentId との関係", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableHeadersRow();

            foreach (var retainer in self.Retainers)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(retainer.Name);
                if (retainer.SellingItems) AttachTooltip("マーケットに出品中です。");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorFavorite, $"0x{retainer.RetainerId:X}");
                if (ImGui.IsItemClicked()) ImGui.SetClipboardText(retainer.RetainerId.ToString());

                ImGui.TableNextColumn();
                var match = _probeGameListings.FirstOrDefault(l => l.RetainerId == retainer.RetainerId);
                if (match == null)
                    ImGui.TextColored(ColorMuted, "いません");
                else if (match.OwnerContentId == self.ContentId)
                    ImGui.TextColored(ColorFavorite, "オーナーID一致");
                else if (match.OwnerContentId != 0)
                    ImGui.TextColored(ColorAccent, $"別のID 0x{match.OwnerContentId:X}");
                else
                    ImGui.TextColored(ColorMuted, "オーナーID = 0");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted,
                    SelfRetainerProbe.DescribeRelation(self.ContentId, retainer.RetainerId));
            }

            ImGui.EndTable();

            ImGui.Spacing();
            var found = self.Retainers.Any(r => _probeGameListings.Any(l => l.RetainerId == r.RetainerId));
            if (!found)
            {
                ImGui.TextColored(ColorMuted,
                    "※ 自分のリテイナーが出品しているアイテムを検索すると、この表で答え合わせができます。");
            }
            else
            {
                var mine = self.Retainers
                    .Select(r => _probeGameListings.FirstOrDefault(l => l.RetainerId == r.RetainerId))
                    .Where(l => l != null)
                    .ToList();

                if (mine.All(l => l!.OwnerContentId == 0))
                    ImGui.TextColored(ColorAccent,
                        "→ 自分の出品でもオーナーIDが 0 です。サーバーが誰の分も送っていないため、" +
                        "出品者を識別子から特定することはできません。");
                else
                    ImGui.TextColored(ColorFavorite,
                        "→ 自分の出品にはオーナーIDが入っています。他人の分も取れる可能性があります。");
            }
        }

        private void RunProbe()
        {
            _probeGameListings = Plugin.MarketWatcher.CurrentListings();
            _probePacketListings = Plugin.MarketWatcher.LastPacketListings;

            var gameOwner = _probeGameListings.Count(l => l.OwnerContentId != 0);
            var gameRetainer = _probeGameListings.Count(l => l.RetainerId != 0);
            var gameName = _probeGameListings.Count(l => !string.IsNullOrEmpty(l.RetainerName));
            var gameArtisan = _probeGameListings.Count(l => l.ArtisanContentId != 0);

            var packetOwner = _probePacketListings.Count(l => l.RetainerOwnerId != 0);
            var packetPlayer = _probePacketListings.Count(l => !string.IsNullOrEmpty(l.PlayerName));

            if (_probeGameListings.Count == 0 && _probePacketListings.Count == 0)
            {
                _probeSummary = "出品を取得できませんでした。マーケットボードでアイテムを開いてからお試しください。";
                return;
            }

            var lines = new List<string>
            {
                $"ゲーム内部: {_probeGameListings.Count} 件中 — オーナーID {gameOwner} / リテイナーID {gameRetainer} / リテイナー名 {gameName} / 製作者ID {gameArtisan}",
                $"パケット: {_probePacketListings.Count} 件中 — オーナーID {packetOwner} / 出品者名 {packetPlayer}",
            };

            if (gameOwner == 0 && packetOwner == 0)
                lines.Add("→ どちらの経路でもオーナーIDが届いていません。この場合、出品者のキャラクターを特定する手段はありません。");
            else if (gameOwner == 0 && packetOwner > 0)
                lines.Add("→ パケット側にはオーナーIDがあります。こちらを使って特定できます。");
            else
                lines.Add("→ オーナーIDが取得できています。出品者の特定に使えます。");

            if (packetPlayer > 0)
                lines.Add("→ 出品者名がそのまま入っています。照会なしで名前が分かります。");

            _probeSummary = string.Join("\n", lines);
        }

        private void DrawProbeGameTable()
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##probe_game_table", 6, flags, new System.Numerics.Vector2(0, 200)))
                return;

            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("リテイナー名", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("リテイナーID", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("オーナーID", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("製作者ID", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (var i = 0; i < _probeGameListings.Count; i++)
            {
                var l = _probeGameListings[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, i.ToString());

                ImGui.TableNextColumn();
                ImGui.Text($"{l.UnitPrice:N0}");

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(l.RetainerName) ? "(空)" : l.RetainerName);

                ImGui.TableNextColumn();
                DrawIdCell(l.RetainerId);

                ImGui.TableNextColumn();
                DrawIdCell(l.OwnerContentId);

                ImGui.TableNextColumn();
                DrawIdCell(l.ArtisanContentId);
            }

            ImGui.EndTable();
        }

        private void DrawProbePacketTable()
        {
            if (_probePacketListings.Count == 0)
            {
                ImGui.TextColored(ColorMuted,
                    "パケットからの読み取り結果がありません。マーケットボードでアイテムを開き直すと取得されます。");
                return;
            }

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##probe_packet_table", 6, flags, new System.Numerics.Vector2(0, 200)))
                return;

            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("リテイナー名", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("オーナーID", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("出品者名", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("製作者ID", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (var i = 0; i < _probePacketListings.Count; i++)
            {
                var l = _probePacketListings[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, i.ToString());

                ImGui.TableNextColumn();
                ImGui.Text($"{l.PricePerUnit:N0}");

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(l.RetainerName) ? "(空)" : l.RetainerName);

                ImGui.TableNextColumn();
                DrawIdCell(l.RetainerOwnerId);

                ImGui.TableNextColumn();
                if (string.IsNullOrEmpty(l.PlayerName))
                    ImGui.TextColored(ColorMuted, "(空)");
                else
                    ImGui.TextColored(ColorFavorite, l.PlayerName);

                ImGui.TableNextColumn();
                DrawIdCell(l.ArtisanId);
            }

            ImGui.EndTable();
        }

        private static void DrawIdCell(ulong id)
        {
            if (id == 0)
            {
                ImGui.TextColored(ColorMuted, "0");
                return;
            }

            ImGui.TextColored(ColorFavorite, $"0x{id:X}");
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(id.ToString());
            AttachTooltip("クリックでコピーします。");
        }

        private string BuildProbeReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== Market Stats 出品データ検証 ==");
            sb.AppendLine(_probeSummary);
            sb.AppendLine($"実装クラス: {PacketListingProbe.ImplementationTypeName}");
            sb.AppendLine();

            sb.AppendLine("[ゲーム内部の一覧]");
            foreach (var l in _probeGameListings)
                sb.AppendLine(
                    $"price={l.UnitPrice} qty={l.Quantity} retainer='{l.RetainerName}' " +
                    $"retainerId=0x{l.RetainerId:X} ownerId=0x{l.OwnerContentId:X} artisan=0x{l.ArtisanContentId:X}");

            sb.AppendLine();
            sb.AppendLine("[パケット]");
            foreach (var l in _probePacketListings)
                sb.AppendLine(
                    $"price={l.PricePerUnit} qty={l.Quantity} retainer='{l.RetainerName}' " +
                    $"ownerId=0x{l.RetainerOwnerId:X} player='{l.PlayerName}' artisan=0x{l.ArtisanId:X}");

            return sb.ToString();
        }
    }
}
