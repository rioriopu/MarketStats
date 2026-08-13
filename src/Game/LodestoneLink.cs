using System.Linq;

namespace MarketStats.Game
{
    /// <summary>
    /// 購入者名から Lodestone のキャラクター検索ページを開く。
    ///
    /// 売却履歴にはキャラクター名しか残らず、所属ワールドは含まれない
    /// （クロスワールド購入もあるため）。そこで検索ページを
    /// 自分のデータセンターで絞り込んだ状態で開き、そこから選んでもらう形にしている。
    /// </summary>
    public static class LodestoneLink
    {
        public static readonly string[] Regions = { "jp", "na", "eu", "fr", "de" };

        /// <summary>ログイン中キャラクターのデータセンター名（英語表記）。取得できなければ空。</summary>
        public static string GetCurrentDataCenter()
        {
            try
            {
                if (!Plugin.PlayerState.IsLoaded) return string.Empty;
                var dc = Plugin.PlayerState.HomeWorld.ValueNullable?.DataCenter.ValueNullable;
                return dc?.Name.ExtractText() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>ログイン中キャラクターのワールド名。取得できなければ空。</summary>
        public static string GetCurrentWorld()
        {
            try
            {
                if (!Plugin.PlayerState.IsLoaded) return string.Empty;
                return Plugin.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 検索 URL を作る。
        ///
        /// ワールドが分かっていればそれで絞る。分からなければデータセンターで絞る。
        /// 名前は完全一致で指定しても部分一致で拾われるため、ワールドまで指定できるかが精度を分ける。
        /// </summary>
        public static string BuildSearchUrl(string characterName, string? worldName = null)
        {
            var region = Plugin.Config.LodestoneRegion;
            if (Array.IndexOf(Regions, region) < 0) region = "jp";

            var url = $"https://{region}.finalfantasyxiv.com/lodestone/character/?q=" +
                      Uri.EscapeDataString(characterName.Trim());

            if (!string.IsNullOrEmpty(worldName))
                return url + "&worldname=" + Uri.EscapeDataString(worldName);

            if (Plugin.Config.LodestoneFilterByDataCenter)
            {
                var dc = GetCurrentDataCenter();
                if (!string.IsNullOrEmpty(dc))
                    url += "&worldname=_dc_" + Uri.EscapeDataString(dc);
            }

            return url;
        }

        /// <summary>キャラクターページを直接開く URL。</summary>
        public static string BuildCharacterUrl(long lodestoneId)
        {
            var region = Plugin.Config.LodestoneRegion;
            if (Array.IndexOf(Regions, region) < 0) region = "jp";
            return $"https://{region}.finalfantasyxiv.com/lodestone/character/{lodestoneId}/";
        }

        /// <summary>
        /// 分かっている情報からできるだけ絞って Lodestone を開く。
        ///
        /// キャラクターページが特定できていればそこへ直行し、
        /// できていなければワールドで絞った検索を開く。
        /// </summary>
        public static void OpenSearch(string characterName, string? worldName = null)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return;

            // 対応表にページ ID が入っていれば、検索を挟まず直接開く。
            var identity = Plugin.Identities.ResolveByName(characterName);
            if (identity is { LodestoneId: > 0 })
            {
                OpenUrl(BuildCharacterUrl(identity.LodestoneId));
                return;
            }

            // 照合済みで本人を特定できていれば、そのページを開く。
            var known = Plugin.NameVerifier.GetCached(characterName);
            if (known is { LodestoneId: > 0 })
            {
                // 次回以降は照合なしで開けるよう覚えておく。
                if (identity != null)
                {
                    identity.LodestoneId = known.LodestoneId;
                    Plugin.Identities.Save();
                }

                OpenUrl(BuildCharacterUrl(known.LodestoneId));
                return;
            }

            OpenUrl(BuildSearchUrl(characterName, worldName ?? ResolveKnownWorld(characterName)));
        }

        /// <summary>その名前のキャラクターのワールドが分かっていれば返す。</summary>
        public static string? ResolveKnownWorld(string characterName)
        {
            try
            {
                // 対応表に登録があればワールドが分かる。
                var identity = Plugin.Identities.ResolveByName(characterName);
                if (identity is { WorldId: not 0 })
                {
                    var world = Plugin.DataManager
                        .GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault(identity.WorldId);
                    var name = world?.Name.ExtractText();
                    if (!string.IsNullOrEmpty(name)) return name;
                }

                // 購入履歴にはワールド名がそのまま入っていることがある。
                var purchase = Plugin.Purchases.ByBuyer(characterName).FirstOrDefault();
                if (purchase != null && !string.IsNullOrEmpty(purchase.WorldName)) return purchase.WorldName;
            }
            catch
            {
                // 分からなければ絞り込まない。
            }

            return null;
        }

        public static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"URL を開けませんでした: {e.Message}");
            }
        }
    }
}
