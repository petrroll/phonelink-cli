using System.Collections;
using System.Globalization;
using System.Text.Json;
using PhoneLink.Interop;
using PhoneLink.Libraries;

namespace PhoneLink;

internal sealed record OutputRow(long Ticks, string Device, string Kind, long Id, Dictionary<string, object?> Data);

internal sealed class NewestRows(int limit)
{
    private static readonly IComparer<OutputRow> Comparer = System.Collections.Generic.Comparer<OutputRow>.Create((a, b) => {
        int order = b.Ticks.CompareTo(a.Ticks);
        if (order == 0) order = StringComparer.Ordinal.Compare(a.Device, b.Device);
        if (order == 0) order = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
        return order == 0 ? b.Id.CompareTo(a.Id) : order;
    });
    private readonly SortedSet<OutputRow> rows = new(Comparer);
    public void Add(OutputRow row)
    {
        rows.Add(row);
        if (rows.Count > limit) rows.Remove(rows.Max!);
    }
    public IReadOnlyList<OutputRow> Items => rows.ToArray();
}

internal sealed class LibraryReader(LibraryCatalog catalog)
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private object? Value(object entity, string name, bool required = false) => catalog.Property(entity, name, required);
    private long Number(object entity, string name, bool required = true)
    {
        return Value(entity, name, required) switch {
            long number => number, int number => number,
            null when !required => 0,
            _ => throw CliException.Contract("Unexpected numeric property: " + entity.GetType().Name + "." + name + ".")
        };
    }
    private DateTimeOffset Time(object entity, string name) => Value(entity, name, true) is DateTimeOffset time ? time :
        throw CliException.Contract("Unexpected timestamp property: " + name + ".");
    private string? Text(object entity, string name) => Value(entity, name) switch {
        string text => text, null => null, _ => throw CliException.Contract("Unexpected text property: " + name + ".")
    };
    private string? Label(object entity, string name) => Value(entity, name) switch {
        null => null, Enum value => value.ToString(), int value => Id(value), long value => Id(value),
        _ => throw CliException.Contract("Unexpected enum property: " + name + ".")
    };
    private object? Strings(object entity, string name)
    {
        object? value = Value(entity, name);
        if (value == null) return null;
        if (value is string text) return text;
        if (value is IEnumerable values)
            return values.Cast<object>().Select(v => v as string ?? throw CliException.Contract("Non-string recipient value.")).ToArray();
        throw CliException.Contract("Unexpected recipients property: " + name + ".");
    }

    private OutputRow Identity(object entity, DeviceProfile device, string kind, string timeProperty)
    {
        long id = Number(entity, "Id");
        DateTimeOffset time = Time(entity, timeProperty).ToUniversalTime();
        return new OutputRow(time.UtcTicks, device.Id, kind, id, new Dictionary<string, object?> {
            { "id", Id(id) }, { "device_id", device.Id }, { "kind", kind },
            { "timestamp_utc", time.ToString("o", CultureInfo.InvariantCulture) },
            { "timestamp_filetime", Id(WindowsTime.ToFileTime(time)) }
        });
    }

    private IEnumerable<object> All(object table, ReadOnlyDatabase database)
    {
        var (name, key) = catalog.TableNames(table);
        if (!database.HasTable(name)) throw new CliException("unsupported_schema", "A library-required table is absent: " + name);
        // A generic, key-only cursor avoids QueryActivitySince's fixed 5000-row
        // cap and timestamp-tie loss. Content SELECTs/mapping come from the library.
        foreach (long[] ids in database.KeyBatches(name, key))
            foreach (object entity in catalog.CallRows(table, "GetEntitiesFromIds", ids)) yield return entity;
    }

    public IReadOnlyList<OutputRow> Messages(DeviceProfile device, Options options)
    {
        using var database = new ReadOnlyDatabase(device.Database("phone"));
        var newest = new NewestRows(options.Limit);
        foreach (var spec in TableSpec.Messages.Where(s => options.Kind == null || options.Kind == s.Kind))
        {
            object table = catalog.CreateTable(spec, database);
            string tableName = catalog.TableNames(table).Table;
            if (!database.HasTable(tableName))
            {
                if (spec.Kind == "sms" || options.Kind != null) throw new CliException("unsupported_schema", "Required message table is absent: " + tableName);
                continue;
            }
            IEnumerable<object> entities = options.Thread.HasValue ? catalog.CallRows(table, "GetMessagesInThread", options.Thread.Value) : All(table, database);
            foreach (object entity in entities)
            {
                var row = Identity(entity, device, spec.Kind, "Timestamp");
                if (options.Since.HasValue && row.Ticks < options.Since.Value.UtcTicks) continue;
                long thread = Number(entity, "ThreadId");
                if (options.Thread.HasValue && thread != options.Thread.Value) continue;
                var data = row.Data;
                data["thread_id"] = Id(thread);
                data["body"] = Text(entity, "Body");
                data["subject"] = Text(entity, "Subject");
                data["from_address"] = Text(entity, "FromAddress");
                data["to_address"] = Text(entity, "ToAddress");
                data["to_addresses"] = Strings(entity, "ToAddresses");
                data["mailbox"] = Label(entity, "MailboxType");
                data["read_status"] = Label(entity, "Status");
                data["pc_status"] = Label(entity, "PcStatus");
                if (spec.Kind == "rcs_filetransfer")
                {
                    data["name"] = Text(entity, "Name");
                    data["content_type"] = Text(entity, "ContentType");
                    data["size_bytes"] = Number(entity, "Size");
                    data["cached_blob_available"] = Value(entity, "BlobStream") != null;
                }
                newest.Add(row);
            }
        }
        // Read MMS text/metadata only for retained messages, in the same snapshot.
        object? partsTable = null;
        foreach (var row in newest.Items.Where(r => r.Kind == "mms"))
        {
            partsTable ??= catalog.CreateTable(TableSpec.MmsParts, database);
            var entities = catalog.CallRows(partsTable, "GetPartsForMessage", row.Id)
                .OrderBy(p => Number(p, "SequenceNumber", false)).ThenBy(p => Number(p, "Id")).ToArray();
            var parts = entities.Select(p => new Dictionary<string, object?> {
                { "id", Id(Number(p, "Id")) }, { "sequence", Number(p, "SequenceNumber", false) },
                { "content_type", Text(p, "ContentType") }, { "text", Text(p, "Text") },
                { "name", Text(p, "Name") }, { "size_bytes", Number(p, "Size", false) },
                { "cached_blob_available", Value(p, "BlobStream") != null }
            }).ToArray();
            row.Data["parts"] = parts;
            var texts = parts.Where(p => (string?)p["content_type"] == "text/plain" && p["text"] is string).Select(p => (string)p["text"]!).ToArray();
            row.Data["body"] = texts.Length == 0 ? null : string.Join("\n", texts);
        }
        return newest.Items;
    }

    public IReadOnlyList<OutputRow> Conversations(DeviceProfile device, Options options)
    {
        using var database = new ReadOnlyDatabase(device.Database("phone"));
        var newest = new NewestRows(options.Limit);
        foreach (var spec in TableSpec.Conversations)
        {
            object table = catalog.CreateTable(spec, database);
            if (spec.Kind == "rcs" && !database.HasTable(catalog.TableNames(table).Table)) continue;
            foreach (object entity in All(table, database))
            {
                var row = Identity(entity, device, spec.Kind, "LatestTimestamp");
                if (options.Since.HasValue && row.Ticks < options.Since.Value.UtcTicks) continue;
                // RCS's entity ThreadId is the SMS/MMS fallback, not its primary
                // RCS thread key. Expose both without silently conflating them.
                row.Data["thread_id"] = Id(row.Id);
                row.Data["sms_mms_thread_id"] = Id(Number(entity, "ThreadId"));
                row.Data["recipients"] = Strings(entity, "RecipientAddresses");
                row.Data["message_count"] = Number(entity, "MessageCount");
                row.Data["unread_count"] = Number(entity, "UnreadCount");
                row.Data["phone_unread_count"] = Number(entity, "PhoneUnreadCount", false);
                newest.Add(row);
            }
        }
        return newest.Items;
    }

    public IReadOnlyList<OutputRow> Notifications(DeviceProfile device, Options options)
    {
        using var database = new ReadOnlyDatabase(device.Database("notifications"));
        object table = catalog.CreateTable(TableSpec.Notifications, database);
        var newest = new NewestRows(options.Limit);
        foreach (object entity in All(table, database))
        {
            var row = Identity(entity, device, "notification", "PostTime");
            if (options.Since.HasValue && row.Ticks < options.Since.Value.UtcTicks) continue;
            string? package = Text(entity, "PackageName");
            if (options.App != null && package != options.App) continue;
            row.Data["notification_id"] = Text(entity, "NotificationId");
            row.Data["package_name"] = package;
            row.Data["app_name"] = Text(entity, "AppName");
            row.Data["title"] = Text(entity, "Title");
            row.Data["text"] = Text(entity, "Description");
            row.Data["pinned_state"] = Label(entity, "PinnedState");
            if (options.IncludePayload)
            {
                string raw = Text(entity, "Json") ?? "";
                try { row.Data["payload"] = JsonSerializer.Deserialize<JsonElement>(raw); }
                catch (JsonException) { row.Data["payload_error"] = "invalid_json"; row.Data["payload_raw"] = raw; }
            }
            newest.Add(row);
        }
        return newest.Items;
    }
}
