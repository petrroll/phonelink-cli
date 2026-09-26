# Phone Link CLI

Experimental **Windows x64 / .NET 10** CLI for reading Phone Link's locally synced
messages, conversations, and notifications as JSON—through its installed managed
libraries, without UI automation.

> **AI / Astra generated. Uses non-public APIs and is likely to break.**
> This project was generated with AI (Astra). It is not a Microsoft product,
> supported integration, or stable SDK. Phone Link updates can break assembly
> loading, interfaces, queries, or output semantics. Treat it as experimental,
> review the code, and do not rely on it for critical workflows.

**Library-backed does not mean live-phone access.** The invoked library methods
read Phone Link's **local cache**. This tool cannot fetch uncached history, force
synchronization, send/reply, dismiss notifications, or mark messages read.

## Requirements

- Windows x64 and the **.NET 10 runtime** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)).
- Phone Link installed for the current Windows user, already paired and synced.
- A compatible Phone Link build. Locally validated with **1.26091.112.0** on
  Windows 11 build 26310 and .NET **10.0.12**. Other builds may not work.

No administrator rights or additional phone app is required. Run as the same
Windows user who owns the Phone Link pairing. iPhone cache behavior is **not
validated**; the tested cache schemas are Android-style.

## Quick start

Build from source below, or use the `phonelink-win-x64` ZIP produced by the
repository's Windows CI/release. The ZIP contains our executable, README, and MIT
license—not Microsoft Phone Link DLLs. The framework-dependent `.exe` needs the
.NET 10 runtime but does not need an SDK or PowerShell to run.

In Windows PowerShell, from the executable's directory:

```powershell
.\phonelink.exe doctor --pretty
.\phonelink.exe devices --pretty
.\phonelink.exe messages --all-devices --limit 20 --pretty
.\phonelink.exe notifications --all-devices --limit 20 --pretty
.\phonelink.exe conversations --all-devices --limit 20 --pretty
```

To select a phone, replace `--all-devices` with `--device DEVICE_ID`. IDs come
from `devices`; **unique prefixes work**. Old pairings can leave stale cache
profiles. Multiple profiles require explicit selection rather than guessing
which phone is active.

From WSL, run the Windows binary directly:

```bash
./dist/win-x64/phonelink.exe messages --all-devices --limit 20
```

It still executes on **Windows** and uses Windows database locking, not Linux
SQLite on `/mnt/c`. You can copy the executable onto a Windows drive and run
it without WSL afterward.

## Commands

```text
phonelink devices
phonelink doctor
phonelink messages      [--device ID | --all-devices] [--limit N]
                        [--since ISO8601] [--thread ID] [--kind KIND]
phonelink notifications [--device ID | --all-devices] [--limit N]
                        [--since ISO8601] [--app PACKAGE] [--include-payload]
phonelink conversations [--device ID | --all-devices] [--limit N]
                        [--since ISO8601]
phonelink version
```

Cache commands accept `--pretty`, `--cache-root PATH`, and `--library-path PATH`.

- `devices` emits IDs, database directories, sizes, and DB/WAL modification times,
  without message content. Modification times are **not** connection/sync status.
- `doctor` reports the backend, runtime, installed library version, available
  read methods, and cache metadata. It does not print message/notification text.
- `messages` includes `sms`, `mms`, `rcs_chat`, and `rcs_filetransfer` where cached.
  `--kind` selects one. MMS bodies combine cached `text/plain` parts in sequence
  order; SMIL is excluded by the library. Binary attachments are **not exported**:
  only names, types, sizes, and cached-blob availability are included.
- `notifications` uses **Phone Link's cached-notification library**, not the
  Windows notification-center API. It exposes the library's title/description
  and app name. `--include-payload` also exposes the cached JSON payload.
  `--app` is an exact Android package name, e.g. `com.whatsapp`.
- `conversations` includes recipients and counts. For RCS, `thread_id` is the
  primary RCS key; `sms_mms_thread_id` preserves the library's fallback thread ID.
  Conversation counts may exceed the number of messages still in the cache.

Data commands emit **one JSON array, newest first**. `--limit` defaults to 50,
accepts 1–10000, and applies globally across selected devices/kinds. Ties have a
stable device/kind/ID ordering. `--since` is inclusive and requires a timezone,
for example `2026-09-01T00:00:00Z` or `2026-09-01T02:00:00+02:00`.

IDs and `timestamp_filetime` are strings to preserve 64-bit precision in
JavaScript. `timestamp_utc` is ISO 8601. FILETIME is 100 ns ticks since 1601;
zero/sentinel dates follow the library's interpretation. Message identity is
`(device_id, kind, id)`, not `id` alone. Enum labels such as `mailbox`,
`read_status`, and `pinned_state` come from the installed library and may change.

Success exits **0**. Cache/library/runtime errors exit **1**; argument errors
exit **2**. Errors are JSON on stderr:

```json
{"error":{"code":"unsupported_library","message":"..."}}
```

Nothing is written to stdout until all selected profiles succeed. Raw vendor
exception messages/stack traces are suppressed because they can contain private
payloads. Incompatible libraries, schemas, or malformed data can fail a whole
command; there is **no silent raw-SQL fallback**.

### Programmatic use

```python
import json
import subprocess

result = subprocess.run(
    [r".\phonelink.exe", "messages", "--all-devices", "--limit", "100"],
    capture_output=True, encoding="utf-8",
)
if result.returncode:
    raise RuntimeError(json.loads(result.stderr)["error"]["message"])
messages = json.loads(result.stdout)
```

For WSL Python use `./dist/win-x64/phonelink.exe`. In Windows PowerShell 5.1,
set `[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)`
if a legacy console code page garbles native UTF-8 output.

There is no HTTP listener or event subscription. Repeated CLI calls can poll
snapshots, but `--since` is **not** a lossless change cursor: updates may retain
old timestamps, rows may disappear, and retention/limits can omit data.

## How it works / safety boundaries

1. Windows package APIs locate the current user's registered Phone Link package.
2. An isolated assembly load context loads its installed managed libraries.
3. The CLI instantiates **table classes**, supplying its own read-only implementations
   of the private `ISqliteConnection` / `ISqliteStatement` interfaces.
4. Table/key metadata comes from the library. The adapter enumerates integer keys
   in bounded batches, then calls the library's **`GetEntitiesFromIds`** methods
   for content queries and entity mapping. This avoids the 5000-row cap in the
   library's `QueryActivitySince` method and avoids losing timestamp ties.
5. The newest matching rows are retained; MMS part queries use the library too.

All database access uses Windows' in-box `winsqlite3.dll`,
`SQLITE_OPEN_READONLY`, a 3-second busy timeout, and read transactions with normal
WAL handling. Only SELECT/WITH queries are admitted and prepared statements must
also pass SQLite's read-only check. Writes, ATTACH, migrations, arbitrary PRAGMAs,
transaction-creation callbacks, and attachment-stream reads are rejected.

**We do not invoke Phone Link's normal database constructors.** Inspection showed
that they can migrate/encrypt files and perform destructive corruption recovery.
No Phone Link process injection, caller-identity spoofing, ACL changes, or UI
control is used. SQLite can still use shared-memory/lock bookkeeping; read-only
means no modification of cached message/notification records. Snapshots are per
DB/device, not globally synchronized across every phone.

`--cache-root` points to a `LocalCache\Indexed` directory containing
`<device-id>\System\Database\*.db`. Do not pass a single DB file or an inconsistent
copy that omits its active WAL. Do not use `immutable=1` or Linux SQLite against
the live Windows cache.

**Only load trusted libraries.** `--library-path` exists for diagnostics/testing;
loading a .NET DLL can execute module initializers. The read-only adapter is not
a sandbox for malicious DLLs. The default uses the locally registered Microsoft
installation. No Microsoft binaries or decompiled implementations are distributed.

WhatsApp/Signal notification previews are not access to those apps' full chat
archives. RCS and attachment availability are limited to what Phone Link cached.
The CLI never initiates a sync. See [API investigation notes](docs/api-notes.md)
for the tested WinRT/COM/Bluetooth alternatives.

## Build and test

Requires the **.NET 10 SDK**. No Windows SDK, NuGet runtime libraries, or compile-time
Phone Link references are needed. Runtime reflection/`DispatchProxy` means
**trimming and Native AOT are not supported**.

Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

WSL (Linux .NET SDK, Windows runtime/interop for running the tests):

```bash
./scripts/build.sh
./scripts/test.sh
```

Or build directly:

```text
dotnet publish src/PhoneLink.Cli -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/win-x64
```

The output is `dist/win-x64/phonelink.exe`. `ExecutionPolicy Bypass` affects only
the shown script invocation, not the system policy.

The regression suite uses **original synthetic vendor test doubles** and temporary
Windows SQLite databases—not Microsoft's DLLs or your Phone Link data. It checks
SMS/MMS/RCS, notification payloads, >5000 rows and timestamp ties, 64-bit IDs,
Unicode/NULs, filtering, stale profiles, live WAL-only commits, locking, read-only
files, blocked writes, redacted failures, and unchanged DB hashes. Windows CI
runs the same tests. Local live validation additionally compared SMS/MMS text to
the synced caches without printing or saving message content. Nonempty RCS
messages were tested synthetically, not on the linked phones.

## Privacy and license

Output may contain phone numbers, private messages, and one-time codes. Do not
commit exports, send them to remote logs, or wrap the CLI in an unauthenticated
server. When reporting bugs, share versions and sanitized error codes—not real
DBs, message payloads, or unsanitized `doctor` paths/profile IDs.

This repository's original code is licensed under **[MIT](LICENSE)**, provided
**AS IS**, with no warranty. That license does not relicense Microsoft Phone Link,
Windows, or their libraries. Those components remain subject to their own terms.
