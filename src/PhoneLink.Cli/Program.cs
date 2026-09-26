using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PhoneLink.Libraries;

namespace PhoneLink;

internal static class Program
{
    internal const string Version = "0.1.0";
    internal const string Backend = "phonelink-managed-libraries";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            Options options = Options.Parse(args);
            if (options.Command == "help") { Console.WriteLine(Help); return 0; }
            if (options.Command == "version") { Write(new { version = Version, backend = Backend, cached = true }, options); return 0; }
            if (!OperatingSystem.IsWindows()) throw new CliException("windows_required", "Run this executable as a Windows process. WSL can launch the published .exe through interop.");
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new CliException("architecture_not_supported", "This release is tested with the x64 Windows Phone Link libraries. Use the win-x64 build.");
            if (options.Command == "devices")
            {
                Write(DeviceProfile.Discover(options.CacheRoot).Select(p => p.Describe()).ToArray(), options);
                return 0;
            }
            var catalog = new LibraryCatalog(Discovery.Libraries(options.LibraryPath));
            if (options.Command == "doctor")
            {
                object devices;
                try { devices = DeviceProfile.Discover(options.CacheRoot).Select(p => p.Describe()).ToArray(); }
                catch (CliException error) { devices = new { error = error.Code, message = error.Message }; }
                Write(new { version = Version, backend = Backend, cached = true, read_only = true,
                    generated_by = "AI / Astra", process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    runtime = RuntimeInformation.FrameworkDescription, libraries = catalog.Describe(), devices,
                    warnings = new[] { "Non-public APIs: likely to break after Phone Link updates.", "Only locally synced/retained data; no live-phone fetch or forced sync.", "Microsoft libraries are loaded from the local installation, not bundled." }
                }, options);
                return 0;
            }
            var selected = DeviceProfile.Select(DeviceProfile.Discover(options.CacheRoot), options);
            var reader = new LibraryReader(catalog);
            var newest = new NewestRows(options.Limit);
            foreach (var device in selected)
            {
                var rows = options.Command switch {
                    "messages" => reader.Messages(device, options),
                    "notifications" => reader.Notifications(device, options),
                    "conversations" => reader.Conversations(device, options),
                    _ => throw CliException.Usage("Unsupported command.")
                };
                foreach (var row in rows) newest.Add(row);
            }
            // Produce nothing on stdout until every selected profile succeeded.
            Write(newest.Items.Select(r => r.Data).ToArray(), options);
            return 0;
        }
        catch (CliException error) { return Error(error.Code, error.Message, error.ExitCode); }
        catch (UnauthorizedAccessException) { return Error("access_denied", "Cannot access the required files. Run as the Windows user who owns Phone Link.", 1); }
        catch (DllNotFoundException) { return Error("native_library_missing", "A required Windows native library was not found. A current Windows 10/11 x64 installation is required.", 1); }
        catch (IOException error) { return Error("io_error", $"File/pipe I/O failed ({error.GetType().Name}, 0x{error.HResult:X8}).", 1); }
        catch (Exception error) { return Error("command_failed", $"Operation failed ({error.GetType().Name}, 0x{error.HResult:X8}). Run 'doctor'; private library interfaces may have changed.", 1); }
    }

    private static void Write(object output, Options options) => Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = options.Pretty }));
    private static int Error(string code, string message, int exitCode)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = new { code, message } }));
        return exitCode;
    }

    private const string Help = """
        Phone Link CLI 0.1.0 — library-backed, Windows x64, read-only
        AI / Astra generated. Uses NON-PUBLIC APIs and is likely to break.

        Usage:
          phonelink devices
          phonelink doctor
          phonelink messages      [--device ID | --all-devices] [--limit N]
                                  [--since ISO8601] [--thread ID] [--kind KIND]
          phonelink notifications [--device ID | --all-devices] [--limit N]
                                  [--since ISO8601] [--app PACKAGE] [--include-payload]
          phonelink conversations [--device ID | --all-devices] [--limit N]
                                  [--since ISO8601]
          phonelink version

        Shared options: --pretty, --cache-root PATH, --library-path TRUSTED_DIRECTORY
        --cache-root is the LocalCache\Indexed directory, not a database file.
        Only use --library-path with trusted code: loading a DLL executes code.
        --device accepts a unique profile-ID prefix; stale pairings are included
        only when explicitly selected or when --all-devices is used.
        --limit is global, defaults to 50, and accepts 1–10000. Newest first.
        --since is inclusive and requires a timezone, e.g. 2026-09-01T00:00:00Z.
        Message kinds: sms, mms, rcs_chat, rcs_filetransfer.
        --app matches an exact Android package name.

        JSON arrays on stdout; JSON errors on stderr. Exit: 0 OK, 1 runtime, 2 args.
        Reads Phone Link's PRIVATE, LOCALLY SYNCED CACHE using its managed libraries.
        No sending, read marking, dismissal, UI automation, forced sync, or HTTP server.
        Attachment metadata only. Output can contain private messages and one-time codes.
        """;
}
