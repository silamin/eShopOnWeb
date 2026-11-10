using Microsoft.Data.SqlClient;

// One-shot init step: for every service whose <PREFIX>_DB_PASSWORD is set, create its own
// database and its own SQL login, and make that login db_owner of that database only.
// Services then connect with that login, never with sa. Safe to run on every `up` (idempotent;
// the login password is re-synced from the environment each time).
//
//   SA_PASSWORD                 sa password of the shared SQL instance
//   CATALOG_ / INVENTORY_ / ORDERING_ DB_NAME / _USER / _PASSWORD      (later: PAYMENT_)

var sa = Environment.GetEnvironmentVariable("SA_PASSWORD") ?? throw new InvalidOperationException("SA_PASSWORD missing");
var host = Environment.GetEnvironmentVariable("SQL_HOST") ?? "sqlserver,1433";
var master = $"Server={host};Database=master;User Id=sa;Password={sa};TrustServerCertificate=true;Encrypt=false;Connect Timeout=5";

async Task<SqlConnection> OpenWhenReadyAsync()
{
    for (var attempt = 1; attempt <= 60; attempt++)
    {
        try
        {
            var c = new SqlConnection(master);
            await c.OpenAsync();
            return c;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SQL not ready (attempt {attempt}/60): {ex.Message.Split('\n')[0]}");
            await Task.Delay(2000);
        }
    }
    throw new InvalidOperationException("SQL Server did not become ready in 120 s.");
}

static async Task ExecAsync(SqlConnection c, string sql, params (string, string)[] p)
{
    await using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
    await cmd.ExecuteNonQueryAsync();
}

await using var conn = await OpenWhenReadyAsync();
Console.WriteLine("SQL ready.");

foreach (var prefix in new[] { "CATALOG", "INVENTORY", "ORDERING", "PAYMENT" })
{
    var pw = Environment.GetEnvironmentVariable($"{prefix}_DB_PASSWORD");
    if (string.IsNullOrEmpty(pw)) continue;
    var db = Environment.GetEnvironmentVariable($"{prefix}_DB_NAME") ?? throw new InvalidOperationException($"{prefix}_DB_NAME missing");
    var user = Environment.GetEnvironmentVariable($"{prefix}_DB_USER") ?? throw new InvalidOperationException($"{prefix}_DB_USER missing");

    // Identifiers go through QUOTENAME; the password is escaped as a string literal (CREATE LOGIN cannot take a parameter).
    await ExecAsync(conn, @"
        DECLARE @sql nvarchar(max);
        IF DB_ID(@db) IS NULL
        BEGIN SET @sql = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC(@sql); END

        IF SUSER_ID(@user) IS NULL
            SET @sql = N'CREATE LOGIN ' + QUOTENAME(@user) + N' WITH PASSWORD = N''' + REPLACE(@pw, '''', '''''') + N''', CHECK_POLICY = OFF';
        ELSE
            SET @sql = N'ALTER LOGIN ' + QUOTENAME(@user) + N' WITH PASSWORD = N''' + REPLACE(@pw, '''', '''''') + N'''';
        EXEC(@sql);

        SET @sql = N'USE ' + QUOTENAME(@db) + N';
            IF USER_ID(' + QUOTENAME(@user, '''') + N') IS NULL CREATE USER ' + QUOTENAME(@user) + N' FOR LOGIN ' + QUOTENAME(@user) + N';
            ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@user) + N';';
        EXEC(@sql);",
        ("@db", db), ("@user", user), ("@pw", pw));

    Console.WriteLine($"{prefix}: database '{db}' and login '{user}' ready (db_owner on that database only).");
}
