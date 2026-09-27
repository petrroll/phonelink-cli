using System.Runtime.InteropServices;
using System.Text;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PhoneLink.Interop;

// All live database access uses the Windows VFS and normal WAL locks, not Linux
// SQLite on /mnt/c, file copies, immutable=1, or Phone Link's migrating DB ctor.
internal sealed class ReadOnlyDatabase : IDisposable
{
    private nint handle;
    private readonly HashSet<Statement> statements = [];
    private CliException? fault;
    public string Path { get; }

    // Some vendor versions may catch adapter exceptions. Preserve the first
    // boundary failure so a caught error cannot become a false empty success.
    internal T Observe<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (Exception error)
        {
            var failure = error as CliException ?? CliException.Contract("Read-adapter operation failed (" + error.GetType().Name + ").");
            fault ??= failure;
            throw failure;
        }
    }
    internal void ThrowIfFaulted() { if (fault != null) throw fault; }

    public ReadOnlyDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(Path)) throw new CliException("database_not_found", "Database not found: " + Path);
        int result = Native.sqlite3_open_v2(Utf8(Path), out handle, 0x1 | 0x10000, 0); // READONLY | FULLMUTEX
        try
        {
            Check(result);
            if (Native.sqlite3_db_readonly(handle, Utf8("main")) != 1)
                throw new CliException("not_read_only", "Refusing a writable database connection.");
            Check(Native.sqlite3_busy_timeout(handle, 3000));
            // Keep one consistent snapshot while reading related headers/parts.
            using var begin = Prepare("BEGIN", allowBegin: true);
            _ = begin.Step();
        }
        catch { Dispose(); throw; }
    }

    public Statement Prepare(string sql) => Prepare(sql, allowBegin: false);

    private Statement Prepare(string sql, bool allowBegin)
    {
        ObjectDisposedException.ThrowIf(handle == 0, this);
        // SQLite labels ATTACH (which may create a file) and some PRAGMAs as
        // read-only. Permit only query forms plus our own deferred BEGIN.
        string start = sql.TrimStart();
        bool query = System.Text.RegularExpressions.Regex.IsMatch(start, @"\A(?:SELECT|WITH)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!query && !(allowBegin && start.TrimEnd().Equals("BEGIN", StringComparison.OrdinalIgnoreCase)))
            throw new CliException("not_read_only", "Only read-only SELECT/WITH queries are allowed.");
        var statement = new Statement(this, sql);
        statements.Add(statement);
        return statement;
    }

    public bool HasTable(string name)
    {
        using var query = Prepare("SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = ?");
        query.Bind(1, name);
        return query.Step();
    }

    // Only generic key enumeration is implemented here. Table/key names come
    // from the vendor object; all content queries and entity mapping are vendor APIs.
    public IEnumerable<long[]> KeyBatches(string table, string key, int size = 256)
    {
        long? after = null;
        while (true)
        {
            using var query = Prepare($"SELECT {Quote(key)} FROM {Quote(table)}" +
                (after.HasValue ? $" WHERE {Quote(key)} > ?" : "") + $" ORDER BY {Quote(key)} LIMIT {size}");
            if (after.HasValue) query.Bind(1, after.Value);
            var ids = new List<long>(size);
            while (query.Step())
            {
                if (query.Get(0) is not long id) throw CliException.Contract("The vendor table does not have integer keys.");
                ids.Add(id);
            }
            if (ids.Count == 0) yield break;
            yield return ids.ToArray();
            after = ids[^1];
            if (ids.Count < size || after == long.MaxValue) yield break;
        }
    }

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');
    private void Check(int code)
    {
        if (code == 0) return;
        // sqlite3_errmsg can echo tokens from vendor-generated SQL or schema.
        // Report status/metadata only, never its potentially private raw text.
        string detail = (code & 0xff) switch {
            5 or 6 => "Database is busy or locked; retry with backoff",
            8 => "Database access was refused as read-only",
            14 => "Database could not be opened",
            26 => "Database format is unsupported or is not SQLite",
            _ => "Database operation failed"
        };
        throw new CliException("sqlite_error", $"{detail} (SQLite {code}): {Path}");
    }
    public void Dispose()
    {
        foreach (var statement in statements.ToArray()) statement.Dispose();
        if (handle != 0) { Native.sqlite3_close_v2(handle); handle = 0; }
    }

    internal sealed record BlobInfo(int Length);

    internal sealed class Statement : IDisposable
    {
        private readonly ReadOnlyDatabase database;
        internal ReadOnlyDatabase Owner => database;
        private nint handle;
        private bool hasRow;
        private readonly bool[] bound;
        private readonly Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly int columnCount;
        public string Sql { get; }

        public Statement(ReadOnlyDatabase database, string sql)
        {
            this.database = database;
            Sql = sql;
            byte[] encoded = Utf8(sql);
            nint memory = Marshal.AllocHGlobal(encoded.Length);
            try
            {
                Marshal.Copy(encoded, 0, memory, encoded.Length);
                database.Check(Native.sqlite3_prepare_v2(database.handle, memory, encoded.Length, out handle, out var tail));
                if (handle == 0 || Native.sqlite3_stmt_readonly(handle) == 0)
                    throw new CliException("not_read_only", "The library attempted a non-read-only statement.");
                long consumed = tail.ToInt64() - memory.ToInt64();
                if (consumed < 0 || consumed >= encoded.Length ||
                    !string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(encoded, (int)consumed, encoded.Length - 1 - (int)consumed)))
                    throw new CliException("multiple_statements", "Only a single read-only statement is permitted.");
                bound = new bool[Native.sqlite3_bind_parameter_count(handle)];
                columnCount = Native.sqlite3_column_count(handle);
                for (int i = 0; i < columnCount; i++)
                    columns.TryAdd(Marshal.PtrToStringUTF8(Native.sqlite3_column_name(handle, i))!, i);
            }
            catch { if (handle != 0) Native.sqlite3_finalize(handle); handle = 0; throw; }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private void EnsureOpen()
        {
            ObjectDisposedException.ThrowIf(handle == 0 || database.handle == 0, this);
        }
        public void Bind(int index, object? value)
        {
            EnsureOpen();
            if (hasRow || index < 1 || index > bound.Length) throw CliException.Contract("Invalid statement binding.");
            int result;
            switch (value)
            {
                case null: result = Native.sqlite3_bind_null(handle, index); break;
                case string text:
                    byte[] bytes = Utf8(text);
                    result = Native.sqlite3_bind_text(handle, index, bytes, bytes.Length - 1, new nint(-1));
                    break;
                case double number: result = Native.sqlite3_bind_double(handle, index, number); break;
                case bool flag: result = Native.sqlite3_bind_int64(handle, index, flag ? 1 : 0); break;
                case DateTimeOffset time: result = Native.sqlite3_bind_int64(handle, index, WindowsTime.ToFileTime(time)); break;
                case int number: result = Native.sqlite3_bind_int64(handle, index, number); break;
                case long number: result = Native.sqlite3_bind_int64(handle, index, number); break;
                default: throw CliException.Contract("Unsupported statement parameter type: " + value.GetType().Name);
            }
            database.Check(result);
            bound[index - 1] = true;
        }
        public bool Step()
        {
            EnsureOpen();
            if (bound.Any(value => !value)) throw CliException.Contract("The library left a SQL parameter unbound.");
            int result = Native.sqlite3_step(handle);
            hasRow = result == 100;
            if (hasRow) return true;
            if (result != 101) database.Check(result);
            Reset(); // matches the installed library's statement contract
            return false;
        }
        public void Reset()
        {
            EnsureOpen();
            database.Check(Native.sqlite3_reset(handle));
            database.Check(Native.sqlite3_clear_bindings(handle));
            Array.Clear(bound);
            hasRow = false;
        }
        public bool HasColumn(string name) => columns.ContainsKey(name);
        public object? Get(string name) => columns.TryGetValue(name, out int index) ? Get(index) :
            throw CliException.Contract("A required result column is missing: " + name);
        public object? Get(int index)
        {
            EnsureOpen();
            if (!hasRow || index < 0 || index >= columnCount) throw CliException.Contract("Invalid result-row access.");
            switch (Native.sqlite3_column_type(handle, index))
            {
                case 1: return Native.sqlite3_column_int64(handle, index);
                case 2: return Native.sqlite3_column_double(handle, index);
                case 3:
                    nint text = Native.sqlite3_column_text(handle, index);
                    int length = Native.sqlite3_column_bytes(handle, index);
                    if (length == 0) return "";
                    if (text == 0) throw new CliException("sqlite_error", "SQLite returned a null text pointer.");
                    byte[] bytes = new byte[length];
                    Marshal.Copy(text, bytes, 0, length);
                    return Encoding.UTF8.GetString(bytes);
                case 4: return new BlobInfo(Native.sqlite3_column_bytes(handle, index)); // never read blob bytes
                case 5: return null;
                default: throw new CliException("sqlite_error", "Unknown SQLite column type.");
            }
        }
        public void Dispose()
        {
            if (handle != 0) { Native.sqlite3_finalize(handle); handle = 0; }
            database.statements.Remove(this);
        }
    }

    private static class Native
    {
        private const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] path, out nint db, int flags, nint vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(nint db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_db_readonly(nint db, byte[] name);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(nint db, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(nint db, nint sql, int length, out nint statement, out nint tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_stmt_readonly(nint statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_parameter_count(nint statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(nint statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(nint statement, int index, long value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_double(nint statement, int index, double value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(nint statement, int index, byte[] value, int bytes, nint destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(nint statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_reset(nint statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_clear_bindings(nint statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(nint statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(nint statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_column_name(nint statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(nint statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(nint statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(nint statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_column_text(nint statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(nint statement, int index);
    }
}

internal static class WindowsTime
{
    private static readonly DateTimeOffset Epoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static DateTimeOffset FromFileTime(long value)
    {
        try { return Epoch.AddTicks(value); }
        catch (ArgumentOutOfRangeException) { throw CliException.Contract("Cached timestamp is outside the representable FILETIME range."); }
    }
    public static long ToFileTime(DateTimeOffset value) => checked(value.UtcTicks - Epoch.Ticks);
}
