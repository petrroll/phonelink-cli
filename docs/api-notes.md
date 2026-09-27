# API investigation summary

These observations were made on Windows build 26310 with Phone Link
1.26091.112.0 and CrossDevice 1.26091.56.0. Private interfaces can change; none
of the findings imply a supported general Phone Link SDK.

## The backend implemented here

The installed `YourPhone.Messaging.Managed.dll` exposes table/query classes,
including `SmsTable`, `MmsTable`, `RcsChatTable`, and `MmsPartTable`.
`YourPhone.Notifications.Managed.dll` exposes `NotificationsTable`.
Their query methods **read the local cache** and map rows into typed entities.

Actual standalone calls returned SMS bodies and MMS metadata, and subsequent
CLI validation covered messages, conversations, and cached notifications across
three profiles. The CLI now supplies a read-only storage adapter, obtains
`TableName`/`PrimaryKeyName` from each library table, enumerates keys, and calls
`GetEntitiesFromIds` in batches. It does not hard-code the vendor's content
SELECTs or invoke its migrating database constructors.

A proof using `QueryActivitySince` exposed that method's fixed 5000-row limit.
The production CLI uses bounded ID batches for all message filters, including
`--thread`; it does not call the unbounded `GetMessagesInThread` API or paginate
by timestamps. Large histories and tied timestamps are covered by synthetic tests.

The normal `YourPhone.Utilities.Database.SqliteConnection` constructor can
finalize encrypted copies and delete/recreate corrupt files. Do not blindly
instantiate it against live data. Its constructor is not used by this project.

## Adapter conventions and compatibility limits

The adapter's semantics are based on inspection of Phone Link 1.26091.112.0,
not on a public contract. It converts stored FILETIME values (including valid
negative values) to UTC; out-of-range values fail explicitly. Null text becomes
an empty string, numeric reads of null fail, reset clears bindings, and lazy blob
metadata uses the library's `<column>_length` projection. The requirement that
all SQL parameters be bound is an additional strictness check.

Synthetic doubles exercise these conventions but cannot prove that a future
vendor build still follows them. The first adapter failure is retained so a
vendor method swallowing that exception cannot turn it into a successful empty
result. Raw vendor/SQLite diagnostics are not exposed in CLI errors.

## Windows notification API — separately tested, not this backend

`Windows.UI.Notifications.Management.UserNotificationListener` was called
successfully and returned Phone Link Windows toast notifications with text.
`GetAccessStatus()` was already `Allowed` in that test; no permission was changed.
Microsoft documents a capability/consent model, so this is not a guarantee for
every process or Windows build.

It reads **Windows notification-center entries**, not Phone Link's full mirrored
notification collection or SMS conversations. The CLI's `notifications` command
instead uses the cache-backed managed library described above.

Documentation: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener

`ChatMessageManager.RequestStoreAsync()` also opened a Windows chat store, but
its first message batch was empty; it did not expose the Phone Link SMS history.

## Private COM interfaces — real, but restricted

CrossDevice registers `Windows.Internal.System.ConnectedDevices.PhoneLink`
interfaces. A standalone unpackaged test process successfully activated the
Phone Link extension COM class, but **`IsSupported`, `GetDefault`, and a direct
`GetDeviceAsync` all returned `0x80070005` (E_ACCESSDENIED)**.

Inspection showed an explicit caller-package allowlist for the actual Phone
Link and CrossDevice packages. Changing C# to Rust does not change that check.
An internal message-search provider additionally requires the shell host caller.
No checks were patched or bypassed.

The AppProxy COM interface also has a promising-looking `QueryDatabase` method,
but it only supports specific hosted-app/window metadata operations, not SMS or
arbitrary SQL. Its COM activation was denied in the test. Its subsequent
getter/query methods were therefore not invoked.

A packaged `PhoneAgentMcpServer.exe` and MCP configuration were found, but direct
process creation was denied and the static tool list only advertised opening the
phone screen. This did not establish a usable external message-reading MCP API.

## Bluetooth MAP — possible direct-phone route, not validated end-to-end

`Microsoft.Internal.Bluetooth.Map.dll` contains `MapClientManager.OpenAsync`,
`MapClient.GetMessagesListingAsync`, and `MapClient.GetMessageAsync`.
Their implementations send OBEX GET requests over Bluetooth RFCOMM, not SQL.
Sending messages and changing read state are separate operations.

Both cached and fresh Windows service discovery found no usable MAP Message
Access Server (`0x1132`) endpoint in the tested setup. **No live MAP message fetch
was demonstrated.** It would require a connected/paired phone exposing that
service and granting message access. Wi-Fi Phone Link pairing alone is not enough;
coverage and history limits depend on the phone. No Bluetooth pairing, message
sending, read-state change, or notification registration was performed.

These notes summarize findings; the repository intentionally excludes downloaded
Microsoft binaries, decompiled implementation files, and all private message data.
