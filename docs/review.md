# Whole-code review record

Two independent static reviews covered **all 36 tracked files** at commit
`a6321ac`: production code, original synthetic test doubles/tests, build scripts,
CI, and documentation.

- **Opus 5.5:** `github-copilot/claude-opus-5.5`
- **Astra:** `github-copilot/gpt-6-astra`

Both were given a code-only source bundle with tools and session persistence
disabled. They received no live phone data, vendor binaries, credentials, or
private validation details. Both reported **no blocking issue**, while identifying
concrete improvements. Both models then reviewed the revised 37-file source tree
and diff in a second pass, again reporting no blockers. Minor documentation and
checksum-format suggestions were addressed. This is AI-assisted static review,
not a security audit or independent execution of the tests.

## Changes made after review

- Removed the separate bulk thread reader. Every message filter now uses the same
  bounded ID-batch path, reducing private API dependencies and memory variability.
- Removed duplicate schema/file checks and unused table-spec metadata. Kept native
  allocation code explicit rather than introducing unsafe code just to shorten it.
- Select eligible profiles per command; a missing unrelated DB no longer breaks
  `--all-devices`. Explicit selection of an incompatible profile still fails.
- Redact raw SQLite diagnostics as well as vendor exception text. Retain the first
  adapter failure so vendor exception swallowing cannot produce an empty success.
- Restrict BEGIN to the adapter's own transaction startup and report invalid
  FILETIME ranges explicitly.
- Stop changing the parent console's output code page. JSON uses the default
  non-ASCII escaping, and help is ASCII. Version text comes from assembly metadata.
- Correct stale-profile auto-selection wording and reduce README/security repetition.
- Wait for timed-out test children to terminate before removing their fixtures;
  isolate multi-profile tests from the shared single-profile setup.
- Generate CI checksums, publish artifacts only from main/version tags, and pin
  official action revisions (checking their versions, runtimes, and required inputs).

Focused regression tests cover these changes. Tests use synthetic data only;
private live checks remain separate and are never added to fixtures or Git.

## Deliberate decisions / review limits

- Kept per-database snapshots, native read-only/tail checks, key pagination,
  bounded newest-row retention, dependency lookup restrictions, and package API
  retry/selection logic. These address demonstrated requirements rather than
  speculative extensibility.
- Did not change the adapter's null/reset/blob/date semantics to generic SQLite
  defaults. They model the inspected Phone Link build and are documented in
  [API notes](api-notes.md); synthetic tests cannot guarantee future compatibility.
- One review questioned external-location precedence for `PackagePathType_Effective`.
  The [current Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/ne-appmodel-packagepathtype)
  explicitly lists user-external, machine-external, mutable, then install locations.
  The implementation was retained. Path-preservation tests still do not establish
  that every possible Phone Link deployment is supported.
- No caching framework, parallel query engine, extra runtime dependency, or language
  rewrite was introduced. New defensive state is limited to the per-DB error latch.
