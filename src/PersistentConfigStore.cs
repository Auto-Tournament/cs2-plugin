using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;

namespace AutoTournamentCS2;

/// <summary>
/// Schema and access for the server-scoped tables in the MatchZy Enhanced database.
///
/// Both <c>at_server_config</c> and <c>at_event_queue</c> hold per-server state, and
/// both used to be keyed without any notion of which server a row belongs to. When several
/// servers share one database (the normal multi-server setup) that means config values collide
/// and queued events get retried by the wrong server, with the wrong remote log URL and headers.
/// Both tables now carry a <c>server_scope</c> column holding the identity from
/// <see cref="ServerIdentity"/>.
///
/// Backwards compatibility rules, applied by every method here:
///   - Rows that existed before the migration are kept and assigned the legacy scope (the empty
///     string). They are not reassigned to whichever server happens to run the migration: at
///     migration time we are creating a table, not loading config, so no server identity is
///     resolved yet, and handing one arbitrary server's identity to shared rows would be exactly
///     the bug this fixes.
///   - Reads prefer the row for this server's scope and fall back to the legacy row. A
///     single-server install therefore keeps reading the values it already had, with no operator
///     action, until something writes and the row becomes scoped.
///   - Writes always write the scoped row and never touch the legacy row, so one server writing
///     can no longer change what another server loads.
/// </summary>
public static class PersistentConfigStore
{
    public const string ConfigTable = "at_server_config";
    public const string EventQueueTable = "at_event_queue";
    public const string ScopeColumn = "server_scope";

    /// <summary>Name the legacy config table is parked under while SQLite rebuilds it.</summary>
    private const string ConfigTableRebuildName = "at_server_config_pre_scope";

    /// <summary>
    /// Creates <c>at_server_config</c> when it is missing, or migrates an existing unscoped
    /// table in place. Safe to run on every startup: it is a no-op once the column is present.
    /// </summary>
    public static void EnsureConfigSchema(IDbConnection connection, bool isSqlite, Action<string>? log = null)
    {
        if (!TableExists(connection, isSqlite, ConfigTable))
        {
            connection.Execute(CreateConfigTableSql(isSqlite));
            log?.Invoke($"[EnsureConfigSchema] Created {ConfigTable} with per-server scoping.");
            return;
        }

        if (ColumnExists(connection, isSqlite, ConfigTable, ScopeColumn))
        {
            return;
        }

        log?.Invoke($"[EnsureConfigSchema] Migrating {ConfigTable} to per-server scoping (existing rows are kept as legacy rows).");

        if (isSqlite)
        {
            MigrateConfigTableSQLite(connection, log);
        }
        else
        {
            MigrateConfigTableSQL(connection, log);
        }

        log?.Invoke($"[EnsureConfigSchema] Migration of {ConfigTable} complete.");
    }

    /// <summary>
    /// Adds <c>server_scope</c> to <c>at_event_queue</c> when it is missing. Existing queued
    /// events keep the legacy scope; see <see cref="PendingEventsScopeClause"/> for how they are
    /// drained.
    /// </summary>
    public static void EnsureEventQueueSchema(IDbConnection connection, bool isSqlite, Action<string>? log = null)
    {
        if (!TableExists(connection, isSqlite, EventQueueTable)) return;
        if (ColumnExists(connection, isSqlite, EventQueueTable, ScopeColumn)) return;

        log?.Invoke($"[EnsureEventQueueSchema] Adding {ScopeColumn} to {EventQueueTable} (existing events are kept as legacy rows).");

        string columnType = isSqlite ? "TEXT" : "VARCHAR(190)";
        connection.Execute($"ALTER TABLE {EventQueueTable} ADD COLUMN {ScopeColumn} {columnType} NOT NULL DEFAULT ''");

        try
        {
            connection.Execute(isSqlite
                ? $"CREATE INDEX IF NOT EXISTS idx_event_queue_scope_status ON {EventQueueTable}({ScopeColumn}, status, next_retry)"
                : $"CREATE INDEX idx_event_queue_scope_status ON {EventQueueTable}({ScopeColumn}, status, next_retry)");
        }
        catch (Exception ex)
        {
            // The index is an optimisation; a duplicate name or an unsupported syntax must not
            // take the whole startup down.
            log?.Invoke($"[EnsureEventQueueSchema] Could not create scope index: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a config value, preferring this server's scoped row over the legacy row.
    /// Returns null when neither exists.
    /// </summary>
    public static string? LoadConfigValue(IDbConnection connection, string key, string scope)
    {
        return connection.QueryFirstOrDefault<string>(
            $@"SELECT config_value
               FROM {ConfigTable}
               WHERE config_key = @Key AND {ScopeColumn} IN (@Scope, @Legacy)
               ORDER BY CASE WHEN {ScopeColumn} = @Scope THEN 0 ELSE 1 END
               LIMIT 1",
            new { Key = key, Scope = scope, Legacy = ServerIdentity.LegacyScope });
    }

    /// <summary>
    /// Writes a config value for this server's scope. The legacy row is left untouched so other
    /// servers that have not written yet keep whatever they were loading before.
    /// </summary>
    public static void SaveConfigValue(IDbConnection connection, bool isSqlite, string key, string value, string scope)
    {
        if (isSqlite)
        {
            connection.Execute(
                $@"INSERT INTO {ConfigTable} ({ScopeColumn}, config_key, config_value, updated_at)
                   VALUES (@Scope, @Key, @Value, datetime('now'))
                   ON CONFLICT({ScopeColumn}, config_key) DO UPDATE SET
                       config_value = @Value,
                       updated_at = datetime('now')",
                new { Scope = scope, Key = key, Value = value });
        }
        else
        {
            connection.Execute(
                $@"INSERT INTO {ConfigTable} ({ScopeColumn}, config_key, config_value)
                   VALUES (@Scope, @Key, @Value)
                   ON DUPLICATE KEY UPDATE
                       config_value = @Value,
                       updated_at = CURRENT_TIMESTAMP",
                new { Scope = scope, Key = key, Value = value });
        }
    }

    /// <summary>
    /// Predicate restricting queued events to the ones this server may send.
    ///
    /// This server's own events, plus legacy events queued before the migration. Legacy events
    /// are a bounded, one-off set: including them keeps a single-server install draining its
    /// backlog across the upgrade, and for a multi-server install it is no worse than the
    /// behaviour they already had.
    /// </summary>
    public static string PendingEventsScopeClause =>
        $"{ScopeColumn} IN (@Scope, @LegacyScope)";

    public static string CreateConfigTableSql(bool isSqlite) => isSqlite
        ? $@"CREATE TABLE IF NOT EXISTS {ConfigTable} (
                {ScopeColumn} TEXT NOT NULL DEFAULT '',
                config_key TEXT NOT NULL,
                config_value TEXT NOT NULL,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY ({ScopeColumn}, config_key)
            )"
        : $@"CREATE TABLE IF NOT EXISTS {ConfigTable} (
                {ScopeColumn} VARCHAR(190) NOT NULL DEFAULT '',
                config_key VARCHAR(255) NOT NULL,
                config_value TEXT NOT NULL,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY ({ScopeColumn}, config_key)
            )";

    /// <summary>
    /// SQLite cannot change a primary key in place, so the table is rebuilt: park the old table,
    /// create the scoped one, copy every row in as a legacy row, drop the parked table.
    /// </summary>
    private static void MigrateConfigTableSQLite(IDbConnection connection, Action<string>? log)
    {
        bool hasUpdatedAt = ColumnExists(connection, isSqlite: true, ConfigTable, "updated_at");

        // A rebuild left half-done by an earlier crash would block the rename.
        connection.Execute($"DROP TABLE IF EXISTS {ConfigTableRebuildName}");

        using IDbTransaction transaction = connection.BeginTransaction();
        try
        {
            connection.Execute($"ALTER TABLE {ConfigTable} RENAME TO {ConfigTableRebuildName}", transaction: transaction);
            connection.Execute(CreateConfigTableSql(isSqlite: true), transaction: transaction);

            string copied = hasUpdatedAt
                ? $@"INSERT INTO {ConfigTable} ({ScopeColumn}, config_key, config_value, updated_at)
                     SELECT '', config_key, config_value, updated_at FROM {ConfigTableRebuildName}"
                : $@"INSERT INTO {ConfigTable} ({ScopeColumn}, config_key, config_value)
                     SELECT '', config_key, config_value FROM {ConfigTableRebuildName}";

            int rows = connection.Execute(copied, transaction: transaction);
            connection.Execute($"DROP TABLE {ConfigTableRebuildName}", transaction: transaction);
            transaction.Commit();
            log?.Invoke($"[EnsureConfigSchema] Preserved {rows} existing config row(s) as legacy rows.");
        }
        catch
        {
            try { transaction.Rollback(); } catch { /* the original table is still intact */ }
            throw;
        }
    }

    /// <summary>
    /// MySQL can add the column and move the primary key in a single statement, so existing rows
    /// keep their values and pick up the legacy scope from the column default.
    /// </summary>
    private static void MigrateConfigTableSQL(IDbConnection connection, Action<string>? log)
    {
        bool hasPrimaryKey = connection.ExecuteScalar<int>(
            $@"SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
               WHERE TABLE_SCHEMA = DATABASE()
                 AND TABLE_NAME = '{ConfigTable}'
                 AND CONSTRAINT_TYPE = 'PRIMARY KEY'") > 0;

        string dropPrimaryKey = hasPrimaryKey ? "DROP PRIMARY KEY, " : "";

        connection.Execute(
            $@"ALTER TABLE {ConfigTable}
                 ADD COLUMN {ScopeColumn} VARCHAR(190) NOT NULL DEFAULT '' FIRST,
                 {dropPrimaryKey}ADD PRIMARY KEY ({ScopeColumn}, config_key)");

        log?.Invoke($"[EnsureConfigSchema] Existing config rows kept and assigned the legacy scope.");
    }

    /// <summary>True when the named table exists in the current database.</summary>
    public static bool TableExists(IDbConnection connection, bool isSqlite, string table)
    {
        if (isSqlite)
        {
            return connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @Table",
                new { Table = table }) > 0;
        }

        return connection.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM information_schema.TABLES
              WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table",
            new { Table = table }) > 0;
    }

    /// <summary>True when the named table has the named column.</summary>
    public static bool ColumnExists(IDbConnection connection, bool isSqlite, string table, string column)
    {
        if (isSqlite)
        {
            // PRAGMA does not accept parameters, and the table names here are compile-time
            // constants rather than anything caller-supplied.
            IEnumerable<string> names = connection.Query($"PRAGMA table_info({table})")
                .Select(row => (string)((IDictionary<string, object>)row)["name"]);
            return names.Contains(column, StringComparer.OrdinalIgnoreCase);
        }

        return connection.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM information_schema.COLUMNS
              WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table AND COLUMN_NAME = @Column",
            new { Table = table, Column = column }) > 0;
    }
}
