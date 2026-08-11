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
            DrawOwnerLookupSection();
            ImGui.Spacing();
            DrawIdentityPairSection();
            ImGui.Spacing();
            DrawTapsSection();
            ImGui.Spacing();
            DrawSelfSection();
            ImGui.Spacing();
            DrawMemoryScanSection();
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

            // リテイナー ID 同士の間隔。連番に近ければ、ID の近さで同じ持ち主を推測できる。
            if (self.Retainers.Count >= 2)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorAccent, "リテイナー ID 同士の間隔");
                ImGui.TextUnformatted(SelfRetainerProbe.DescribeRetainerIdSpacing(self.Retainers));
            }

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

        private int _selectedCapture = -1;

        /// <summary>
        /// マーケット関連の各フック地点の状態と、そこで捕まえた生データを表示する。
        /// 構造体から読めない情報が、届いたデータの中には入っていないかを確かめる。
        /// </summary>
        private void DrawTapsSection()
        {
            if (!ImGui.CollapsingHeader("フックした地点###probe_taps", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            ImGui.TextWrapped(
                "マーケットのデータがゲーム内部に取り込まれる瞬間を、複数の地点で捕まえています。" +
                "生データの取り込みを有効にすると、届いた内容を解析して" +
                "識別子や名前が含まれていないかを自動で調べます。");

            ImGui.Spacing();

            var capture = Plugin.Config.EnablePacketCapture;
            if (ImGui.Checkbox("届いた生データを取り込んで解析する（診断用）", ref capture))
            {
                Plugin.Config.EnablePacketCapture = capture;
                Plugin.Config.Save();
            }
            AttachTooltip(
                "フック地点に届いたデータの先頭 512 バイトを取り込み、\n" +
                "自分の ContentId やリテイナー ID、名前らしき文字列が含まれるかを調べます。\n" +
                "調査が済んだら無効に戻してください。");

            ImGui.Spacing();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("##taps", 4, flags))
            {
                ImGui.TableSetupColumn("地点", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("回数", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("直近の結果", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableHeadersRow();

                foreach (var status in Plugin.Taps.Statuses)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(status.Name);
                    AttachTooltip(status.Purpose);

                    ImGui.TableNextColumn();
                    if (status.Active) ImGui.TextColored(ColorFavorite, "設置済み");
                    else ImGui.TextColored(ColorAccent, "未設置");
                    AttachTooltip(status.Detail);

                    ImGui.TableNextColumn();
                    ImGui.Text($"{status.HitCount}");
                    if (status.LastHitLocal != DateTime.MinValue)
                        AttachTooltip($"直近 {status.LastHitLocal:HH:mm:ss}");

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(string.IsNullOrEmpty(status.LastFinding) ? "-" : status.LastFinding);
                }

                ImGui.EndTable();
            }

            var captures = Plugin.Taps.Captures;
            if (captures.Count == 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted,
                    "取り込んだデータはまだありません。上を有効にしてから、" +
                    "マーケットボードでアイテムの購入履歴や出品一覧を開いてください。");
                return;
            }

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted, $"取り込んだデータ: {captures.Count} 件");

            for (var i = 0; i < captures.Count; i++)
            {
                var item = captures[i];
                var label = $"{item.Local:HH:mm:ss} {item.Source} — 発見 {item.Findings.Count} 件##cap{i}";

                if (!ImGui.CollapsingHeader(label)) continue;

                ImGui.Indent(12);

                if (item.Findings.Count == 0)
                    ImGui.TextColored(ColorMuted, "識別子・名前らしき値は見つかりませんでした。");
                else
                    foreach (var finding in item.Findings.Take(20))
                        ImGui.BulletText(finding);

                if (ImGui.SmallButton($"生データをコピー##cap_copy{i}"))
                    ImGui.SetClipboardText(item.ToHex());

                ImGui.SameLine();
                if (ImGui.SmallButton($"生データを表示##cap_show{i}"))
                    _selectedCapture = _selectedCapture == i ? -1 : i;

                if (_selectedCapture == i)
                {
                    if (ImGui.BeginChild($"##cap_view{i}", new System.Numerics.Vector2(0, 220), true))
                        ImGui.TextUnformatted(item.ToHex());
                    ImGui.EndChild();
                }

                ImGui.Unindent(12);
            }
        }

        private RetainerOwnerProbe.ProbeResult? _ownerProbe;
        private string _retainerIdInput = string.Empty;

        private List<IdentityPair> _identityPairs = new();
        private string _pairSummary = string.Empty;

        /// <summary>
        /// メモリ上から「識別子と名前が並んで置かれている場所」を探し、対応表をまとめて回収する。
        /// </summary>
        private void DrawIdentityPairSection()
        {
            if (!ImGui.CollapsingHeader("識別子と名前の組を探す###probe_pairs")) return;

            ImGui.TextWrapped(
                "ゲームが人物の情報を持つとき、識別子と名前は同じレコードに並んで置かれていることが多いです。" +
                "メモリを走査して両者が近接している箇所を探し、対応表としてまとめて取り込みます。");

            ImGui.Spacing();

            if (ImGui.Button("走査する"))
            {
                var regions = IdentityPairScanner.EnumerateRegions();
                _identityPairs = IdentityPairScanner.Scan(regions);

                var fresh = _identityPairs.Count(p => !p.AlreadyKnown);
                _pairSummary =
                    $"{regions.Count} 箇所を走査し、{_identityPairs.Count} 組を発見しました" +
                    $"（うち未登録 {fresh} 組）。";
            }

            ImGui.SameLine();
            if (ImGui.Button("見つかった組を対応表に登録") && _identityPairs.Count > 0)
            {
                var added = 0;
                foreach (var pair in _identityPairs.Where(p => !p.AlreadyKnown))
                {
                    Plugin.Identities.Record(pair.ContentId, pair.Name, 0, Data.IdentitySource.ObjectTable);
                    added++;
                }

                Plugin.Identities.Save(force: true);
                _pairSummary = $"{added} 組を対応表に登録しました。";
                foreach (var pair in _identityPairs) pair.AlreadyKnown = true;
            }
            AttachTooltip(
                "走査で見つかった組を対応表へ入れます。\n" +
                "誤検出が混ざる可能性があるため、内容を確認してから実行してください。");

            if (!string.IsNullOrEmpty(_pairSummary))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(_pairSummary);
            }

            if (_identityPairs.Count == 0) return;

            ImGui.Spacing();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##pairs", 5, flags, new System.Numerics.Vector2(0, 240))) return;

            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("識別子", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("場所", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("距離", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("状態", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var pair in _identityPairs.OrderBy(p => p.Distance))
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(pair.Name);

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorFavorite, $"0x{pair.ContentId:X}");
                if (ImGui.IsItemClicked()) ImGui.SetClipboardText(pair.ContentId.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, pair.Region);
                AttachTooltip($"識別子 +0x{pair.IdOffset:X} / 名前 +0x{pair.NameOffset:X}");

                ImGui.TableNextColumn();
                ImGui.Text($"{pair.Distance}");

                ImGui.TableNextColumn();
                if (pair.AlreadyKnown) ImGui.TextColored(ColorMuted, "登録済み");
                else ImGui.TextColored(ColorAccent, "未登録");
            }

            ImGui.EndTable();
        }

        private string _scanInput = string.Empty;
        private List<ScanHit> _scanHits = new();
        private string _scanSummary = string.Empty;
        private int _dumpIndex;
        private string _dumpText = string.Empty;

        /// <summary>
        /// メモリを直接走査して、識別子が本当に届いていないのかを確かめる。
        /// 構造体の読み取り位置がずれているだけ、という可能性を潰すための機能。
        /// </summary>
        private void DrawMemoryScanSection()
        {
            if (!ImGui.CollapsingHeader("メモリを直接調べる###probe_scan")) return;

            ImGui.TextWrapped(
                "出品データのメモリを総当たりで走査します。自分の出品を検索した状態で" +
                "「自分の ContentId を探す」を押すと、識別子がメモリ上に実在するかが分かります。\n" +
                "見つかれば読み取り位置がずれているだけなので、そこから読めば特定できます。" +
                "見つからなければ、サーバーが送っていないと確定します。");

            ImGui.Spacing();

            if (ImGui.Button("自分の ContentId を探す"))
            {
                var self = SelfRetainerProbe.Read();
                if (self.ContentId == 0)
                    _scanSummary = "自分の ContentId を取得できませんでした。";
                else
                    RunScan(self.ContentId, $"自分の ContentId (0x{self.ContentId:X})");
            }

            ImGui.SameLine();
            if (ImGui.Button("自分のリテイナー ID を探す"))
            {
                var self = SelfRetainerProbe.Read();
                var retainer = self.Retainers.FirstOrDefault(r => r.SellingItems) ?? self.Retainers.FirstOrDefault();
                if (retainer == null)
                    _scanSummary = "リテイナーを取得できませんでした。";
                else
                    RunScan(retainer.RetainerId, $"リテイナー {retainer.Name} (0x{retainer.RetainerId:X})");
            }

            ImGui.Spacing();
            ImGui.SetNextItemWidth(240);
            var input = _scanInput;
            if (ImGui.InputTextWithHint("##scan_input", "任意の数値（10進 / 0x 付き16進）", ref input, 32))
                _scanInput = input;

            ImGui.SameLine();
            if (ImGui.Button("この値を探す"))
            {
                var text = _scanInput.Trim();
                ulong needle = 0;
                var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out needle)
                    : ulong.TryParse(text, out needle);

                if (ok && needle != 0) RunScan(needle, $"0x{needle:X}");
                else _scanSummary = "数値として解釈できませんでした。";
            }

            if (!string.IsNullOrEmpty(_scanSummary))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(_scanSummary);
            }

            if (_scanHits.Count > 0)
            {
                ImGui.Spacing();
                foreach (var hit in _scanHits.Take(30))
                    ImGui.BulletText(hit.ToString());

                if (_scanHits.Count > 30)
                    ImGui.TextColored(ColorMuted, $"…ほか {_scanHits.Count - 30} 件");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(ColorMuted, "出品 1 件分の生データを見る");

            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("出品の番号##dumpidx", ref _dumpIndex);

            ImGui.SameLine();
            if (ImGui.Button("生データを表示"))
            {
                _dumpText = MemoryScanner.DumpListingBytes(_dumpIndex);
                var fields = MemoryScanner.DescribeNonZeroFields(_dumpIndex);
                if (!string.IsNullOrEmpty(fields))
                    _dumpText += "\n0 でない 8 バイト値:\n" + fields;
                Plugin.PluginLog.Information($"出品 #{_dumpIndex} の生データ:\n{_dumpText}");
            }

            ImGui.SameLine();
            if (ImGui.Button("生データをコピー") && !string.IsNullOrEmpty(_dumpText))
                ImGui.SetClipboardText(_dumpText);

            if (!string.IsNullOrEmpty(_dumpText))
            {
                ImGui.Spacing();
                if (ImGui.BeginChild("##dump_view", new System.Numerics.Vector2(0, 200), true))
                    ImGui.TextUnformatted(_dumpText);
                ImGui.EndChild();
            }
        }

        private void RunScan(ulong needle, string label)
        {
            _scanHits = MemoryScanner.ScanForValue(needle);

            if (_scanHits.Count == 0)
            {
                _scanSummary =
                    $"{label} はマーケット関連のメモリに存在しませんでした。\n" +
                    "→ この値はサーバーから送られてきていません。";
                return;
            }

            var inListing = _scanHits.Where(h => h.ListingIndex >= 0).ToList();
            _scanSummary = $"{label} を {_scanHits.Count} 箇所で発見しました。";

            if (inListing.Count > 0)
            {
                var offsets = inListing.Select(h => h.OffsetInListing).Distinct().OrderBy(o => o);
                _scanSummary +=
                    $"\n→ うち {inListing.Count} 件は出品データの中です（オフセット " +
                    string.Join(", ", offsets.Select(o => $"+0x{o:X2}")) + "）。" +
                    "\n→ ここから読めば取得できます。オフセットを教えてください。";
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
                ImGui.SameLine();
                if (ImGui.SmallButton($"探す##own_{i}"))
                    _ownerProbe = RetainerOwnerProbe.TryResolve(l.RetainerId);
                AttachTooltip("このリテイナー ID から持ち主を辿れないか、考えられる方法を順に試します。");

                ImGui.TableNextColumn();
                DrawIdCell(l.OwnerContentId);

                ImGui.TableNextColumn();
                DrawIdCell(l.ArtisanContentId);
            }

            ImGui.EndTable();
        }

        /// <summary>
        /// リテイナー ID を起点に、持ち主へ辿り着けないかを試すセクション。
        /// 出品一覧の「探す」ボタンからもここに結果が出る。
        /// </summary>
        private void DrawOwnerLookupSection()
        {
            if (!ImGui.CollapsingHeader("リテイナー ID から持ち主を探す###probe_owner",
                    ImGuiTreeNodeFlags.DefaultOpen))
                return;

            ImGui.TextWrapped(
                "リテイナー ID を起点に、考えられる経路を順に試します。" +
                "下の「ゲーム内部の一覧」にある「探す」ボタンからも実行できます。");

            ImGui.Spacing();
            ImGui.SetNextItemWidth(280);
            var retainerInput = _retainerIdInput;
            if (ImGui.InputTextWithHint("##retainer_id_input",
                    "リテイナー ID（例 33776997236783377）", ref retainerInput, 32))
                _retainerIdInput = retainerInput;

            ImGui.SameLine();
            if (ImGui.Button("持ち主を探す"))
            {
                var text = _retainerIdInput.Trim();
                ulong id = 0;
                var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out id)
                    : ulong.TryParse(text, out id);

                _ownerProbe = ok && id != 0 ? RetainerOwnerProbe.TryResolve(id) : null;
                if (_ownerProbe == null)
                    ImGui.SetClipboardText(string.Empty);
            }
            AttachTooltip(
                "10 進でも 0x 付きの 16 進でも指定できます。\n" +
                "台帳 / 対応表 / ゲーム内のリスト / 識別子の変換 / 周辺メモリ / 製作者署名 の順に試します。");

            DrawOwnerProbeResult();
        }

        /// <summary>リテイナー ID から持ち主を辿る試行の結果。</summary>
        private void DrawOwnerProbeResult()
        {
            if (_ownerProbe == null) return;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(ColorAccent,
                $"リテイナー ID 0x{_ownerProbe.RetainerId:X} からの追跡結果" +
                (string.IsNullOrEmpty(_ownerProbe.RetainerName) ? string.Empty : $"（{_ownerProbe.RetainerName}）"));

            if (!string.IsNullOrEmpty(_ownerProbe.OwnerName))
                ImGui.TextColored(ColorFavorite, $"→ 持ち主: {_ownerProbe.OwnerName}");
            else
                ImGui.TextColored(ColorMuted, "→ どの方法でも持ち主に到達できませんでした。");

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("##owner_probe", 3, flags))
            {
                ImGui.TableSetupColumn("試した方法", ImGuiTableColumnFlags.WidthFixed, 170);
                ImGui.TableSetupColumn("結果", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("内容", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableHeadersRow();

                foreach (var attempt in _ownerProbe.Attempts)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(attempt.Method);

                    ImGui.TableNextColumn();
                    if (attempt.Success) ImGui.TextColored(ColorFavorite, "到達");
                    else ImGui.TextColored(ColorMuted, "不可");

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(attempt.Result);
                }

                ImGui.EndTable();
            }

            if (_ownerProbe.ContentIdCandidates.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted, "名刺で確認できる候補:");

                foreach (var candidate in _ownerProbe.ContentIdCandidates.Take(6))
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.Text($"0x{candidate:X}");
                    ImGui.SameLine();

                    var busy = Plugin.CharaCard.IsBusy;
                    if (busy) ImGui.BeginDisabled();
                    if (ImGui.SmallButton($"名刺##cand_{candidate:X}"))
                        Plugin.CharaCard.Request(candidate);
                    if (busy) ImGui.EndDisabled();
                }

                if (!string.IsNullOrEmpty(Plugin.CharaCard.LastResult))
                    ImGui.TextColored(ColorAccent, $"名刺照会: {Plugin.CharaCard.LastResult}");
            }

            ImGui.Spacing();
            if (ImGui.SmallButton("結果を閉じる")) _ownerProbe = null;
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
