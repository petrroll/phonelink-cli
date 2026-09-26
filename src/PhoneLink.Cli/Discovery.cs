using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneLink;

internal sealed record LibraryLocation(string Directory, string Version, string Discovery);

internal static class Discovery
{
    public const string Family = "Microsoft.YourPhone_8wekyb3d8bbwe";
    public static string DefaultCacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", Family, "LocalCache", "Indexed");

    public static LibraryLocation Libraries(string? overridePath)
    {
        if (overridePath != null) return Validate(Path.GetFullPath(overridePath), "explicit trusted path");
        if (!OperatingSystem.IsWindows()) throw new CliException("windows_required", "Run the CLI as a Windows process.");
        var candidates = new List<LibraryLocation>();
        foreach (string name in PackageNames())
        {
            uint length = 0;
            int result = GetPackagePathByFullName(name, ref length, 0);
            if (result != 122 || length == 0) continue;
            nint buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                if (GetPackagePathByFullName(name, ref length, buffer) != 0) continue;
                string path = Marshal.PtrToStringUni(buffer)!;
                if (File.Exists(Path.Combine(path, "YourPhone.Messaging.Managed.dll"))) candidates.Add(Validate(path, "Windows package registration"));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return candidates.OrderByDescending(c => Version.TryParse(c.Version, out var v) ? v : new Version()).FirstOrDefault()
            ?? throw new CliException("phone_link_not_installed", "No registered Phone Link installation with the required managed libraries was found for this Windows user.");
    }

    private static LibraryLocation Validate(string path, string discovery)
    {
        foreach (string file in new[] { "YourPhone.AppCore.Managed.dll", "YourPhone.Messaging.Managed.dll" })
            if (!File.Exists(Path.Combine(path, file))) throw new CliException("library_not_found", "Required library not found: " + Path.Combine(path, file));
        string version = FileVersionInfo.GetVersionInfo(Path.Combine(path, "YourPhone.Messaging.Managed.dll")).FileVersion ?? "unknown";
        return new LibraryLocation(path, version, discovery);
    }

    private static List<string> PackageNames()
    {
        uint count = 0, length = 0;
        int result = GetPackagesByPackageFamily(Family, ref count, 0, ref length, 0);
        if (result == 0 && count == 0) return [];
        for (int attempt = 0; attempt < 3 && result == 122; attempt++)
        {
            nint names = Marshal.AllocHGlobal(checked((int)count * nint.Size));
            nint buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                result = GetPackagesByPackageFamily(Family, ref count, names, ref length, buffer);
                if (result == 0)
                {
                    var output = new List<string>();
                    for (int i = 0; i < count; i++) output.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * nint.Size))!);
                    return output;
                }
            }
            finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
        }
        throw new CliException("package_discovery_failed", "Windows package enumeration failed (Win32 " + result + ").");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, nint names, ref uint bufferLength, nint buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string name, ref uint length, nint path);
}

internal sealed record DeviceProfile(string Id, string DatabaseDirectory)
{
    public string Database(string name) => Path.Combine(DatabaseDirectory, name + ".db");

    public object Describe()
    {
        var files = Directory.GetFiles(DatabaseDirectory, "*.db").Order(StringComparer.OrdinalIgnoreCase).Select(path => {
            var file = new FileInfo(path);
            var wal = new FileInfo(path + "-wal");
            DateTime written = wal.Exists && wal.LastWriteTimeUtc > file.LastWriteTimeUtc ? wal.LastWriteTimeUtc : file.LastWriteTimeUtc;
            return new { name = file.Name, size_bytes = file.Length, wal_size_bytes = wal.Exists ? wal.Length : 0, last_write_utc = written };
        }).ToArray();
        return new { id = Id, database_directory = DatabaseDirectory, databases = files };
    }

    public static List<DeviceProfile> Discover(string? root)
    {
        root = Path.GetFullPath(root ?? Discovery.DefaultCacheRoot);
        if (!Directory.Exists(root)) throw new CliException("cache_not_found", "Phone Link's Indexed cache directory was not found. Pair/sync a phone first, or specify --cache-root. Path: " + root);
        var profiles = new List<DeviceProfile>();
        foreach (string path in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            string database = Path.Combine(path, "System", "Database");
            if (File.Exists(Path.Combine(database, "phone.db")) || File.Exists(Path.Combine(database, "notifications.db")))
                profiles.Add(new DeviceProfile(Path.GetFileName(path), database));
        }
        return profiles;
    }

    public static List<DeviceProfile> Select(List<DeviceProfile> profiles, Options options)
    {
        if (profiles.Count == 0) throw new CliException("no_devices", "No cached device profiles found. Let Phone Link sync a paired phone first.");
        if (options.AllDevices) return profiles;
        if (options.Device == null)
        {
            if (profiles.Count == 1) return profiles;
        }
        else
        {
            var exact = profiles.Where(p => p.Id.Equals(options.Device, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact;
            var matches = profiles.Where(p => p.Id.StartsWith(options.Device, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches;
            if (matches.Count == 0) throw new CliException("device_not_found", "No cached device matches that ID. Run 'devices'.");
        }
        throw new CliException("ambiguous_device", "Multiple cached profiles match. Use --device ID (a unique prefix is OK) or --all-devices. Old pairings can leave stale profiles.");
    }
}
