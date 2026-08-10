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

            if (_probeGameListings.Count == 0 && _probePacketListings.Count == 0)
                return;

            if (ImGui.CollapsingHeader($"ゲーム内部の一覧 ({_probeGameListings.Count} 件)###probe_game",
                    ImGuiTreeNodeFlags.DefaultOpen))
                DrawProbeGameTable();

            if (ImGui.CollapsingHeader($"パケットから読めた値 ({_probePacketListings.Count} 件)###probe_packet",
                    ImGuiTreeNodeFlags.DefaultOpen))
                DrawProbePacketTable();
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
