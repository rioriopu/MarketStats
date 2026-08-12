using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private string _retainerSearch = string.Empty;
        private bool _retainerIdentifiedOnly;
        private ulong _selectedRetainerId;
        private ulong _manualOwnerTarget;
        private string _manualOwnerName = string.Empty;

        /// <summary>
        /// マーケットで見かけたリテイナーの台帳。持ち主が判明／推定できたものを一覧する。
        /// </summary>
        private void DrawRetainersTab()
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "マーケットで見かけたリテイナーの一覧です。リテイナー名から持ち主を直接引く手段はゲームに無いため、" +
                "「そのリテイナーが商品を出す直前に、同じ商品を買っていた人は誰か」を突き合わせて持ち主を推定します。");

            ImGui.Spacing();

            ImGui.SetNextItemWidth(260);
            var search = _retainerSearch;
            if (ImGui.InputTextWithHint("##retainer_search",
                    "リテイナー名 / 持ち主 / 識別子で絞り込み", ref search, 64))
                _retainerSearch = search;
            AttachTooltip(
                "名前のほか、リテイナー ID・オーナー ID・製作者 ID でも絞り込めます\n" +
                "（0x 付きの 16 進、10 進のどちらでも）。");

            ImGui.SameLine();
            ImGui.Checkbox("判明・推定できたものだけ", ref _retainerIdentifiedOnly);

            ImGui.SameLine();
            if (ImGui.Button("いま推定し直す"))
            {
                var updated = OwnerResolver.Update(
                    Plugin.Listings, Plugin.Purchases, Plugin.Store,
                    Plugin.Retainers, Plugin.Identities, Plugin.Config.ResaleWindowHours);
                Plugin.Retainers.Save(force: true);
                Plugin.ChatGui.Print($"[Market Stats] リテイナー {updated} 件の持ち主を推定しました。");
            }
            AttachTooltip("蓄積済みの出品と購入履歴をもとに、その場で推定をやり直します。");

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted,
                $"リテイナー {Plugin.Retainers.Count:N0} 件 / 持ち主が分かったもの {Plugin.Retainers.IdentifiedCount:N0} 件 / " +
                $"購入履歴 {Plugin.Purchases.Count:N0} 件（{Plugin.Purchases.ItemCount} 種）");
            ImGui.Separator();

            var profiles = Plugin.Retainers.Snapshot();

            if (_retainerIdentifiedOnly)
                profiles = profiles.Where(p => p.HasOwner || !string.IsNullOrEmpty(p.GuessedOwnerName)).ToList();

            if (!string.IsNullOrWhiteSpace(_retainerSearch))
            {
                var q = _retainerSearch.Trim();

                // 識別子でも探せるようにする（16 進 / 10 進のどちらでも）。
                if (TryParseId(q, out var id))
                {
                    profiles = profiles.Where(p =>
                        p.RetainerId == id ||
                        p.OwnerContentId == id ||
                        p.ArtisanCounts.ContainsKey(id)).ToList();
                }
                else
                {
                    profiles = profiles.Where(p =>
                        p.RetainerName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (p.OwnerName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (p.GuessedOwnerName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                }
            }

            if (profiles.Count == 0)
            {
                ImGui.TextColored(ColorMuted,
                    "まだ記録がありません。マーケットボードでアイテムの出品一覧と購入履歴を開くと集まります。");
                return;
            }

            profiles = profiles
                .OrderByDescending(p => p.IsMine)
                .ThenByDescending(p => p.HasOwner)
                .ThenByDescending(p => p.GuessScore)
                .ThenByDescending(p => p.LastSeenUnix)
                .ToList();

            var listHeight = MathF.Max(140f, ImGui.GetContentRegionAvail().Y * 0.55f);
            if (ImGui.BeginChild("##retainer_list", new System.Numerics.Vector2(0, listHeight), true))
                DrawRetainerTable(profiles);
            ImGui.EndChild();

            ImGui.Spacing();
            DrawRetainerDetail();
        }

        private void DrawRetainerTable(List<RetainerProfile> profiles)
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##retainers", 7, flags)) return;

            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("リテイナー ID", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("持ち主", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("確度", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("出品", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("品目", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("最終確認", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var profile in profiles)
            {
                ImGui.TableNextRow();
                ImGui.PushID(profile.RetainerId.ToString());

                ImGui.TableNextColumn();
                var selected = _selectedRetainerId == profile.RetainerId;
                if (ImGui.Selectable($"##row", selected, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedRetainerId = profile.RetainerId;
                ImGui.SameLine(0, 0);
                ImGui.Text(string.IsNullOrEmpty(profile.RetainerName) ? "(名前不明)" : profile.RetainerName);

                ImGui.TableNextColumn();
                DrawCopyableId(profile.RetainerId, $"rid_{profile.RetainerId}");

                ImGui.TableNextColumn();
                if (profile.IsMine)
                    ImGui.TextColored(ColorFavorite, $"{profile.OwnerName}（自分）");
                else if (profile.HasOwner)
                {
                    ImGui.TextColored(ColorLink, profile.OwnerName!);
                    if (ImGui.IsItemClicked()) LodestoneOpen(profile.OwnerName!);
                    AttachTooltip("クリックで Lodestone のキャラクター検索を開きます。");
                }
                else if (!string.IsNullOrEmpty(profile.GuessedOwnerName))
                    ImGui.TextColored(ColorAccent, $"{profile.GuessedOwnerName}（推定）");
                else
                    ImGui.TextColored(ColorMuted, "不明");

                ImGui.TableNextColumn();
                if (profile.IsMine || profile.HasOwner)
                    ImGui.TextColored(ColorMuted, "確定");
                else if (profile.GuessScore > 0)
                {
                    var text = profile.GuessScore switch
                    {
                        >= 200 => "高",
                        >= 120 => "中",
                        _ => "低",
                    };
                    ImGui.TextColored(profile.GuessScore >= 200 ? ColorFavorite : ColorMuted, text);
                    AttachTooltip($"スコア {profile.GuessScore}");
                }
                else
                    ImGui.TextColored(ColorMuted, "-");

                ImGui.TableNextColumn();
                ImGui.Text($"{profile.ObservedListings}");

                ImGui.TableNextColumn();
                ImGui.Text($"{profile.ObservedItems.Count}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, profile.LastSeenLocal.ToString("M/d HH:mm"));

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        /// <summary>
        /// 製作者署名からの手がかりを表示する。
        ///
        /// 出品者の識別子はサーバーから送られてこないが、製作者の識別子は届いている。
        /// しかも製作者の識別子はキャラクターの識別子と同じものなので、
        /// 冒険者名刺で名前まで辿れる。自作品を並べている出品者なら、これが持ち主に直結する。
        /// </summary>
        private void DrawArtisanSection(RetainerProfile profile)
        {
            if (profile.SignedListingCount == 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted,
                    "製作者署名のある出品を観測していません（署名なしの品や、素材類だけの場合があります）。");
                return;
            }

            var artisanId = profile.MainArtisanId;
            var ratio = profile.MainArtisanRatio;
            var identity = Plugin.Identities.Resolve(artisanId);

            ImGui.Spacing();
            ImGui.TextColored(ColorAccent, "製作者からの手がかり");

            var ratioText = $"署名のある出品 {profile.SignedListingCount} 件のうち {ratio * 100:F0}% が同じ製作者";
            ImGui.TextWrapped(ratioText);

            ImGui.Text("主な製作者:");
            ImGui.SameLine();

            if (identity != null && identity.Source != IdentitySource.Inferred)
            {
                ImGui.TextColored(ColorLink, identity.Name);
                if (ImGui.IsItemClicked()) LodestoneOpen(identity.Name);
                AttachTooltip("クリックで Lodestone のキャラクター検索を開きます。");
            }
            else
            {
                ImGui.TextColored(ColorMuted, $"不明 (0x{artisanId:X})");

                ImGui.SameLine();
                var busy = Plugin.CharaCard.IsBusy;
                if (busy) ImGui.BeginDisabled();
                if (ImGui.SmallButton($"名刺で製作者を調べる##artisan_{profile.RetainerId}"))
                    Plugin.CharaCard.Request(artisanId);
                if (busy) ImGui.EndDisabled();
                AttachTooltip(
                    "製作者の識別子はキャラクターの識別子と同じものなので、冒険者名刺で名前が分かります。\n" +
                    "自作品を並べている出品者なら、その製作者が持ち主である可能性が高くなります。");
            }

            if (ratio >= 0.8)
                ImGui.TextColored(ColorFavorite,
                    "→ 出品が特定の製作者に偏っています。自作品を売っている可能性が高いです。");
            else
                ImGui.TextColored(ColorMuted,
                    "→ 複数の製作者の品が混ざっています。仕入れて売っている可能性があります。");

            DrawLinkedRetainers(profile);
        }

        /// <summary>
        /// 同じ持ち主だと思われるリテイナーを表示する。
        /// 名前が分からなくても束ねておけば、1 つ判明した時点でまとめて適用できる。
        /// </summary>
        private void DrawLinkedRetainers(RetainerProfile profile)
        {
            var links = RetainerLinkAnalyzer.FindLinks(profile, Plugin.Listings, Plugin.Retainers);
            if (links.Count == 0) return;

            ImGui.Spacing();
            ImGui.TextColored(ColorAccent, $"同じ持ち主と思われるリテイナー（{links.Count} 件）");
            ImGui.TextWrapped(
                "製作者の一致・出品時刻の揃い方・ID の近さ・品揃えの重なりから判断しています。" +
                "どれか 1 つで持ち主が判明すれば、他にも当てはめられます。");

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##retainer_links", 4, flags)) return;

            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("確度", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("持ち主", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("根拠", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableHeadersRow();

            foreach (var link in links.Take(15))
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"{link.RetainerName}##link_{link.RetainerId}"))
                    _selectedRetainerId = link.RetainerId;

                ImGui.TableNextColumn();
                ImGui.TextColored(link.Score >= 120 ? ColorFavorite : ColorMuted, link.ConfidenceLabel);
                AttachTooltip($"スコア {link.Score}");

                ImGui.TableNextColumn();
                if (string.IsNullOrEmpty(link.KnownOwner))
                    ImGui.TextColored(ColorMuted, "不明");
                else
                    ImGui.TextColored(ColorFavorite, link.KnownOwner);

                ImGui.TableNextColumn();
                ImGui.TextWrapped(string.Join(" / ", link.Reasons));
            }

            ImGui.EndTable();

            // 判明している持ち主がいれば、その名前をこのリテイナーにも当てはめられる。
            var resolved = links.FirstOrDefault(l => !string.IsNullOrEmpty(l.KnownOwner));
            if (resolved == null || profile.HasOwner) return;

            ImGui.Spacing();
            if (ImGui.Button($"「{resolved.KnownOwner}」をこのリテイナーの持ち主として設定"))
            {
                Plugin.Retainers.SetOwnerManually(profile.RetainerId, resolved.KnownOwner!);
                Plugin.Retainers.Save(force: true);
            }
            AttachTooltip(
                $"関連するリテイナー「{resolved.RetainerName}」の持ち主が {resolved.KnownOwner} です。\n" +
                "同じ人物だと判断できるなら、ここで確定させられます。");
        }

        /// <summary>
        /// このリテイナーに紐づく識別子を並べる。
        /// コピーして外部で調べたり、そこから関連を辿ったりできるようにする。
        /// </summary>
        private void DrawIdentifiers(RetainerProfile profile)
        {
            ImGui.Spacing();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##identifiers", 3, flags)) return;

            ImGui.TableSetupColumn("識別子", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("値", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("辿る", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();

            // リテイナー ID
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("リテイナー ID");
            ImGui.TableNextColumn();
            DrawCopyableId(profile.RetainerId, $"det_rid_{profile.RetainerId}", showSearchButton: true);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"持ち主を探す##probe_rid_{profile.RetainerId}"))
                OpenProbeForRetainerId(profile.RetainerId);
            AttachTooltip("検証タブで、この ID から持ち主を辿れないか順に試します。");

            // オーナー ID
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("オーナー ID");
            ImGui.TableNextColumn();
            if (profile.OwnerContentId != 0)
                DrawCopyableId(profile.OwnerContentId, $"det_oid_{profile.RetainerId}", showSearchButton: true);
            else
            {
                ImGui.TextColored(ColorMuted, "届いていません");
                AttachTooltip("マーケットの出品データに出品者の識別子は含まれていません（サーバーが送っていません）。");
            }
            ImGui.TableNextColumn();
            if (profile.OwnerContentId != 0 && ImGui.SmallButton($"調べる##probe_oid_{profile.RetainerId}"))
                OpenProbeForContentId(profile.OwnerContentId);

            // 主な製作者 ID
            var artisanId = profile.MainArtisanId;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("主な製作者 ID");
            ImGui.TableNextColumn();
            if (artisanId != 0)
                DrawCopyableId(artisanId, $"det_aid_{profile.RetainerId}", showSearchButton: true);
            else
                ImGui.TextColored(ColorMuted, "-");
            ImGui.TableNextColumn();
            if (artisanId != 0)
            {
                if (ImGui.SmallButton($"調べる##probe_aid_{profile.RetainerId}"))
                    OpenProbeForContentId(artisanId);

                ImGui.SameLine();
                var busy = Plugin.CharaCard.IsBusy;
                if (busy) ImGui.BeginDisabled();
                if (ImGui.SmallButton($"名刺##card_aid_{profile.RetainerId}"))
                    Plugin.CharaCard.Request(artisanId);
                if (busy) ImGui.EndDisabled();
            }

            ImGui.EndTable();

            if (profile.ArtisanCounts.Count > 1)
            {
                ImGui.TextColored(ColorMuted,
                    $"ほかに {profile.ArtisanCounts.Count - 1} 人の製作者の品も扱っています: " +
                    string.Join(", ", profile.ArtisanCounts
                        .Where(kv => kv.Key != artisanId)
                        .OrderByDescending(kv => kv.Value)
                        .Take(4)
                        .Select(kv => $"0x{kv.Key:X}({kv.Value}件)")));
            }
        }

        /// <summary>検証タブを開いて、この識別子を調べる。</summary>
        private void OpenProbeForContentId(ulong id)
        {
            _contentIdInput = $"0x{id:X}";
            _contentIdReport = Game.ContentIdProbe.Investigate(id);
            _requestedTab = Tab.Probe;
        }

        /// <summary>検証タブを開いて、このリテイナー ID から持ち主を辿る。</summary>
        private void OpenProbeForRetainerId(ulong id)
        {
            _retainerIdInput = $"0x{id:X}";
            _ownerProbe = Game.RetainerOwnerProbe.TryResolve(id);
            _requestedTab = Tab.Probe;
        }

        /// <summary>
        /// 識別子を、コピーと再検索ができる形で描画する。
        /// 左クリックで 16 進をコピー、右クリックで 10 進をコピー。
        /// </summary>
        private void DrawCopyableId(ulong id, string key, bool showSearchButton = false)
        {
            if (id == 0)
            {
                ImGui.TextColored(ColorMuted, "-");
                return;
            }

            var hex = $"0x{id:X}";
            ImGui.TextColored(ColorFavorite, hex);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) ImGui.SetClipboardText(hex);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.SetClipboardText(id.ToString());

            AttachTooltip(
                $"16 進: {hex}\n10 進: {id:N0}\n\n" +
                "左クリックで 16 進をコピー / 右クリックで 10 進をコピー\n" +
                "（Universalis などは 10 進で扱います）");

            if (!showSearchButton) return;

            ImGui.SameLine();
            if (ImGui.SmallButton($"この ID で絞り込む##f_{key}"))
                _retainerSearch = hex;
            AttachTooltip("この識別子に関係するリテイナーだけを一覧に残します。");
        }

        /// <summary>識別子の文字列を数値に直す（16 進 / 10 進の両対応）。</summary>
        private static bool TryParseId(string text, out ulong id)
        {
            text = text.Trim();

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out id);

            // 10 進として解釈できる場合のみ（名前と紛れないよう、すべて数字のときに限る）
            if (text.Length >= 6 && text.All(char.IsAsciiDigit))
                return ulong.TryParse(text, out id);

            id = 0;
            return false;
        }

        private void DrawRetainerDetail()
        {
            if (_selectedRetainerId == 0)
            {
                ImGui.TextColored(ColorMuted, "リテイナーを選ぶと、推定の根拠と取り扱っている商品が表示されます。");
                return;
            }

            var profile = Plugin.Retainers.Resolve(_selectedRetainerId);
            if (profile == null)
            {
                ImGui.TextColored(ColorMuted, "選択したリテイナーの情報が見つかりませんでした。");
                return;
            }

            ImGui.TextColored(ColorAccent, profile.RetainerName);
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, $"— 持ち主: {profile.DisplayOwner}");

            if (profile.OwnerContentId != 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("名刺で確認"))
                    Plugin.CharaCard.Request(profile.OwnerContentId);
                AttachTooltip("この出品者の冒険者名刺を開いて、名前を確定させます。");
            }

            if (profile.Confidence > 0 && !profile.IsMine)
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorMuted, $"（確度 {profile.Confidence}）");
            }

            DrawIdentifiers(profile);

            // 集めた手がかりを種類ごとに並べる。
            if (profile.Evidence.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted, "集まった手がかり:");

                const ImGuiTableFlags evidenceFlags =
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

                if (ImGui.BeginTable("##evidence", 3, evidenceFlags))
                {
                    ImGui.TableSetupColumn("手法", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("指している人", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn("内容", ImGuiTableColumnFlags.WidthStretch, 2f);
                    ImGui.TableHeadersRow();

                    foreach (var e in profile.Evidence.OrderByDescending(e => e.Weight))
                    {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.TextColored(e.IsDecisive ? ColorFavorite : ColorMuted, e.KindLabel);
                        AttachTooltip(e.IsDecisive ? "これ 1 つで確定できる手がかりです。" : $"重み {e.Weight}");

                        ImGui.TableNextColumn();
                        ImGui.Text(e.OwnerName);

                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(e.Description);
                    }

                    ImGui.EndTable();
                }
            }
            else if (profile.GuessReasons.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted, profile.ManuallySet ? "設定:" : "推定の根拠:");
                foreach (var reason in profile.GuessReasons)
                    ImGui.BulletText(reason);
            }

            if (!string.IsNullOrEmpty(profile.InconclusiveReason))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorAccent, $"結論を出していません: {profile.InconclusiveReason}");
            }

            // 推定した名前が実在するかを Lodestone で裏取りする。
            var candidate = profile.OwnerName ?? profile.GuessedOwnerName;
            if (Plugin.Config.VerifyNamesOnLodestone && !string.IsNullOrEmpty(candidate) && !profile.IsMine)
            {
                ImGui.Spacing();
                var verification = Plugin.NameVerifier.GetCached(candidate);

                if (verification == null)
                {
                    if (ImGui.SmallButton($"Lodestone で実在を確認##verify_{profile.RetainerId}"))
                        _ = Plugin.NameVerifier.VerifyAsync(candidate);
                    AttachTooltip("この名前のキャラクターが自分のデータセンターに実在するかを調べます。");
                }
                else if (verification.Error != null)
                    ImGui.TextColored(ColorMuted, $"Lodestone 照合: 失敗（{verification.Error}）");
                else if (verification.Exists)
                    ImGui.TextColored(ColorFavorite,
                        $"Lodestone 照合: 実在を確認（{verification.HitCount} 件" +
                        (string.IsNullOrEmpty(verification.WorldName) ? "" : $" / {verification.WorldName}") + "）");
                else
                    ImGui.TextColored(ColorAccent,
                        "Lodestone 照合: このデータセンターに該当なし（推定が誤っている可能性）");
            }

            // 推定が外れている場合に、正しい持ち主を手で入れて上書きできるようにする。
            if (!profile.IsMine)
            {
                ImGui.Spacing();
                ImGui.SetNextItemWidth(220);
                if (_manualOwnerTarget != profile.RetainerId)
                {
                    _manualOwnerTarget = profile.RetainerId;
                    _manualOwnerName = profile.ManuallySet ? profile.OwnerName ?? string.Empty : string.Empty;
                }

                var manual = _manualOwnerName;
                if (ImGui.InputTextWithHint("##manual_owner", "持ち主を手動で入力", ref manual, 64))
                    _manualOwnerName = manual;

                ImGui.SameLine();
                if (ImGui.Button("設定"))
                {
                    Plugin.Retainers.SetOwnerManually(profile.RetainerId, _manualOwnerName);
                    Plugin.Retainers.Save(force: true);
                }
                AttachTooltip("分かっている持ち主を手で入力して確定させます。推定より優先されます。");

                if (profile.ManuallySet || !string.IsNullOrEmpty(profile.GuessedOwnerName))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("消去"))
                    {
                        Plugin.Retainers.SetOwnerManually(profile.RetainerId, string.Empty);
                        Plugin.Retainers.ClearGuess(profile.RetainerId, "手動で消去");
                        Plugin.Retainers.Save(force: true);
                        _manualOwnerName = string.Empty;
                    }
                    AttachTooltip("この推定・設定を取り消して「不明」に戻します。");
                }
            }

            DrawArtisanSection(profile);

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted, "取り扱っている商品:");

            var listings = Plugin.Listings.Snapshot()
                .Where(l => l.RetainerId == profile.RetainerId)
                .OrderByDescending(l => l.EffectiveListedUnix)
                .Take(50)
                .ToList();

            if (listings.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "記録がありません。");
                return;
            }

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##retainer_items", 4, flags)) return;

            ImGui.TableSetupColumn("アイテム", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("個数", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("出品時刻", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableHeadersRow();

            foreach (var listing in listings)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(Plugin.Items.GetName(listing.ItemId) + (listing.Hq ? " (HQ)" : string.Empty));

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Quantity:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.UnitPrice:N0}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted,
                    DateTimeOffset.FromUnixTimeSeconds(listing.EffectiveListedUnix).LocalDateTime.ToString("M/d HH:mm"));
            }

            ImGui.EndTable();
        }
    }
}
