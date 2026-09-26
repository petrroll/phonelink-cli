using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneLink.Tests;

internal static class Fixture
{
    public static readonly long Time = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero).ToFileTime();
    public const string Text = "Synthetic only: žluťoučký, नमस्ते, 😀, quote \" and \\ slash";
    public static string Device(string root, string id)
    {
        string directory = Path.Combine(root, id, "System", "Database");
        Directory.CreateDirectory(directory);
        return directory;
    }
    public static string Q(string text) => "'" + text.Replace("'", "''") + "'";
    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static void Phone(string path, bool rich = true)
    {
        using var writer = new Writer(path);
        writer.Exec("""
            CREATE TABLE message(message_id INTEGER PRIMARY KEY,thread_id INTEGER,timestamp INTEGER,body TEXT,mailbox INTEGER);
            CREATE TABLE mms(message_id INTEGER PRIMARY KEY,thread_id INTEGER,timestamp INTEGER);
            CREATE TABLE rcs_chat(message_id INTEGER PRIMARY KEY,thread_id INTEGER,timestamp INTEGER,body TEXT);
            CREATE TABLE rcs_filetransfer(message_id INTEGER PRIMARY KEY,thread_id INTEGER,timestamp INTEGER,name TEXT,size INTEGER,content_type TEXT,blob BLOB);
            CREATE TABLE mms_part(part_id INTEGER PRIMARY KEY,message_id INTEGER,sequence_num INTEGER,content_type TEXT,text TEXT,name TEXT,size INTEGER,blob BLOB);
            CREATE TABLE conversation(thread_id INTEGER PRIMARY KEY,timestamp INTEGER,recipients TEXT,message_count INTEGER,unread_count INTEGER);
            CREATE TABLE rcs_conversation(thread_id INTEGER PRIMARY KEY,fallback_id INTEGER,timestamp INTEGER,recipients TEXT,message_count INTEGER,unread_count INTEGER);
            """);
        if (!rich) return;
        writer.Exec($"""
            INSERT INTO message VALUES(1,10,{Time},{Q(Text)},1);
            INSERT INTO message VALUES(2,11,{Time + 100000000L},'outgoing',2);
            INSERT INTO message VALUES(3,10,0,'before'||char(0)||'after',1);
            INSERT INTO message VALUES(9007199254740993,9007199254740995,{Time + 400000000L},'large ID',1);
            INSERT INTO mms VALUES(1,10,{Time + 200000000L});
            INSERT INTO mms_part VALUES(20,1,1,'text/plain','second part',NULL,11,NULL);
            INSERT INTO mms_part VALUES(10,1,0,'text/plain','first part',NULL,10,NULL);
            INSERT INTO mms_part VALUES(30,1,2,'image/jpeg',NULL,'photo.jpg',3,X'010203');
            INSERT INTO mms_part VALUES(31,1,3,'application/smil','<smil/>',NULL,7,NULL);
            INSERT INTO rcs_chat VALUES(5,12,{Time + 300000000L},'RCS body');
            INSERT INTO rcs_filetransfer VALUES(6,12,{Time + 500000000L},'document.pdf',4,'application/pdf',X'01020304');
            INSERT INTO conversation VALUES(10,{Time + 200000000L},'+15550000001',3,1);
            INSERT INTO conversation VALUES(11,{Time + 100000000L},'+15550000002',1,0);
            INSERT INTO rcs_conversation VALUES(12,10,{Time + 500000000L},'+15550000003',2,0);
            """);
    }
    public static void Notifications(string path)
    {
        string payload = JsonSerializer.Serialize(new { appName = "Example", title = Text, text = "short", bigText = "expanded", messages = new[] { new { text = "nested synthetic text" } } });
        string mail = JsonSerializer.Serialize(new { title = "Mail", text = "mail text" });
        using var writer = new Writer(path);
        writer.Exec($"""
            CREATE TABLE notifications(id INTEGER PRIMARY KEY,notification_id TEXT,package_name TEXT,json TEXT,post_time INTEGER);
            INSERT INTO notifications VALUES(1,'one','org.example.chat',{Q(payload)},{Time + 50000000L});
            INSERT INTO notifications VALUES(2,'two','org.example.mail',{Q(mail)},{Time + 150000000L});
            """);
    }
}

// WRITE-CAPABLE CODE EXISTS ONLY IN THE SYNTHETIC TEST HARNESS, not the CLI.
internal sealed class Writer : IDisposable
{
    private nint handle;
    public Writer(string path)
    {
        int result = sqlite3_open_v2(Encoding.UTF8.GetBytes(path + '\0'), out handle, 2 | 4, 0);
        if (result != 0) { Dispose(); throw new Exception("Fixture open failed: " + result); }
    }
    public void Exec(string sql)
    {
        int result = sqlite3_exec(handle, Encoding.UTF8.GetBytes(sql + '\0'), 0, 0, out var error);
        try { if (result != 0) throw new Exception("Fixture SQL failed: " + result + ": " + Marshal.PtrToStringUTF8(error)); }
        finally { if (error != 0) sqlite3_free(error); }
    }
    public void Dispose() { if (handle != 0) { sqlite3_close_v2(handle); handle = 0; } }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out nint handle, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(nint handle, byte[] sql, nint callback, nint arg, out nint error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(nint handle);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(nint memory);
}
