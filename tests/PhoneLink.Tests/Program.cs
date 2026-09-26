using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PhoneLink.Interop;
using PhoneLink.Libraries;
using YourPhone.AppCore.WinRT.DataStore;

namespace PhoneLink.Tests;

internal static partial class Suite
{
    private static string executable = "";
    private static string libraries = "";
    private static string temporary = "";
    private static string root = "";
    private static int passed, failed;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (!OperatingSystem.IsWindows() || args.Length != 1)
        {
            Console.Error.WriteLine("Run on Windows and pass the published phonelink.exe path. Tests use only synthetic data.");
            return 2;
        }
        executable = Path.GetFullPath(args[0]);
        libraries = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        temporary = Path.Combine(Path.GetTempPath(), "PhoneLink CLI synthetic žluťoučký " + Guid.NewGuid().ToString("N"));
        root = Path.Combine(temporary, "Indexed");
        try
        {
            string first = Fixture.Device(root, "alpha-device");
            string phone = Path.Combine(first, "phone.db"), notifications = Path.Combine(first, "notifications.db");
            Fixture.Phone(phone);
            Fixture.Notifications(notifications);
            Directory.CreateDirectory(Path.Combine(root, "FullTrust"));
            string phoneHash = Fixture.Hash(phone), notificationHash = Fixture.Hash(notifications);

            Test("help, version, and honest provenance", () => {
                var help = Invoke(["--help"]);
                Require(help.ExitCode == 0 && help.Out.Contains("AI / Astra") && help.Out.Contains("NON-PUBLIC"), "missing warnings/help");
                var version = Parse(Invoke(["version"]));
                Require(version.GetProperty("backend").GetString() == "phonelink-managed-libraries", "wrong backend");
                Require(version.GetProperty("cached").GetBoolean(), "not disclosing cache source");
            });
            Test("doctor resolves explicit fixture libraries", () => {
                var result = Parse(Run("doctor"));
                Require(result.GetProperty("read_only").GetBoolean(), "not read-only");
                Require(result.GetProperty("libraries").GetProperty("capabilities").GetArrayLength() == 8, "missing capabilities");
            });
            Test("devices metadata only; ignores FullTrust", () => {
                var result = Run("devices");
                Require(Rows(result).Length == 1, "wrong profile count");
                Require(!result.Out.Contains("Synthetic only:"), "metadata command leaked message data");
            });
            Test("single profile auto-selection and every message kind", () => {
                var rows = Rows(Run("messages"));
                Require(rows.Length == 7, "wrong message count");
                Require(rows[0].GetProperty("kind").GetString() == "rcs_filetransfer", "wrong newest row");
                Require(rows.Select(r => r.GetProperty("kind").GetString()).Distinct().Count() == 4, "kind missing");
            });
            Test("global limit across kinds", () => {
                var rows = Rows(Run("messages", "--limit", "2"));
                Require(rows.Length == 2 && rows[1].GetProperty("id").GetString() == "9007199254740993", "limit/order wrong");
            });
            Test("Unicode paths, quotes, backslashes, and embedded NUL", () => {
                var rows = Rows(Run("messages"));
                Require(Find(rows, "sms", "1").GetProperty("body").GetString() == Fixture.Text, "Unicode changed");
                Require(Find(rows, "sms", "3").GetProperty("body").GetString() == "before\0after", "NUL truncated");
                Require(Rows(RunAt(root + "\\", "messages")).Length == 7, "trailing path separator failed");
            });
            Test("64-bit IDs and FILETIME remain exact JSON strings", () => {
                var row = Find(Rows(Run("messages")), "sms", "9007199254740993");
                Require(row.GetProperty("thread_id").GetString() == "9007199254740995", "ID precision lost");
                Require(row.GetProperty("timestamp_filetime").GetString() == (Fixture.Time + 400000000L).ToString(), "FILETIME precision lost");
                Require(row.GetProperty("timestamp_utc").GetString() == "2026-09-01T12:00:40.0000000+00:00", "timestamp conversion wrong");
            });
            Test("kind filter", () => {
                Require(Rows(Run("messages", "--kind", "sms")).Length == 4, "SMS filter wrong");
                Require(Rows(Run("messages", "--kind", "rcs_filetransfer")).Length == 1, "RCS file filter wrong");
            });
            Test("thread filter across SMS/MMS and large IDs", () => {
                Require(Rows(Run("messages", "--thread", "10")).Length == 3, "thread query wrong");
                Require(Rows(Run("messages", "--thread", "9007199254740995")).Length == 1, "large thread query wrong");
            });
            Test("since inclusive, timezone-aware, 100ns precision", () => {
                Require(Rows(Run("messages", "--since", "2026-09-01T12:00:20Z")).Length == 4, "inclusive boundary wrong");
                Require(Rows(Run("messages", "--since", "2026-09-01T14:00:20+02:00")).Length == 4, "offset conversion wrong");
                Require(Rows(Run("messages", "--since", "2026-09-01T12:00:40.0000001Z")).Length == 1, "tick precision wrong");
            });
            Test("MMS text/part ordering and lazy binary metadata", () => {
                var row = Find(Rows(Run("messages")), "mms", "1");
                Require(row.GetProperty("body").GetString() == "first part\nsecond part", "MMS body/order wrong");
                var parts = row.GetProperty("parts").EnumerateArray().ToArray();
                Require(parts.Length == 3, "SMIL should be omitted by the fixture library");
                Require(parts[2].GetProperty("cached_blob_available").GetBoolean(), "blob presence lost");
                Require(parts[2].GetProperty("name").GetString() == "photo.jpg", "attachment metadata lost");
                Require(!row.GetRawText().Contains("AQID"), "blob content escaped into JSON");
            });
            Test("RCS file-transfer metadata without binary content", () => {
                var row = Rows(Run("messages", "--kind", "rcs_filetransfer"))[0];
                Require(row.GetProperty("name").GetString() == "document.pdf", "file name missing");
                Require(row.GetProperty("size_bytes").GetInt64() == 4 && row.GetProperty("cached_blob_available").GetBoolean(), "file metadata missing");
            });
            Test("conversation IDs distinguish RCS from its SMS fallback", () => {
                var rows = Rows(Run("conversations"));
                Require(rows.Length == 3, "conversation count wrong");
                Require(rows[0].GetProperty("thread_id").GetString() == "12", "RCS key conflated with fallback");
                Require(rows[0].GetProperty("sms_mms_thread_id").GetString() == "10", "fallback missing");
                Require(rows[1].GetProperty("unread_count").GetInt64() == 1, "read state changed");
            });
            Test("notification library maps text and preserves optional payload", () => {
                var row = Rows(Run("notifications", "--app", "org.example.chat", "--include-payload"))[0];
                Require(row.GetProperty("title").GetString() == Fixture.Text, "notification title changed");
                Require(row.GetProperty("text").GetString() == "expanded", "expanded text missing");
                Require(row.GetProperty("payload").GetProperty("messages").GetArrayLength() == 1, "nested payload missing");
                Require(!Rows(Run("notifications"))[0].TryGetProperty("payload", out _), "payload should be opt-in");
            });
            Test("notification filters are exact; quoted input cannot inject SQL", () => {
                Require(Rows(Run("notifications", "--app", "org.example.chat' OR 1=1 --")).Length == 0, "app injection/filter failure");
                Require(Rows(Run("notifications", "--since", "2026-09-01T12:00:15Z")).Length == 1, "notification since wrong");
            });
            Test("pretty output is valid JSON", () => Require(Rows(Run("messages", "--pretty")).Length == 7, "pretty JSON invalid"));
            Test("arguments fail with JSON stderr and empty stdout", () => {
                foreach (var args2 in new[] {
                    new[] { "messages", "--limit", "0" }, new[] { "messages", "--limit", "10001" },
                    new[] { "messages", "--limit", "1", "--limit", "2" }, new[] { "messages", "--limit" },
                    new[] { "messages", "--since", "2026-09-01" }, new[] { "messages", "--thread", "1 OR 1=1" },
                    new[] { "messages", "--app", "wrong" }, new[] { "messages", "--kind", "unknown" },
                    new[] { "messages", "--device", "alpha", "--all-devices" }
                }) Error(Run(args2), 2, "invalid_arguments");
                Error(Invoke(["unknown"]), 2, "invalid_arguments");
            });
            Test("missing root creates nothing; empty root is explicit", () => {
                string missing = Path.Combine(temporary, "missing");
                Error(RunAt(missing, "messages"), 1, "cache_not_found");
                Require(!Directory.Exists(missing), "created missing root");
                Directory.CreateDirectory(missing);
                Require(Rows(RunAt(missing, "devices")).Length == 0, "empty root wrong");
                Error(RunAt(missing, "messages"), 1, "no_devices");
            });
            Test("no installed DLLs are needed by synthetic tests; missing override fails", () => {
                var result = Invoke(["messages", "--cache-root", root, "--library-path", Path.Combine(temporary, "no-libraries")]);
                Error(result, 1, "library_not_found");
            });
            Test("read-only file attribute is supported", () => {
                var original = File.GetAttributes(phone);
                try { File.SetAttributes(phone, original | FileAttributes.ReadOnly); Require(Rows(Run("messages")).Length == 7, "RO file read failed"); }
                finally { File.SetAttributes(phone, original); }
            });
            Test("vendor write attempts are rejected before execution", () => {
                var result = RunEnv(root, new() { ["FIXTURE_ATTEMPT_WRITE"] = "1" }, "messages");
                Error(result, 1, "not_read_only");
                Require(Fixture.Hash(phone) == phoneHash, "vendor write changed DB");
            });
            Test("vendor exception text cannot leak private contents", () => {
                var result = RunEnv(root, new() { ["FIXTURE_THROW_SECRET"] = "1" }, "messages");
                Error(result, 1, "library_call_failed");
                Require(!result.Err.Contains("synthetic-private-value"), "vendor error leaked content");
            });
            Test("optional absent tables are skipped; explicit absent kind fails", () => {
                string minimal = Path.Combine(temporary, "minimal");
                string path = Path.Combine(Fixture.Device(minimal, "one"), "phone.db");
                using (var writer = new Writer(path)) writer.Exec($"CREATE TABLE message(message_id INTEGER PRIMARY KEY,thread_id INTEGER,timestamp INTEGER,body TEXT); INSERT INTO message VALUES(1,1,{Fixture.Time},'minimal');");
                Require(Rows(RunAt(minimal, "messages")).Length == 1, "optional tables not tolerated");
                Error(RunAt(minimal, "messages", "--kind", "mms"), 1, "unsupported_schema");
            });
            Test("incompatible schema fails instead of returning false empty success", () => {
                string bad = Path.Combine(temporary, "schema");
                using (var writer = new Writer(Path.Combine(Fixture.Device(bad, "one"), "phone.db")))
                    writer.Exec("CREATE TABLE message(message_id INTEGER PRIMARY KEY,thread_id INTEGER,body TEXT); INSERT INTO message VALUES(1,1,'synthetic');");
                Error(RunAt(bad, "messages"), 1, "unsupported_library");
            });
            Test("over 5000 records, timestamp ties, and min/max signed IDs are not lost", () => {
                string large = Path.Combine(temporary, "large");
                string path = Path.Combine(Fixture.Device(large, "one"), "phone.db");
                Fixture.Phone(path, false);
                using (var writer = new Writer(path))
                {
                    var sql = new StringBuilder("BEGIN;");
                    foreach (long id in Enumerable.Range(0, 5007).Select(n => (long)n).Append(long.MinValue).Append(long.MaxValue))
                        sql.Append($"INSERT INTO message VALUES({id},1,{Fixture.Time},'synthetic',1);");
                    writer.Exec(sql.Append("COMMIT;").ToString());
                }
                var rows = Rows(RunAt(large, "messages", "--limit", "10000"));
                Require(rows.Length == 5009, "lost records at fixed library limit or key boundary");
                Require(rows[0].GetProperty("id").GetString() == long.MaxValue.ToString(), "max key missing");
                Require(rows[^1].GetProperty("id").GetString() == long.MinValue.ToString(), "min key missing");
            });
            Test("committed WAL-only data visible, base DB not checkpointed", () => {
                string walRoot = Path.Combine(temporary, "wal");
                string path = Path.Combine(Fixture.Device(walRoot, "one"), "phone.db");
                Fixture.Phone(path, false);
                using var writer = new Writer(path);
                writer.Exec($"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO message VALUES(1,1,{Fixture.Time},'only in WAL',1);");
                Require(new FileInfo(path + "-wal").Length > 0, "fixture WAL missing");
                string hash = Fixture.Hash(path);
                var rows = Rows(RunAt(walRoot, "messages"));
                Require(rows.Length == 1 && rows[0].GetProperty("body").GetString() == "only in WAL", "WAL commit invisible");
                Require(Fixture.Hash(path) == hash, "CLI checkpointed DB");
            });
            Test("Windows locks have a bounded failure path", () => {
                string locked = Path.Combine(temporary, "locked");
                string path = Path.Combine(Fixture.Device(locked, "one"), "phone.db");
                Fixture.Phone(path, false);
                using var writer = new Writer(path);
                writer.Exec("BEGIN EXCLUSIVE;");
                Error(RunAt(locked, "messages"), 1, "sqlite_error");
                writer.Exec("ROLLBACK;");
            });
            Test("malformed notification payload does not leak through errors", () => {
                string invalid = Path.Combine(temporary, "invalid-json");
                string path = Path.Combine(Fixture.Device(invalid, "one"), "notifications.db");
                Fixture.Notifications(path);
                using (var writer = new Writer(path)) writer.Exec("UPDATE notifications SET json='private-synthetic-not-json' WHERE id=1;");
                var result = RunAt(invalid, "notifications");
                Error(result, 1, "library_call_failed");
                Require(!result.Err.Contains("private-synthetic-not-json"), "bad payload leaked");
            });
            Test("multiple profiles require explicit, unambiguous selection", () => {
                Fixture.Phone(Path.Combine(Fixture.Device(root, "alpha-backup"), "phone.db"), false);
                Error(Run("messages"), 1, "ambiguous_device");
                Error(Run("messages", "--device", "alpha"), 1, "ambiguous_device");
                Error(Run("messages", "--device", "unknown"), 1, "device_not_found");
                Require(Rows(Run("messages", "--device", "ALPHA-D")).Length == 7, "prefix selection wrong");
                Require(Rows(Run("messages", "--device", "alpha-device")).Length == 7, "exact selection wrong");
            });
            Test("all-devices limit remains global", () => {
                string path = Path.Combine(root, "alpha-backup", "System", "Database", "phone.db");
                using (var writer = new Writer(path)) writer.Exec($"INSERT INTO message VALUES(1,1,{Fixture.Time + 600000000L},'newest other phone',1);");
                var rows = Rows(Run("messages", "--all-devices", "--limit", "2"));
                Require(rows.Length == 2 && rows[0].GetProperty("device_id").GetString() == "alpha-backup", "cross-device ordering/limit wrong");
            });
            Test("failure in a later profile cannot produce partial success JSON", () => {
                string broken = Path.Combine(temporary, "broken");
                Fixture.Phone(Path.Combine(Fixture.Device(broken, "a-good"), "phone.db"));
                File.WriteAllText(Path.Combine(Fixture.Device(broken, "z-bad"), "phone.db"), "not SQLite");
                Error(RunAt(broken, "messages", "--all-devices"), 1, "sqlite_error");
            });
            Test("all read commands leave source DB bytes unchanged", () => {
                Require(Fixture.Hash(phone) == phoneHash && Fixture.Hash(notifications) == notificationHash, "source database changed");
            });
            NativeTests(phone);
            DiscoveryTests();
        }
        catch (Exception error) { failed++; Console.Error.WriteLine("SETUP FAILURE " + error); }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        Console.WriteLine($"{passed} passed, {failed} failed. Synthetic fixtures only; no Phone Link user data accessed.");
        return failed == 0 ? 0 : 1;
    }

    private static void NativeTests(string phone)
    {
        Test("native boundary rejects writes, ATTACH, PRAGMA changes, and SQL tails", () => {
            using var database = new ReadOnlyDatabase(phone);
            foreach (string sql in new[] { "DELETE FROM message", "CREATE TABLE bad(x)", "ATTACH DATABASE 'bad.db' AS bad", "PRAGMA journal_mode=DELETE", "WITH a AS (SELECT 1) DELETE FROM message" })
                Throws(() => database.Prepare(sql), "not_read_only");
            Throws(() => database.Prepare("SELECT 1; SELECT 2"), "multiple_statements");
            Require(!File.Exists(Path.Combine(Environment.CurrentDirectory, "bad.db")), "ATTACH created a DB");
        });
        Test("native binding, reset, duplicates, null and column types", () => {
            using var database = new ReadOnlyDatabase(phone);
            using var statement = database.Prepare("SELECT ? AS value, NULL AS empty, 2.5 AS real_value");
            Throws(() => statement.Step(), "unsupported_library");
            statement.Bind(1, "ž\0😀");
            Require(statement.Step() && (string?)statement.Get("value") == "ž\0😀" && statement.Get("empty") == null, "binding/null wrong");
            Require((double)statement.Get("real_value")! == 2.5, "double decoding wrong");
            Require(!statement.Step(), "DONE missing");
            statement.Bind(1, long.MaxValue);
            Require(statement.Step() && (long)statement.Get(0)! == long.MaxValue, "reset failed");
            using var duplicates = database.Prepare("SELECT 1 AS x, 2 AS x");
            Require(duplicates.Step() && (long)duplicates.Get(1)! == 2, "ordinal bounds used unique-name count");
        });
        Test("adapter binders preserve dates, booleans, doubles, and UTF-8", () => {
            using var database = new ReadOnlyDatabase(phone);
            var adapter = (ReadOnlyConnectionAdapter)DispatchProxy.Create(typeof(ISqliteConnection), typeof(ReadOnlyConnectionAdapter));
            adapter.Database = database; adapter.StatementType = typeof(ISqliteStatement);
            var connection = (ISqliteConnection)(object)adapter;
            using var row = connection.CreateStatement("SELECT ? AS time, ? AS flag, ? AS real_value, ? AS text_value, ? AS number");
            var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1);
            row.BindDateTime(1, time); row.BindBool(2, true); row.BindDouble(3, 2.5);
            row.BindText(4, Encoding.UTF8.GetBytes(Fixture.Text)); row.BindInt(5, 42);
            Require(row.Step() && row.ReadDateTime("time") == time && row.ReadBool("flag"), "date/bool binding wrong");
            Require(row.ReadDouble("real_value") == 2.5 && row.ReadText16("text_value") == Fixture.Text, "double/UTF-8 binding wrong");
            Require(row.ReadText16("number") == "42" && !row.IsDbNull("missing"), "text conversion or missing-column null behavior wrong");
        });
        Test("adapter refuses transactions and binary stream reads", () => {
            using var database = new ReadOnlyDatabase(phone);
            var adapter = (ReadOnlyConnectionAdapter)DispatchProxy.Create(typeof(ISqliteConnection), typeof(ReadOnlyConnectionAdapter));
            adapter.Database = database; adapter.StatementType = typeof(ISqliteStatement);
            var connection = (ISqliteConnection)(object)adapter;
            Throws(() => connection.CreateTransaction(), "unsupported_library");
            using var row = connection.CreateStatement("SELECT length(blob) AS blob_length FROM mms_part WHERE part_id=30");
            Require(row.Step(), "blob fixture missing");
            var reference = row.ReadBlob("blob", "mms_part", 30);
            Require(reference != null, "cached blob metadata lost");
            Throws(() => reference!.OpenReadAsync(), "binary_not_supported");
        });
    }

    private sealed record Result(int ExitCode, string Out, string Err);
    private static Result Run(params string[] args) => RunAt(root, args);
    private static Result RunAt(string cache, params string[] args) => RunEnv(cache, null, args);
    private static Result RunEnv(string cache, Dictionary<string, string>? environment, params string[] args) =>
        Invoke([args[0], "--library-path", libraries, "--cache-root", cache, .. args.Skip(1)], environment);
    private static Result Invoke(string[] args, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (environment != null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("CLI timed out"); }
        return new Result(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
    private static JsonElement Parse(Result result)
    {
        Require(result.ExitCode == 0 && result.Err.Length == 0, "CLI failed: " + result.Err);
        using var document = JsonDocument.Parse(result.Out);
        return document.RootElement.Clone();
    }
    private static JsonElement[] Rows(Result result) => Parse(result).EnumerateArray().ToArray();
    private static JsonElement Find(JsonElement[] rows, string kind, string id) => rows.Single(r => r.GetProperty("kind").GetString() == kind && r.GetProperty("id").GetString() == id);
    private static void Error(Result result, int code, string error)
    {
        Require(result.ExitCode == code && result.Out.Length == 0, "wrong failure result: " + result.Err);
        using var document = JsonDocument.Parse(result.Err);
        Require(document.RootElement.GetProperty("error").GetProperty("code").GetString() == error, "wrong error code: " + result.Err);
    }
    private static void Throws(Action action, string code)
    {
        try { action(); }
        catch (CliException error) { Require(error.Code == code, "wrong exception: " + error.Code); return; }
        throw new Exception("Expected exception " + code);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
    }
}
