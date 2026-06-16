using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Infrastructure.Persistence;

/// <summary>
/// Brings an existing SQLite database up to the current EF model after
/// <see cref="Microsoft.EntityFrameworkCore.Storage.IDatabaseCreator"/>'s
/// <c>EnsureCreated</c>, which only builds the schema for a brand-new file and
/// never updates one created by an earlier version.
///
/// The reconciler is additive and idempotent: it reads the expected tables,
/// columns, and indexes from EF metadata, compares them against the live
/// schema, and issues <c>CREATE TABLE</c>, <c>ALTER TABLE ADD COLUMN</c>, and
/// <c>CREATE INDEX</c> for whatever is missing. It never drops or alters
/// existing objects, so it is safe to run on every startup regardless of which
/// version originally created the database. Destructive changes (renames, drops,
/// type changes) are out of scope and would need a real migration.
/// </summary>
internal static class SchemaReconciler
{
    public static async Task ReconcileAsync(
        AgentRecallDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var existingTables = await QueryNamesAsync(
                connection, "SELECT name FROM sqlite_master WHERE type = 'table';", cancellationToken)
                .ConfigureAwait(false);
            var existingIndexes = await QueryNamesAsync(
                connection, "SELECT name FROM sqlite_master WHERE type = 'index';", cancellationToken)
                .ConfigureAwait(false);

            foreach (var entityType in context.Model.GetEntityTypes())
            {
                var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
                if (store is null)
                {
                    continue;
                }

                var table = store.Value.Name;

                if (!existingTables.Contains(table))
                {
                    await ExecuteAsync(connection, BuildCreateTable(entityType, store.Value), cancellationToken)
                        .ConfigureAwait(false);
                    logger.LogInformation("Schema reconcile: created missing table {Table}", table);
                }
                else
                {
                    await AddMissingColumnsAsync(connection, entityType, store.Value, table, logger, cancellationToken)
                        .ConfigureAwait(false);
                }

                await AddMissingIndexesAsync(
                    connection, entityType, store.Value, table, existingIndexes, logger, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task AddMissingColumnsAsync(
        IDbConnection connection,
        IEntityType entityType,
        StoreObjectIdentifier store,
        string table,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var existingColumns = await QueryColumnNamesAsync(connection, table, cancellationToken).ConfigureAwait(false);

        foreach (var property in entityType.GetProperties())
        {
            var column = property.GetColumnName(store);
            if (column is null || existingColumns.Contains(column))
            {
                continue;
            }

            var definition = BuildColumnDefinition(property, store, forCreateTable: false);
            await ExecuteAsync(connection, $"ALTER TABLE \"{table}\" ADD COLUMN {definition};", cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation("Schema reconcile: added column {Table}.{Column}", table, column);
        }
    }

    private static async Task AddMissingIndexesAsync(
        IDbConnection connection,
        IEntityType entityType,
        StoreObjectIdentifier store,
        string table,
        HashSet<string> existingIndexes,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var index in entityType.GetIndexes())
        {
            var name = index.GetDatabaseName(store);
            if (name is null || existingIndexes.Contains(name))
            {
                continue;
            }

            var columns = index.Properties
                .Select(p => $"\"{p.GetColumnName(store)}\"")
                .ToArray();
            if (columns.Length == 0)
            {
                continue;
            }

            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            await ExecuteAsync(
                connection,
                $"CREATE {unique}INDEX \"{name}\" ON \"{table}\" ({string.Join(", ", columns)});",
                cancellationToken).ConfigureAwait(false);
            existingIndexes.Add(name);
            logger.LogInformation("Schema reconcile: created index {Index} on {Table}", name, table);
        }
    }

    private static string BuildCreateTable(IEntityType entityType, StoreObjectIdentifier store)
    {
        var key = entityType.FindPrimaryKey();
        var keyColumns = key?.Properties
            .Select(p => p.GetColumnName(store))
            .Where(c => c is not null)
            .ToList() ?? [];

        // A single integer key maps to SQLite's rowid-backed AUTOINCREMENT, which
        // must be declared inline on the column rather than as a table constraint.
        var inlineAutoIncrementKey = keyColumns.Count == 1 ? keyColumns[0] : null;

        var lines = new List<string>();
        foreach (var property in entityType.GetProperties())
        {
            var column = property.GetColumnName(store);
            if (column is null)
            {
                continue;
            }

            var isInlineKey = column == inlineAutoIncrementKey && IsAutoIncrement(property);
            lines.Add(BuildColumnDefinition(property, store, forCreateTable: true, asInlineKey: isInlineKey, tableName: entityType.GetTableName()));
        }

        if (inlineAutoIncrementKey is null && keyColumns.Count > 0)
        {
            var cols = string.Join(", ", keyColumns.Select(c => $"\"{c}\""));
            lines.Add($"PRIMARY KEY ({cols})");
        }

        var body = string.Join(",\n    ", lines);
        return $"CREATE TABLE \"{store.Name}\" (\n    {body}\n);";
    }

    private static string BuildColumnDefinition(
        IProperty property,
        StoreObjectIdentifier store,
        bool forCreateTable,
        bool asInlineKey = false,
        string? tableName = null)
    {
        var column = property.GetColumnName(store);
        var type = property.GetColumnType(store);
        var nullable = property.IsColumnNullable(store);

        var sb = new StringBuilder();
        sb.Append('"').Append(column).Append("\" ").Append(type);

        if (asInlineKey)
        {
            sb.Append(" NOT NULL CONSTRAINT \"PK_").Append(tableName).Append("\" PRIMARY KEY AUTOINCREMENT");
            return sb.ToString();
        }

        if (!nullable)
        {
            sb.Append(" NOT NULL");
        }

        var defaultClause = BuildDefaultClause(property, store, type, nullable, forCreateTable);
        if (defaultClause is not null)
        {
            sb.Append(" DEFAULT ").Append(defaultClause);
        }

        return sb.ToString();
    }

    private static string? BuildDefaultClause(
        IProperty property,
        StoreObjectIdentifier store,
        string columnType,
        bool nullable,
        bool forCreateTable)
    {
        var defaultSql = property.GetDefaultValueSql(store);
        if (!string.IsNullOrWhiteSpace(defaultSql))
        {
            return defaultSql;
        }

        var configuredDefault = property.GetDefaultValue(store);
        if (configuredDefault is not null)
        {
            return EncodeLiteral(configuredDefault);
        }

        // SQLite cannot add a NOT NULL column to a populated table without a
        // default. For CREATE TABLE on an empty table no default is required, so
        // only synthesise one when altering an existing table.
        if (!nullable && !forCreateTable)
        {
            return ZeroDefaultForType(columnType);
        }

        return null;
    }

    private static string EncodeLiteral(object value) => value switch
    {
        bool b => b ? "1" : "0",
        string s => Quote(s),
        DateTimeOffset dto => Quote(dto.ToString("O", CultureInfo.InvariantCulture)),
        DateTime dt => Quote(dt.ToString("O", CultureInfo.InvariantCulture)),
        Enum e => Quote(e.ToString()),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(value.ToString() ?? string.Empty),
    };

    private static string ZeroDefaultForType(string columnType)
    {
        var t = columnType.ToUpperInvariant();
        if (t.Contains("INT") || t.Contains("REAL") || t.Contains("NUMERIC") ||
            t.Contains("DOUBLE") || t.Contains("FLOAT") || t.Contains("DECIMAL"))
        {
            return "0";
        }

        if (t.Contains("BLOB"))
        {
            return "x''";
        }

        return "''";
    }

    private static bool IsAutoIncrement(IProperty property) =>
        property.ValueGenerated == ValueGenerated.OnAdd &&
        (property.ClrType == typeof(int) || property.ClrType == typeof(long));

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<HashSet<string>> QueryNamesAsync(
        IDbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await ExecuteReaderAsync(command, cancellationToken).ConfigureAwait(false);
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                names.Add(reader.GetString(0));
            }
        }

        return names;
    }

    private static async Task<HashSet<string>> QueryColumnNamesAsync(
        IDbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = await ExecuteReaderAsync(command, cancellationToken).ConfigureAwait(false);
        while (reader.Read())
        {
            // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async Task ExecuteAsync(IDbConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (command is System.Data.Common.DbCommand dbCommand)
        {
            await dbCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            command.ExecuteNonQuery();
        }
    }

    private static Task<System.Data.Common.DbDataReader> ExecuteReaderAsync(
        IDbCommand command,
        CancellationToken cancellationToken)
    {
        if (command is System.Data.Common.DbCommand dbCommand)
        {
            return dbCommand.ExecuteReaderAsync(cancellationToken);
        }

        return Task.FromResult<System.Data.Common.DbDataReader>((System.Data.Common.DbDataReader)command.ExecuteReader());
    }
}
