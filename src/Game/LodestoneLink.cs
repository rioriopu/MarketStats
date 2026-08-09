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

        public static string BuildSearchUrl(string characterName)
        {
            var region = Plugin.Config.LodestoneRegion;
            if (Array.IndexOf(Regions, region) < 0) region = "jp";

            var url = $"https://{region}.finalfantasyxiv.com/lodestone/character/?q=" +
                      Uri.EscapeDataString(characterName.Trim());

            if (Plugin.Config.LodestoneFilterByDataCenter)
            {
                var dc = GetCurrentDataCenter();
                if (!string.IsNullOrEmpty(dc))
                    url += "&worldname=_dc_" + Uri.EscapeDataString(dc);
            }

            return url;
        }

        public static void OpenSearch(string characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return;
            OpenUrl(BuildSearchUrl(characterName));
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
