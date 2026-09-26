// Synthetic vendor API implementation for integration testing. Authored for
// this project, not copied/decompiled Microsoft implementation code.
using YourPhone.AppCore.WinRT.DataStore;

namespace YourPhone.Messaging.WinRT.DataStore;

public enum Mailbox { Inbox = 1, Sent = 2 }
public enum ReadStatus { Unread = 1, Read = 2 }

public class Message
{
    public long Id { get; set; }
    public long ThreadId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Body { get; set; }
    public string? Subject { get; set; }
    public string? FromAddress { get; set; }
    public string? ToAddress { get; set; }
    public string[]? ToAddresses { get; set; }
    public Mailbox MailboxType { get; set; } = Mailbox.Inbox;
    public ReadStatus Status { get; set; } = ReadStatus.Unread;
    public ReadStatus PcStatus { get; set; } = ReadStatus.Unread;
    public string? Name { get; set; }
    public string? ContentType { get; set; }
    public int Size { get; set; }
    public IFixtureBlobReference? BlobStream { get; set; }
}

public sealed class Part
{
    public long Id { get; set; }
    public int SequenceNumber { get; set; }
    public string? ContentType { get; set; }
    public string? Text { get; set; }
    public string? Name { get; set; }
    public int Size { get; set; }
    public IFixtureBlobReference? BlobStream { get; set; }
}

public sealed class Conversation
{
    public long Id { get; set; }
    public long ThreadId { get; set; }
    public DateTimeOffset LatestTimestamp { get; set; }
    public string[] RecipientAddresses { get; set; } = [];
    public int MessageCount { get; set; }
    public int UnreadCount { get; set; }
    public int PhoneUnreadCount { get; set; }
}

public abstract class Table(ISqliteConnection connection, string name, string key)
{
    protected ISqliteConnection Connection { get; } = connection;
    public string TableName { get; } = name;
    public string PrimaryKeyName { get; } = key;
    protected virtual string Projection => "*";
    protected abstract object Map(ISqliteStatement row);

    public IReadOnlyList<object> GetEntitiesFromIds(IReadOnlyList<long> ids)
    {
        if (Environment.GetEnvironmentVariable("FIXTURE_ATTEMPT_WRITE") == "1")
        {
            using var forbidden = Connection.CreateStatement("DELETE FROM message");
            forbidden.Step();
        }
        if (Environment.GetEnvironmentVariable("FIXTURE_THROW_SECRET") == "1")
            throw new InvalidOperationException("synthetic-private-value-do-not-leak");
        using var row = Connection.CreateStatement($"SELECT {Projection} FROM {TableName} WHERE {PrimaryKeyName} IN ({string.Join(',', ids)})");
        return Read(row);
    }
    public IReadOnlyList<object> GetMessagesInThread(long thread)
    {
        using var row = Connection.CreateStatement($"SELECT {Projection} FROM {TableName} WHERE thread_id = ?");
        row.BindInt64(1, thread);
        return Read(row);
    }
    protected List<object> Read(ISqliteStatement statement)
    {
        var result = new List<object>();
        while (statement.Step()) result.Add(Map(statement));
        return result;
    }
    protected Message ReadMessage(ISqliteStatement r) => new() {
        Id = r.ReadInt64("message_id"), ThreadId = r.ReadInt64("thread_id"),
        Timestamp = r.ReadDateTime("timestamp"), Body = r.HasColumn("body") ? r.ReadText16("body") : null,
        FromAddress = "+15550000000", MailboxType = r.HasColumn("mailbox") ? (Mailbox)r.ReadInt("mailbox") : Mailbox.Inbox
    };
}

public sealed class SmsTable(ISqliteConnection connection) : Table(connection, "message", "message_id")
{
    protected override object Map(ISqliteStatement row) => ReadMessage(row);
}
public sealed class MmsTable(ISqliteConnection connection) : Table(connection, "mms", "message_id")
{
    protected override object Map(ISqliteStatement row) => ReadMessage(row);
}
public sealed class RcsChatTable(ISqliteConnection connection) : Table(connection, "rcs_chat", "message_id")
{
    protected override object Map(ISqliteStatement row) => ReadMessage(row);
}
public sealed class RcsFileTransferTable(ISqliteConnection connection) : Table(connection, "rcs_filetransfer", "message_id")
{
    protected override string Projection => "message_id,thread_id,timestamp,name,size,content_type,length(blob) AS blob_length";
    protected override object Map(ISqliteStatement row)
    {
        var value = ReadMessage(row);
        value.Name = row.ReadText16("name");
        value.Size = row.ReadInt("size");
        value.ContentType = row.ReadText16("content_type");
        value.BlobStream = row.ReadBlob("blob", TableName, value.Id);
        return value;
    }
}
public sealed class MmsPartTable(ISqliteConnection connection) : Table(connection, "mms_part", "part_id")
{
    protected override string Projection => "part_id,message_id,sequence_num,content_type,text,name,size,length(blob) AS blob_length";
    public IReadOnlyList<object> GetPartsForMessage(long id)
    {
        using var row = Connection.CreateStatement($"SELECT {Projection} FROM mms_part WHERE message_id = ? AND content_type != 'application/smil'");
        row.BindInt64(1, id);
        return Read(row);
    }
    protected override object Map(ISqliteStatement row) => new Part {
        Id = row.ReadInt64("part_id"), SequenceNumber = row.ReadInt("sequence_num"), ContentType = row.ReadText16("content_type"),
        Text = row.ReadText16("text"), Name = row.ReadText16("name"), Size = row.ReadInt("size"),
        BlobStream = row.ReadBlob("blob", TableName, row.ReadInt64("part_id"))
    };
}
public class ConversationTable(ISqliteConnection connection) : Table(connection, "conversation", "thread_id")
{
    protected override object Map(ISqliteStatement r) => ReadConversation(r, false);
    internal static Conversation ReadConversation(ISqliteStatement r, bool rcs) => new() {
        Id = r.ReadInt64("thread_id"), ThreadId = rcs ? r.ReadInt64("fallback_id") : r.ReadInt64("thread_id"),
        LatestTimestamp = r.ReadDateTime("timestamp"), RecipientAddresses = r.ReadText16("recipients").Split(','),
        MessageCount = r.ReadInt("message_count"), UnreadCount = r.ReadInt("unread_count"), PhoneUnreadCount = r.ReadInt("unread_count")
    };
}
public sealed class RcsConversationTable(ISqliteConnection connection) : Table(connection, "rcs_conversation", "thread_id")
{
    protected override object Map(ISqliteStatement row) => ConversationTable.ReadConversation(row, true);
}
