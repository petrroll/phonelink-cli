# Security considerations and reporting

This is experimental **AI / Astra-generated** software using **non-public Phone
Link APIs**. Automated tests and a successful local read are not a security
audit. Interfaces, storage formats, and dependency behavior can change after an
update. There is no security-support SLA or warranty; see the MIT license.

## Data flow and trust boundary

The CLI runs as the Windows user who launches it. It locates that user's
registered Phone Link package, loads its managed libraries, reads already-synced
local caches through a read-only SQLite adapter, and writes JSON to stdout.

That can expose SMS/MMS/RCS text, sender/recipient numbers, notification previews,
verification/login codes, attachment metadata, and device/timing information.
Access to previously synced data does not necessarily require the phone to be
currently connected or unlocked, or Phone Link's window to be visible. Stale
profiles, exports, and backups can outlive the current pairing/session. Do not
assume that unpairing removes every copy.

This does **not** assert that every build stores readable plaintext or that the
CLI defeats encryption. The effective Windows user's permissions, the package's
storage protections, and interface compatibility still apply. Encrypted or
unsupported caches may fail to read. Successful output, however, is plaintext
JSON and must be protected accordingly.

**The Windows account/process boundary is the relevant boundary, not the CLI's
read-only label.** The CLI adds no password, per-device authorization, or new
phone consent dialog. It must only be used with data you are authorized to read.
A malicious process already able to access the same data under the same account,
or an administrator/compromised OS, is not isolated by this tool. Syncing SMS MFA
codes to a PC also means they are not independent of a compromised PC session;
consider phishing-resistant authentication for sensitive accounts.

## What read-only protects—and what it does not

The storage adapter opens SQLite with `SQLITE_OPEN_READONLY`, uses ordinary
Windows locking/WAL handling, rejects non-query SQL and non-read-only prepared
statements, and does not invoke the application's migrating database constructors.
The CLI does not send/reply, mark messages read, dismiss notifications, force
sync, inject code into Phone Link, spoof caller identities, or change ACLs.
SQLite can still perform lock/shared-memory bookkeeping. These controls aim to
prevent modification of message/notification records **through this adapter**.

They do not:

- prevent authorized reads from disclosing private data;
- sandbox .NET module initializers, DLL code, or native dependencies;
- stop loaded code from using its own file/network/process APIs;
- protect against malicious/tampered dependencies or a compromised host;
- guarantee memory zeroization, absence of OS paging/crash dumps, or downstream
  log/export retention;
- turn command filters, result limits, or library load contexts into security
  boundaries. Libraries may read more rows into memory than the CLI emits.

## Installation/dependency trust

Automatic discovery uses current-user Windows AppModel package registration and
asks Windows for the effective package directory. It does not guess a drive,
scan WindowsApps, search other users, or search the working directory/`PATH` for
an installation. Only matching/neutral main packages with required DLLs are
considered. Store-update races can still cause a command to fail; retry after the
update completes rather than copying arbitrary DLLs into the executable folder.

**Registration is location/provenance information, not an independent integrity
verification by this CLI.** It relies on the Windows installation/ACL trust model;
it does not verify Authenticode signatures or hashes of every loaded DLL. A
mutable/external deployment or a folder writable by others deserves particular
caution. `doctor` reports the resolved identity/path and that the CLI has not
performed signature verification.

`--library-path` deliberately overrides registration for trusted diagnostics/test
fixtures. Supplying an untrusted folder is equivalent to running untrusted code,
including when only running `doctor`. Do not accept this argument from untrusted
input or point it at an untrusted share/download. Run without administrator rights.

Non-framework managed dependency lookup is restricted to the chosen directory.
The CLI's direct native dependency resolver checks that directory, then Windows
System32, and rejects paths and working-directory/`PATH` fallback. This is defense
in depth, not a complete DLL-hijacking defense: native libraries can perform
additional loading themselves and all loaded code shares process privileges.

Release executables are not Authenticode-signed by this project. SHA-256 sums help
check downloaded bytes, but do not independently authenticate a compromised
repository/release. Review/build the source and obtain artifacts from a source
you trust. The releases contain no Microsoft Phone Link DLLs or private caches.

## Handling output and integrations

- Keep exports out of source control, shared folders, remote telemetry, and agent
  logs unless you intentionally authorize that disclosure. Use appropriate file
  ACLs and retention policies; `.gitignore` is not access control.
- Treat message bodies, URLs, titles, and notification action metadata as
  **untrusted data**. Never interpolate them into shell commands or treat them as
  AI-agent instructions. The CLI does not execute notification actions. Protect
  downstream renderers and workflows against injection and require confirmation
  for actions with side effects.
- `--all-devices` includes stale cached profiles. `--include-payload` exports extra
  fields. Select only what is needed, while recognizing this is data minimization,
  not a permission boundary.
- Even metadata-only `devices`/`doctor` output can disclose account paths, package
  paths, device IDs, timing, or installation details. Sanitize it before sharing.
- Do not put an unauthenticated HTTP, MCP, or IPC service around the executable.
  Wrappers must authenticate callers, authorize data access, limit exposure, and
  avoid logging payloads. Loopback binding does not by itself make a service safe
  from browsers, other local users/processes, or other agents.

The CLI does not intentionally create message exports or network endpoints, and
suppresses raw vendor exception text to reduce accidental leaks. This is not a
promise that third-party code, the OS, or downstream consumers never persist or
transmit data.

## Reporting

Never attach real phone databases, messages, OTPs, tokens, memory dumps, or
unsanitized paths/profile IDs to an issue. Provide the CLI/Phone Link versions,
Windows build/architecture, a sanitized error code, and preferably a minimal
synthetic reproduction.

For a potential vulnerability with sensitive details, use private reporting if
available, or open a sanitized high-level issue to arrange a private follow-up
with the maintainer. Do not publish private data as proof. The synthetic tests
are safe starting points for reproductions; they do not read user Phone Link data.
