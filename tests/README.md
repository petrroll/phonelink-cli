# Test-fixture provenance and distribution rules

Everything in `FakeVendor.*` is **original test-double source authored for this
project**, not copied, extracted, decompiled, or translated Phone Link code.
The minimal namespaces/type names/signatures model the private contract the CLI
calls. They are not the actual vendor implementation.

The test projects intentionally compile to names such as
`YourPhone.AppCore.Managed.dll` and `YourPhone.Messaging.Managed.dll` so the
normal runtime loader can be exercised against synthetic fixtures. Those files
are built from the C# source in this directory; **matching a filename does not
make them Microsoft DLLs**. They stay in ignored build/test output directories.
They are not committed and are not part of the CLI publish/bundle/release.

Rules for changes:

- **Never copy an installed Phone Link/Microsoft product DLL, `.winmd`, extracted
  implementation, or decompiled source into this repository or a test fixture.**
- Never add real messages, notification payloads, contacts, phone numbers, tokens,
  database copies, or personal validation cases. Use invented synthetic data.
- Keep the fixture API minimal. Extend our own test doubles when needed rather
  than importing the vendor implementation to make a test pass.
- Real-installation checks are separate local validation, not CI dependencies or
  a source of fixture data. They must not save or publish personal contents.

The tests use the Windows-provided `winsqlite3.dll` at runtime. Native-loader tests
also make temporary renamed copies of the **OS-provided** `version.dll` under a
unique Windows TEMP directory, then delete them. No OS or Phone Link DLL is
stored in Git, included in release artifacts, or downloaded for these tests.

The production CLI project has no reference to these fixture projects. Its
pre-bundle publish allowlist admits only our executable/managed assembly and
its .NET metadata files. This catches accidentally added fixture/vendor DLLs
before they can be hidden inside a single-file executable. CI also rejects
tracked binary/data artifacts and packages only the CLI and public documentation.
These checks do not replace review for copied third-party source or licensing.
