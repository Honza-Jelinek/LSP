using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Data;

/// <summary>Prepise absolutni cesty po zmene pismene disku portable baliku.</summary>
public static class PortablePathRewriter
{
    public const string LastRootSetting = "portable.lastRoot";

    private static readonly (string Table, string Column)[] PathColumns =
    [
        ("MediaFiles", "Path"),
        ("LibraryFolders", "Path"),
        ("PlaybackProgress", "Path"),
        ("ParseCaches", "Path"),
        ("ManualMatches", "Key"),
        ("Movies", "PosterFile"),
        ("Shows", "PosterFile"),
        ("TmdbCaches", "PosterFile"),
        ("TmdbCaches", "BackdropFile"),
    ];

    public static void InitializePortableDatabase(LibraryDbContext db)
    {
        if (!AppPaths.IsPortable)
            return;

        db.Database.OpenConnection();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var currentRoot = NormalizeRoot(AppPaths.AppRoot);
        var lastRoot = GetSetting(connection, LastRootSetting);

        if (lastRoot is null)
        {
            SetSetting(connection, LastRootSetting, currentRoot);
            return;
        }

        if (string.Equals(NormalizeRoot(lastRoot), currentRoot, StringComparison.OrdinalIgnoreCase))
            return;

        using var transaction = connection.BeginTransaction();
        RewriteRoot(connection, lastRoot, currentRoot, transaction);
        SetSetting(connection, LastRootSetting, currentRoot, transaction);
        transaction.Commit();
    }

    public static void RewriteRoot(
        SqliteConnection connection,
        string oldRoot,
        string newRoot,
        SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        oldRoot = NormalizeRoot(oldRoot);
        newRoot = NormalizeRoot(newRoot);

        foreach (var (table, column) in PathColumns)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE "{table}"
                SET "{column}" = @newRoot || substr("{column}", length(@oldRoot) + 1)
                WHERE "{column}" LIKE @oldRoot || '%';
                """;
            command.Parameters.AddWithValue("@oldRoot", oldRoot);
            command.Parameters.AddWithValue("@newRoot", newRoot);
            command.ExecuteNonQuery();
        }
    }

    public static string? GetSetting(SqliteConnection connection, string key, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM Settings WHERE Key = @key LIMIT 1;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    public static void SetSetting(
        SqliteConnection connection,
        string key,
        string value,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
