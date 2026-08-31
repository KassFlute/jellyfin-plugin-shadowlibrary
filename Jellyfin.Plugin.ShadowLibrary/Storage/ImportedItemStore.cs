using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Storage;

/// <summary>
/// SQLite store of imported items, kept in the plugin data folder and independent from
/// the Jellyfin database.
/// </summary>
public class ImportedItemStore
{
    private const string DatabaseFileName = "shadowlibrary.db";

    // bump on any schema change, the store rebuilds itself rather than migrating
    private const int SchemaVersion = 3;

    private const string ItemColumns =
        "id, friend_server_id, remote_item_id, kind, local_item_id, folder_path, strm_path, "
        + "nfo_path, last_import_utc, unavailable_since_utc, metadata_hash, claim_keys";

    private const string SeriesColumns =
        "friend_server_id, remote_series_id, folder_path, metadata_hash, last_import_utc";

    private readonly ILogger<ImportedItemStore> _logger;
    private readonly Lock _initLock = new();
    private readonly string _connectionString;
    private bool _initialised;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportedItemStore"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public ImportedItemStore(ILogger<ImportedItemStore> logger)
    {
        _logger = logger;

        var dataFolder = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("The plugin is not initialised.");
        Directory.CreateDirectory(dataFolder);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataFolder, DatabaseFileName),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    /// <summary>
    /// Returns every item imported from a friend server.
    /// </summary>
    /// <param name="friendServerId">Friend server identifier.</param>
    /// <returns>The stored items.</returns>
    public IReadOnlyList<ImportedItem> GetByFriendServer(Guid friendServerId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + ItemColumns + " FROM imported_items WHERE friend_server_id = $friend";
        command.Parameters.AddWithValue("$friend", Key(friendServerId));

        var items = new List<ImportedItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    /// <summary>
    /// Returns a single item by its plugin side identifier.
    /// </summary>
    /// <param name="id">Plugin side identifier.</param>
    /// <returns>The item, or null when it is unknown.</returns>
    public ImportedItem? GetById(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + ItemColumns + " FROM imported_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", Key(id));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    /// <summary>
    /// Returns the Jellyfin identifiers of every imported item, used to tell native items apart.
    /// </summary>
    /// <returns>The known local identifiers.</returns>
    public HashSet<Guid> GetLocalItemIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_item_id FROM imported_items WHERE local_item_id IS NOT NULL";

        var ids = new HashSet<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Guid.TryParse(reader.GetString(0), out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Inserts an item, or replaces the row already held for the same remote item.
    /// </summary>
    /// <param name="item">Item to store.</param>
    public void Upsert(ImportedItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO imported_items
                (id, friend_server_id, remote_item_id, kind, local_item_id, folder_path, strm_path,
                 nfo_path, last_import_utc, unavailable_since_utc, metadata_hash, claim_keys)
            VALUES
                ($id, $friend, $remote, $kind, $local, $folder, $strm, $nfo, $last, $unavailable, $hash, $claims)
            ON CONFLICT(friend_server_id, remote_item_id) DO UPDATE SET
                kind = excluded.kind,
                local_item_id = excluded.local_item_id,
                folder_path = excluded.folder_path,
                strm_path = excluded.strm_path,
                nfo_path = excluded.nfo_path,
                last_import_utc = excluded.last_import_utc,
                unavailable_since_utc = excluded.unavailable_since_utc,
                metadata_hash = excluded.metadata_hash,
                claim_keys = excluded.claim_keys
            """;

        command.Parameters.AddWithValue("$id", Key(item.Id));
        command.Parameters.AddWithValue("$friend", Key(item.FriendServerId));
        command.Parameters.AddWithValue("$remote", item.RemoteItemId);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$local", item.LocalItemId is null ? DBNull.Value : Key(item.LocalItemId.Value));
        command.Parameters.AddWithValue("$folder", item.FolderPath);
        command.Parameters.AddWithValue("$strm", item.StrmPath);
        command.Parameters.AddWithValue("$nfo", item.NfoPath);
        command.Parameters.AddWithValue("$last", Stamp(item.LastImportUtc));
        command.Parameters.AddWithValue(
            "$unavailable",
            item.UnavailableSinceUtc is null ? DBNull.Value : Stamp(item.UnavailableSinceUtc.Value));
        command.Parameters.AddWithValue("$hash", item.MetadataHash);
        command.Parameters.AddWithValue("$claims", string.Join('|', item.ClaimKeys));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes an item.
    /// </summary>
    /// <param name="id">Plugin side identifier.</param>
    public void Delete(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM imported_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", Key(id));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the deduplication keys held by every imported item, with the friend server
    /// that holds them, so ownership survives a cycle where a friend server is unreachable.
    /// </summary>
    /// <returns>One entry per stored item that carries keys.</returns>
    public IReadOnlyList<(Guid FriendServerId, string[] Keys)> GetClaims()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT friend_server_id, claim_keys FROM imported_items WHERE claim_keys <> ''";

        var claims = new List<(Guid, string[])>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            claims.Add((
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetString(1).Split('|', StringSplitOptions.RemoveEmptyEntries)));
        }

        return claims;
    }

    /// <summary>
    /// Rewrites the stored paths of a friend server after its generated folder moved. The
    /// resolved Jellyfin identifiers go with them, since Jellyfin keys an item on its path
    /// and will create new ones at the new location.
    /// </summary>
    /// <param name="friendServerId">Friend server identifier.</param>
    /// <param name="oldPrefix">Folder the files were in.</param>
    /// <param name="newPrefix">Folder they are in now.</param>
    public void Repath(Guid friendServerId, string oldPrefix, string newPrefix)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE imported_items SET
                folder_path = $new || substr(folder_path, $length + 1),
                strm_path = $new || substr(strm_path, $length + 1),
                nfo_path = $new || substr(nfo_path, $length + 1),
                local_item_id = NULL
            WHERE friend_server_id = $friend AND substr(folder_path, 1, $length) = $old;

            UPDATE imported_series SET
                folder_path = $new || substr(folder_path, $length + 1)
            WHERE friend_server_id = $friend AND substr(folder_path, 1, $length) = $old;
            """;

        command.Parameters.AddWithValue("$friend", Key(friendServerId));
        command.Parameters.AddWithValue("$old", oldPrefix);
        command.Parameters.AddWithValue("$new", newPrefix);
        command.Parameters.AddWithValue("$length", oldPrefix.Length);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns every series folder generated for a friend server.
    /// </summary>
    /// <param name="friendServerId">Friend server identifier.</param>
    /// <returns>The stored series.</returns>
    public IReadOnlyList<ImportedSeries> GetSeriesByFriendServer(Guid friendServerId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + SeriesColumns + " FROM imported_series WHERE friend_server_id = $friend";
        command.Parameters.AddWithValue("$friend", Key(friendServerId));

        var series = new List<ImportedSeries>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            series.Add(ReadSeries(reader));
        }

        return series;
    }

    /// <summary>
    /// Inserts a series, or replaces the row already held for the same remote series.
    /// </summary>
    /// <param name="series">Series to store.</param>
    public void UpsertSeries(ImportedSeries series)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO imported_series
                (friend_server_id, remote_series_id, folder_path, metadata_hash, last_import_utc)
            VALUES
                ($friend, $remote, $folder, $hash, $last)
            ON CONFLICT(friend_server_id, remote_series_id) DO UPDATE SET
                folder_path = excluded.folder_path,
                metadata_hash = excluded.metadata_hash,
                last_import_utc = excluded.last_import_utc
            """;

        command.Parameters.AddWithValue("$friend", Key(series.FriendServerId));
        command.Parameters.AddWithValue("$remote", series.RemoteSeriesId);
        command.Parameters.AddWithValue("$folder", series.FolderPath);
        command.Parameters.AddWithValue("$hash", series.MetadataHash);
        command.Parameters.AddWithValue("$last", Stamp(series.LastImportUtc));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes a series row.
    /// </summary>
    /// <param name="friendServerId">Friend server identifier.</param>
    /// <param name="remoteSeriesId">Series identifier on the friend server.</param>
    public void DeleteSeries(Guid friendServerId, string remoteSeriesId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM imported_series WHERE friend_server_id = $friend AND remote_series_id = $remote";
        command.Parameters.AddWithValue("$friend", Key(friendServerId));
        command.Parameters.AddWithValue("$remote", remoteSeriesId);
        command.ExecuteNonQuery();
    }

    private static string Key(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private static string Stamp(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseStamp(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static ImportedItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        FriendServerId = Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        RemoteItemId = reader.GetString(2),
        Kind = (ImportedItemKind)reader.GetInt32(3),
        LocalItemId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        FolderPath = reader.GetString(5),
        StrmPath = reader.GetString(6),
        NfoPath = reader.GetString(7),
        LastImportUtc = ParseStamp(reader.GetString(8)),
        UnavailableSinceUtc = reader.IsDBNull(9) ? null : ParseStamp(reader.GetString(9)),
        MetadataHash = reader.GetString(10),
        ClaimKeys = reader.IsDBNull(11)
            ? []
            : reader.GetString(11).Split('|', StringSplitOptions.RemoveEmptyEntries)
    };

    private static ImportedSeries ReadSeries(SqliteDataReader reader) => new()
    {
        FriendServerId = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        RemoteSeriesId = reader.GetString(1),
        FolderPath = reader.GetString(2),
        MetadataHash = reader.GetString(3),
        LastImportUtc = ParseStamp(reader.GetString(4))
    };

    private SqliteConnection Open()
    {
        EnsureCreated();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "PRAGMA takes no parameter, and the value is a private constant of this class.")]
    private void EnsureCreated()
    {
        if (_initialised)
        {
            return;
        }

        lock (_initLock)
        {
            if (_initialised)
            {
                return;
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                // a sync writing must not make a playback request fail on a locked database
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            DropOutdatedSchema(connection);

            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS imported_items (
                    id TEXT PRIMARY KEY,
                    friend_server_id TEXT NOT NULL,
                    remote_item_id TEXT NOT NULL,
                    kind INTEGER NOT NULL,
                    local_item_id TEXT NULL,
                    folder_path TEXT NOT NULL,
                    strm_path TEXT NOT NULL,
                    nfo_path TEXT NOT NULL,
                    last_import_utc TEXT NOT NULL,
                    unavailable_since_utc TEXT NULL,
                    metadata_hash TEXT NOT NULL,
                    claim_keys TEXT NOT NULL DEFAULT ''
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ix_imported_items_remote
                    ON imported_items (friend_server_id, remote_item_id);

                CREATE TABLE IF NOT EXISTS imported_series (
                    friend_server_id TEXT NOT NULL,
                    remote_series_id TEXT NOT NULL,
                    folder_path TEXT NOT NULL,
                    metadata_hash TEXT NOT NULL,
                    last_import_utc TEXT NOT NULL,
                    PRIMARY KEY (friend_server_id, remote_series_id)
                );
                """;
            command.ExecuteNonQuery();

            using var stamp = connection.CreateCommand();
            stamp.CommandText = "PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture);
            stamp.ExecuteNonQuery();

            _logger.LogDebug("[ShadowLibrary] Imported item store ready.");
            _initialised = true;
        }
    }

    /// <summary>
    /// Drops the tables when they were written by a different schema. Rebuilding costs one
    /// import cycle, and the generated paths are deterministic so the files are simply
    /// rewritten in place rather than duplicated.
    /// </summary>
    private void DropOutdatedSchema(SqliteConnection connection)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(read.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (current == SchemaVersion || current == 0)
        {
            return;
        }

        _logger.LogWarning(
            "[ShadowLibrary] Item store schema is version {Current}, expected {Expected}. Rebuilding it, "
            + "every item will be imported again on the next cycle.",
            current,
            SchemaVersion);

        using var drop = connection.CreateCommand();
        drop.CommandText = """
            DROP TABLE IF EXISTS imported_items;
            DROP TABLE IF EXISTS imported_series;
            """;
        drop.ExecuteNonQuery();
    }
}
