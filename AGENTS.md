# Repository rules

- Do not commit or redistribute Microsoft Phone Link or other non-.NET product
  DLLs, `.winmd` files, extracted/decompiled implementations, or third-party code
  copied to fixtures.
  Load the user's installed libraries at runtime only.
- `tests/FakeVendor.*` contains original, minimal source-only contract doubles.
  Never replace them with real vendor assemblies; see `tests/README.md`.
- Never put personal phone messages, sender details, contacts, IDs tied to real
  data, tokens, or database copies in Git or fixtures. Live checks stay local and
  must not print/save private contents.
- Preserve the read-only boundary and the CLI publish allowlist. No UI automation,
  sending, read marking, permission bypass, or vendor database constructors.
- Validate changes with `./scripts/test.sh` in WSL, or `scripts/test.ps1` on Windows.
  Both use synthetic fixtures and require the .NET 10 SDK; running tests needs Windows.
