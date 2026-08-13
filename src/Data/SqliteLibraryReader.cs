using System.Linq;
using Microsoft.Data.Sqlite;

namespace MarketStats.Data
{
    /// <summary>
    /// SQLite ライブラリを使った読み取り。
    ///
    /// 自前パーサ（<see cref="SqliteReader"/>）と同じことを、正規のライブラリで行う。
    /// 両者の結果を突き合わせれば、自前パーサが正しく読めているかを確かめられる。
    ///
    /// このファイルと、csproj のパッケージ参照を消せば、依存ごと取り除ける。
    /// </summary>
    public static class SqliteLibraryReader
    {
        private static bool _initialized;

        /// <summary>ライブラリが使える状態か。</summary>
        public static bool IsAvailable { get; private set; }

        public static string Status { get; private set; } = "未確認";

        /// <summary>ライブラリを初期化する。使えなければ理由を残す。</summary>
        public static bool Initialize()
        {
            if (_initialized) return IsAvailable;
            _initialized = true;

            try
            {
                SQLitePCL.Batteries_V2.Init();
                IsAvailable = true;
                Status = "利用できます";
            }
            catch (Exception e)
            {
                IsAvailable = false;
                Status = $"利用できません: {e.Message}";
                Plugin.PluginLog.Warning($"SQLite ライブラリを初期化できませんでした: {e.Message}");
            }

            return IsAvailable;
        }

        /// <summary>テーブルを丸ごと読む。</summary>
        public static List<SqliteReader.Row> ReadTable(string path, string tableName, int maxRows = 100_000)
        {
            var rows = new List<SqliteReader.Row>();
            if (!Initialize()) return rows;

            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                };

                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();

                using var command = connection.CreateCommand();

                // テーブル名は自分で用意した候補からしか来ないが、念のため素性を確かめる。
                if (!tableName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    Plugin.PluginLog.Warning($"扱えないテーブル名です: {tableName}");
                    return rows;
                }

                command.CommandText = $"SELECT * FROM {tableName} LIMIT {maxRows}";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new SqliteReader.Row();

                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        row.Set(name, reader.IsDBNull(i) ? null : reader.GetValue(i));
                    }

                    rows.Add(row);
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"ライブラリでの読み取りに失敗しました: {e.Message}");
            }

            return rows;
        }

        /// <summary>
        /// 自前パーサとライブラリで同じテーブルを読み、結果が一致するかを確かめる。
        /// </summary>
        public static string Compare(string path, string tableName, string idColumn, string nameColumn)
        {
            if (!Initialize()) return Status;

            try
            {
                var mine = SqliteReader.ReadTable(path, tableName)
                    .Select(r => (Id: r.GetId(idColumn), Name: r.GetText(nameColumn)))
                    .OrderBy(x => x.Id)
                    .ToList();

                var library = ReadTable(path, tableName)
                    .Select(r => (Id: r.GetId(idColumn), Name: r.GetText(nameColumn)))
                    .OrderBy(x => x.Id)
                    .ToList();

                if (mine.Count != library.Count)
                    return $"件数が違います（自前 {mine.Count} 件 / ライブラリ {library.Count} 件）。";

                for (var i = 0; i < mine.Count; i++)
                {
                    if (mine[i].Id == library[i].Id && mine[i].Name == library[i].Name) continue;

                    return $"{i + 1} 件目で違いがあります。\n" +
                           $"　自前:       0x{mine[i].Id:X} / {mine[i].Name}\n" +
                           $"　ライブラリ: 0x{library[i].Id:X} / {library[i].Name}";
                }

                return $"完全に一致しました（{mine.Count} 件）。自前パーサは正しく読めています。";
            }
            catch (Exception e)
            {
                return $"比較に失敗しました: {e.Message}";
            }
        }
    }
}
