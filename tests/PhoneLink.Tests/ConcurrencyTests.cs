using System.Text;
using PhoneLink.Interop;

namespace PhoneLink.Tests;

internal static partial class Suite
{
    private static void ConcurrencyTests()
    {
        Test("WAL writer commits during an active CLI snapshot without torn rows/parts", () => {
            string concurrentRoot = Path.Combine(temporary, "concurrent-wal");
            string path = Path.Combine(Fixture.Device(concurrentRoot, "one"), "phone.db");
            Fixture.Phone(path, false);
            using var writer = new Writer(path);
            var setup = new StringBuilder("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; BEGIN;");
            for (int id = 1; id <= 512; id++)
                setup.Append($"INSERT INTO message VALUES({id},1,{Fixture.Time},'old generation',1);");
            setup.Append($"INSERT INTO mms VALUES(1,1,{Fixture.Time + 1});");
            setup.Append("INSERT INTO mms_part VALUES(1,1,0,'text/plain','old MMS',NULL,7,NULL); COMMIT;");
            writer.Exec(setup.ToString());

            string barrier = Path.Combine(concurrentRoot, "barrier");
            Directory.CreateDirectory(barrier);
            var reader = Task.Run(() => RunEnv(concurrentRoot,
                new() { ["FIXTURE_READ_BARRIER"] = barrier }, "messages", "--limit", "10000"));
            Result? snapshot = null;
            try
            {
                bool signaled = SpinWait.SpinUntil(() => File.Exists(Path.Combine(barrier, "reader-ready")) || reader.IsCompleted,
                    TimeSpan.FromSeconds(15));
                Require(signaled && File.Exists(Path.Combine(barrier, "reader-ready")), "reader did not establish its snapshot");
                Require(!reader.IsCompleted, "reader was not paused inside the snapshot");
                // This commit must finish BEFORE releasing the reader. The body
                // updates, late-page key deletion/insertion, and MMS part change
                // are a single Windows SQLite transaction on the same live DB.
                writer.Exec($"""
                    BEGIN IMMEDIATE;
                    UPDATE message SET body='new generation';
                    DELETE FROM message WHERE message_id=511;
                    INSERT INTO message VALUES(10000,1,{Fixture.Time},'new generation',1);
                    UPDATE mms_part SET text='new MMS' WHERE part_id=1;
                    UPDATE mms SET timestamp={Fixture.Time + 20} WHERE message_id=1;
                    COMMIT;
                    """);
                Require(!reader.IsCompleted, "reader finished before the concurrent commit assertion");
            }
            finally
            {
                // Always release and join the child so no test process or lock
                // survives a failed assertion and interferes with fixture cleanup.
                File.WriteAllText(Path.Combine(barrier, "reader-continue"), "synthetic test marker");
                snapshot = reader.GetAwaiter().GetResult();
            }
            var before = Rows(snapshot);
            var sms = before.Where(r => r.GetProperty("kind").GetString() == "sms").ToArray();
            Require(sms.Length == 512 && sms.All(r => r.GetProperty("body").GetString() == "old generation"), "mixed-generation body read");
            Require(sms.Any(r => r.GetProperty("id").GetString() == "511") && !sms.Any(r => r.GetProperty("id").GetString() == "10000"), "key pagination escaped its snapshot");
            var mms = Find(before, "mms", "1");
            Require(mms.GetProperty("body").GetString() == "old MMS" &&
                mms.GetProperty("timestamp_filetime").GetString() == (Fixture.Time + 1).ToString(), "MMS header/part snapshot torn");

            var after = Rows(RunAt(concurrentRoot, "messages", "--limit", "10000"));
            sms = after.Where(r => r.GetProperty("kind").GetString() == "sms").ToArray();
            Require(sms.Length == 512 && sms.All(r => r.GetProperty("body").GetString() == "new generation"), "next invocation did not see committed writes");
            Require(!sms.Any(r => r.GetProperty("id").GetString() == "511") && sms.Any(r => r.GetProperty("id").GetString() == "10000"), "new committed key set missing");
            Require(Find(after, "mms", "1").GetProperty("body").GetString() == "new MMS", "new MMS part not visible");
        });

        Test("rollback-journal reader can delay writer commit until reader disposal", () => {
            string rollbackRoot = Path.Combine(temporary, "rollback-reader");
            string path = Path.Combine(Fixture.Device(rollbackRoot, "one"), "phone.db");
            Fixture.Phone(path, false);
            using var writer = new Writer(path);
            writer.Exec($"PRAGMA journal_mode=DELETE; PRAGMA busy_timeout=0; INSERT INTO message VALUES(1,1,{Fixture.Time},'before',1);");
            using (var database = new ReadOnlyDatabase(path))
            {
                using (var row = database.Prepare("SELECT body FROM message WHERE message_id=1"))
                    Require(row.Step() && (string?)row.Get(0) == "before", "snapshot not established");
                writer.Exec("BEGIN IMMEDIATE; UPDATE message SET body='after' WHERE message_id=1;");
                bool busy = false;
                try { writer.Exec("COMMIT;"); }
                catch (FixtureSqliteException error) when (error.Code == 5) { busy = true; }
                Require(busy, "expected reader to delay rollback-journal commit");
            }
            writer.Exec("COMMIT;");
            Require(Rows(RunAt(rollbackRoot, "messages"))[0].GetProperty("body").GetString() == "after", "writer did not recover after reader released locks");
        });
    }
}
