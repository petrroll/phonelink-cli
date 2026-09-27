# Phone Link CLI

Experimental **Windows x64 / .NET 10** CLI for reading Phone Link's synced
messages, conversations, and notifications as JSON through its installed managed
libraries—without UI automation.

> **AI / Astra generated. Uses non-public APIs and is likely to break.**
> Not a Microsoft product, supported integration, or stable SDK. Phone Link
> updates can break loading, interfaces, queries, and output semantics. Review
> the code and do not rely on it for critical workflows.

> **Read-only does not mean confidential or sandboxed.** Successful reads expose
> private messages and possibly login/verification codes as plaintext JSON.
> Loaded DLLs and downstream consumers must be trusted. See [SECURITY.md](SECURITY.md).

**This is cache-backed, not a live-phone API.** It cannot fetch uncached history,
force synchronization, send/reply, dismiss notifications, or mark messages read.

## Requirements and quick start

- Windows x64, the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0),
  and a compatible Phone Link installation for the current Windows user.
- Phone Link must already be paired and have synced the requested data.
- Validated locally against Phone Link **1.26091.112.0**, Windows 11 build 26310,
  and .NET **10.0.12**. Other builds may not work. iPhone caches are **not validated**.

Use a [tagged release](https://github.com/petrroll/phonelink-cli/releases), or build
below. CI publishes artifacts only from `main` and version tags, not pull-request
runs. The ZIP contains `phonelink.exe`, README, security notes, and the MIT license;
SHA-256 sums accompany it. No Microsoft Phone Link DLLs or user data are bundled.
The executable needs the runtime, but not an SDK or PowerShell.

```powershell
.\phonelink.exe doctor --pretty
.\phonelink.exe devices --pretty
.\phonelink.exe messages --all-devices --limit 20 --pretty
.\phonelink.exe notifications --all-devices --limit 20 --pretty
.\phonelink.exe conversations --all-devices --limit 20 --pretty
```

Run **without administrator rights**, as the Windows user who owns the pairing.
To select a phone, use `--device DEVICE_ID`; unique prefixes work. A sole eligible
cached profile is auto-selected **even if stale**. Multiple eligible profiles
require explicit selection. `--all-devices` reads profiles containing the command's
DB (`phone.db` for messages/conversations, `notifications.db` for notifications).
An explicitly selected profile missing that DB fails rather than being skipped.
Cache presence does not prove an active pairing.

From WSL, launch the Windows process directly:

```bash
./dist/win-x64/phonelink.exe messages --all-devices --limit 20
```

It uses Windows locking, not Linux SQLite on `/mnt/c`. The `.exe` can also be
copied onto a Windows drive and run without WSL.

## Commands and JSON interface

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

Cache commands also accept `--pretty`, `--cache-root PATH`, and
`--library-path TRUSTED_DIRECTORY`.

| Command | Data |
|---|---|
| `devices` | All cached profile IDs, DB paths/sizes, and DB/WAL modification times; no message content. Times are not sync/connection status. |
| `doctor` | Runtime, resolved installation, package identity/version/architecture, read-method availability, and cache metadata. It **loads DLLs** but does not print messages. |
| `messages` | `sms`, `mms`, `rcs_chat`, `rcs_filetransfer`; `--kind` selects one. MMS body text joins cached `text/plain` parts in sequence order. Binary attachments are not exported. |
| `notifications` | Phone Link's **cached-notification library**, not Windows notification-center entries. App name, title/description, and optional JSON `payload`. `--app` matches an exact Android package name. |
| `conversations` | Recipients and counts. RCS `thread_id` is its primary key; `sms_mms_thread_id` is the library's fallback key. Counts may exceed retained message rows. |

Data commands return **one JSON array, newest first**, with stable device/kind/ID
ordering for ties. `--limit` defaults to 50, accepts 1–10000, and is global across
selected profiles/kinds. It limits output, **not** scan duration. `--thread` filters
the message entity's thread ID; it uses the same bounded key-batch path as other
filters, not an unbounded vendor thread reader. Nonempty RCS cases are tested
synthetically, not against the linked phones.

`--since` is inclusive and requires a timezone, e.g. `2026-09-01T00:00:00Z`.
IDs and `timestamp_filetime` are strings to preserve 64-bit precision in JavaScript.
`timestamp_utc` is ISO 8601. The adapter interprets timestamps as Windows FILETIME
(100 ns ticks since 1601); zero is the epoch and invalid ranges fail explicitly.
Message identity is `(device_id, kind, id)`, not `id` alone. Enum names such as
`mailbox`, `read_status`, and `pinned_state` come from the installed library and
may change. Missing optional text/list fields are null; optional numeric counts,
part sizes, and sequence values default to zero.

Success exits **0**; runtime/cache/library errors **1**; argument errors **2**.
Errors are JSON on stderr, with no partial success on stdout:

```json
{"error":{"code":"unsupported_library","message":"..."}}
```

Raw vendor and SQLite diagnostic text is suppressed because it may expose private
content. Adapter failures remain errors even if a vendor method catches them.
There is **no silent raw-SQL fallback**. The default JSON encoder escapes non-ASCII
characters; output is valid UTF-8/ASCII without changing the parent console code page.

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

For WSL Python use `./dist/win-x64/phonelink.exe`. Repeated calls can poll snapshots,
but `--since` is **not** a lossless change cursor: timestamps may remain unchanged,
rows can disappear, and retention/limits can omit data. No server/event subscription
is included.

## Discovery and implementation

There is **no hard-coded drive or WindowsApps path**. Each run that needs libraries:

1. Uses current-user `GetPackagesByPackageFamily` registration for
   `Microsoft.YourPhone_8wekyb3d8bbwe`.
2. Asks `GetPackagePathByFullName2(PackagePathType_Effective)` for the actual
   effective external/mutable/install directory, including relocated volumes.
   If that API is absent, it uses `GetPackagePathByFullName`.
3. Skips resource/bundle, incompatible-architecture, or incomplete candidates and
   chooses the newest registered package version. Update races are retried/skipped;
   there is no scan of unrelated folders, other users, the working directory, or `PATH`.

Cache discovery separately uses Windows' `LocalApplicationData` known folder.
`--cache-root` overrides the `LocalCache\Indexed` root containing
`<device-id>\System\Database\*.db`, not a single DB file. In WSL, explicit overrides
must be Windows paths (`wslpath -w` converts them). `--library-path` is an explicit
**unverified code-loading override**, not just a data directory. `doctor` reports
paths and that this CLI does not independently verify DLL signatures.

A separate assembly load context manages dependencies, not permissions. Table
classes receive our private-interface adapters; the CLI enumerates integer keys
in batches and calls the library's `GetEntitiesFromIds` for content queries and
mapping. This avoids the fixed cap/timestamp-tie issue in `QueryActivitySince`.
MMS part queries also use the library. See [API/adapter notes](docs/api-notes.md).

The storage boundary uses in-box `winsqlite3.dll`, `SQLITE_OPEN_READONLY`, normal
Windows locking/WAL handling, and a read transaction per DB. Vendor statements
must be single SELECT/WITH queries and pass SQLite's read-only check. Writes,
ATTACH, arbitrary PRAGMAs, and attachment-stream reads are rejected. We do **not**
invoke the app's migration-capable database constructors or bypass caller checks.

### Concurrent access: read-only is not lock-free

- In normal WAL mode, writers can commit while the CLI reads a consistent snapshot.
  Later commits become visible on the next invocation.
- Long readers can delay WAL checkpoints/truncation. Rollback-journal readers can
  delay writer commits. Open handles can conflict with replacement/migration.
- SQLite waits up to **3 seconds per contended operation**, not per command; some
  errors fail immediately. Transactions span DB processing and MMS enrichment.
- Avoid tight polling; retry transient contention with backoff. Do not disable
  locks, delete WAL/SHM files, or copy only a live `.db` to work around busy errors.

There is no zero-interference guarantee. Deterministic tests cover simultaneous
WAL commits, snapshot/key/MMS consistency, and rollback-mode writer blocking.

## Security

**Synced phone data is PC data**, including potentially SMS MFA codes. Processes
with access to those caches may read them without another phone unlock. Do not
rely on UI visibility, a disconnected phone, or `--limit` as a security boundary.

Load only trusted libraries and do not run elevated. Loaded code has process
privileges outside the adapter; neither the assembly context nor read-only SQL is
a sandbox. Our dependency resolver restricts package/framework/System32 lookup,
but this is not independent DLL integrity verification or complete hijack protection.

Protect plaintext exports, logs, backups, and agent inputs. Treat messages and
notification payloads as **untrusted data**, not commands or AI instructions.
Do not expose an unauthenticated HTTP/MCP/IPC wrapper. The full threat model,
MFA implications, retention/loader limitations, and reporting guidance are in
**[SECURITY.md](SECURITY.md)**.

## Build, test, and review

Builds need the **.NET 10 SDK**, not a Windows SDK, Phone Link compile-time
references, or NuGet runtime libraries. Reflection/`DispatchProxy` means trimming
and Native AOT are unsupported.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

```bash
# WSL: Linux SDK to build; Windows runtime/interop to execute tests.
./scripts/build.sh
./scripts/test.sh
```

Output: `dist/win-x64/phonelink.exe`. The shown execution-policy override is only
for that script invocation. A direct equivalent is:

```text
dotnet publish src/PhoneLink.Cli -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/win-x64
```

Tests use **original synthetic vendor doubles and temporary Windows DBs**, never
personal phone data. They cover batching/large histories, filters, Unicode,
64-bit IDs, privacy failures, partial profiles, discovery, DLL lookup, and locking.
They prove our adapter's internal contract—not future vendor compatibility. Local
live checks are separate and do not save message content. See the
[whole-code review summary](docs/review.md) for the independent Opus 5.5/Astra review.

## License

Original code is **[MIT](LICENSE)**, provided **AS IS**, without warranty. Microsoft
Phone Link, Windows, and their libraries remain subject to their own terms; this
license does not relicense them. No vendor binaries or decompiled implementation
files are included in this repository.
