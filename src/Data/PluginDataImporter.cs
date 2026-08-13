using System.IO;
using System.Linq;

namespace MarketStats.Data
{
    /// <summary>取り込み元として見つかった他プラグインのデータ。</summary>
    public sealed class ExternalSource
    {
        public string PluginName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;

        /// <summary>識別子・名前・ワールド・Lodestone の列名。</summary>
        public string ContentIdColumn { get; set; } = string.Empty;
        public string NameColumn { get; set; } = string.Empty;
        public string WorldColumn { get; set; } = string.Empty;
        public string LodestoneColumn { get; set; } = string.Empty;

        public long FileSize { get; set; }
        public DateTime UpdatedLocal { get; set; }
    }

    /// <summary>
    /// 他のプラグインが貯めた記録から、識別子と名前の対応を取り込む。
    ///
    /// 同じ「誰がどのキャラクターか」を集めているプラグインは他にもあるので、
    /// すでに手元にある記録を活かせば、一から集め直す必要がない。
    /// 読み取り専用で開くため、相手のデータを書き換えることはない。
    /// </summary>
    public static class PluginDataImporter
    {
        /// <summary>対応しているプラグインと、その中でのデータの置き場所。</summary>
        private static readonly ExternalSource[] KnownSources =
        {
            new()
            {
                PluginName = "PlayerTrack",
                Path = @"PlayerTrack\data.db",
                TableName = "players",
                ContentIdColumn = "content_id",
                NameColumn = "name",
                WorldColumn = "world_id",
                LodestoneColumn = "lodestone_id",
            },
            new()
            {
                PluginName = "PlayerScope",
                Path = @"PlayerScope\PlayerScope.db",
                TableName = "Players",
                ContentIdColumn = "LocalContentId",
                NameColumn = "Name",
                WorldColumn = "WorldId",
                LodestoneColumn = "LodestoneId",
            },
            new()
            {
                PluginName = "RetainerTrack",
                Path = @"RetainerTrack\data.db",
                TableName = "player_names",
                ContentIdColumn = "content_id",
                NameColumn = "name",
                WorldColumn = "world_id",
                LodestoneColumn = "",
            },
        };

        /// <summary>取り込めそうなデータが手元にあるかを探す。</summary>
        public static List<ExternalSource> Detect()
        {
            var found = new List<ExternalSource>();

            try
            {
                // 自分の設定フォルダの隣に、他プラグインの設定フォルダが並んでいる。
                var configRoot = Directory.GetParent(Plugin.PluginInterface.GetPluginConfigDirectory());
                if (configRoot == null) return found;

                foreach (var source in KnownSources)
                {
                    var path = System.IO.Path.Combine(configRoot.FullName, source.Path);
                    if (!File.Exists(path)) continue;

                    var info = new FileInfo(path);

                    found.Add(new ExternalSource
                    {
                        PluginName = source.PluginName,
                        Path = path,
                        TableName = source.TableName,
                        ContentIdColumn = source.ContentIdColumn,
                        NameColumn = source.NameColumn,
                        WorldColumn = source.WorldColumn,
                        LodestoneColumn = source.LodestoneColumn,
                        FileSize = info.Length,
                        UpdatedLocal = info.LastWriteTime,
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"他プラグインのデータを探せませんでした: {e.Message}");
            }

            return found;
        }

        /// <summary>見つけたデータから、識別子と名前の対応を取り込む。</summary>
        public static ImportResult Import(ExternalSource source)
        {
            var result = new ImportResult();

            try
            {
                var rows = SqliteReader.ReadTable(source.Path, source.TableName);

                if (rows.Count == 0)
                {
                    result.Problems.Add(
                        $"{source.PluginName} から行を読み取れませんでした" +
                        "（対応していない形式か、まだ記録が無い可能性があります）。");
                    return result;
                }

                foreach (var row in rows)
                {
                    result.Read++;

                    var contentId = row.GetId(source.ContentIdColumn);
                    var name = row.GetText(source.NameColumn);

                    if (contentId == 0 || string.IsNullOrWhiteSpace(name))
                    {
                        result.Skipped++;
                        continue;
                    }

                    ushort worldId = 0;
                    if (!string.IsNullOrEmpty(source.WorldColumn))
                    {
                        var rawWorld = row.GetLong(source.WorldColumn);
                        if (rawWorld is > 0 and <= ushort.MaxValue) worldId = (ushort)rawWorld;
                    }

                    Plugin.Identities.Record(contentId, name.Trim(), worldId, IdentitySource.ObjectTable);

                    // Lodestone のページ ID があれば、本人のページを直接開けるようになる。
                    if (!string.IsNullOrEmpty(source.LodestoneColumn))
                    {
                        var lodestoneId = row.GetLong(source.LodestoneColumn);
                        if (lodestoneId > 0)
                        {
                            var identity = Plugin.Identities.Resolve(contentId);
                            if (identity != null) identity.LodestoneId = lodestoneId;
                        }
                    }

                    result.Added++;
                }

                if (result.Added > 0) Plugin.Identities.Save(force: true);

                Plugin.PluginLog.Information(
                    $"{source.PluginName} から {result.Added} 件の対応を取り込みました。");
            }
            catch (Exception e)
            {
                result.Problems.Add($"取り込みに失敗しました: {e.Message}");
            }

            return result;
        }
    }
}
