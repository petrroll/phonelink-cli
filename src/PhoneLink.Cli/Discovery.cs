using System.Diagnostics;
using System.Runtime.InteropServices;
using PhoneLink.Interop;

namespace PhoneLink;

internal sealed record LibraryLocation(string Directory, string Version, string Discovery,
    string? PackageFullName = null, string? PackageVersion = null, string? Architecture = null);

internal static class Discovery
{
    public const string Family = "Microsoft.YourPhone_8wekyb3d8bbwe";
    public static string DefaultCacheRoot => CacheRootFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string CacheRootFor(string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
            throw new CliException("cache_location_unavailable", "Windows did not provide an absolute LocalApplicationData path. Run as the Phone Link user or specify --cache-root.");
        return Path.Combine(localApplicationData, "Packages", Family, "LocalCache", "Indexed");
    }

    public static LibraryLocation Libraries(string? overridePath) => Libraries(overridePath,
        new WindowsPackageSource(), File.Exists,
        path => FileVersionInfo.GetVersionInfo(path).FileVersion,
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

    // Keep selection separate from native enumeration so moved package volumes,
    // redirected user folders, and update races can be tested without moving a
    // real WindowsApps directory or accessing another user's installation.
    internal static LibraryLocation Libraries(string? overridePath, IPackageSource packages,
        Func<string, bool> exists, Func<string, string?> fileVersion, string architecture = "x64")
    {
        if (overridePath != null) return Validate(Path.GetFullPath(overridePath), "explicit library path (unverified)", exists, fileVersion);
        var candidates = new List<(LibraryLocation Location, Version PackageVersion, bool NativeArchitecture)>();
        foreach (var package in packages.FindForCurrentUser(Family))
        {
            // Full package identity is name_version_architecture_resourceId_publisherId.
            // Do not load resource/bundle packages or another CPU architecture.
            string[] identity = package.FullName.Split('_');
            if (identity.Length != 5 || !string.Equals(identity[0] + "_" + identity[4], Family, StringComparison.OrdinalIgnoreCase) ||
                identity[3].Length != 0 || !Version.TryParse(identity[1], out var version)) continue;
            string cpu = identity[2].ToLowerInvariant();
            if (cpu != architecture && cpu != "neutral") continue;
            if (string.IsNullOrWhiteSpace(package.Directory) || !Path.IsPathFullyQualified(package.Directory)) continue;
            try
            {
                string directory = Path.GetFullPath(package.Directory);
                var location = Validate(directory, "Windows package registration: " + package.PathSource, exists, fileVersion) with {
                    PackageFullName = package.FullName, PackageVersion = version.ToString(), Architecture = cpu
                };
                candidates.Add((location, version, cpu == architecture));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException || error is CliException { Code: "library_not_found" })
            {
                // A Store update can remove a candidate between enumeration and
                // inspection. An incomplete candidate must not hide another valid one.
            }
        }
        return candidates.OrderByDescending(c => c.PackageVersion).ThenByDescending(c => c.NativeArchitecture)
            .ThenBy(c => c.Location.PackageFullName, StringComparer.Ordinal).Select(c => c.Location).FirstOrDefault()
            ?? throw new CliException("phone_link_not_installed", "No current-user registered Phone Link main package for this architecture contains the required managed libraries. A Store update may be in progress. No filesystem search or other-user fallback is performed.");
    }

    private static readonly string[] RequiredFiles = ["YourPhone.AppCore.Managed.dll", "YourPhone.Messaging.Managed.dll"];
    private static LibraryLocation Validate(string path, string discovery, Func<string, bool> exists, Func<string, string?> fileVersion)
    {
        foreach (string file in RequiredFiles)
            if (!exists(Path.Combine(path, file))) throw new CliException("library_not_found", "Required library not found: " + Path.Combine(path, file));
        string version = fileVersion(Path.Combine(path, "YourPhone.Messaging.Managed.dll")) ?? "unknown";
        return new LibraryLocation(path, version, discovery);
    }
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

    public static List<DeviceProfile> Select(List<DeviceProfile> profiles, Options options, string database)
    {
        if (profiles.Count == 0) throw new CliException("no_devices", "No cached device profiles found. Let Phone Link sync a paired phone first.");
        if (options.Device != null)
        {
            var exact = profiles.Where(p => p.Id.Equals(options.Device, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count == 1 ? exact : profiles.Where(p => p.Id.StartsWith(options.Device, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new CliException("device_not_found", "No cached device matches that ID. Run 'devices'.");
            if (matches.Count == 1)
            {
                if (!File.Exists(matches[0].Database(database)))
                    throw new CliException("database_not_found", "The selected profile has no " + database + ".db cache.");
                return matches;
            }
        }
        else
        {
            // A notifications-only profile cannot answer a messages command (and
            // vice versa). Explicit selection above still reports a missing DB.
            var eligible = profiles.Where(p => File.Exists(p.Database(database))).ToList();
            if (eligible.Count == 0) throw new CliException("database_not_found", "No cached profile contains " + database + ".db.");
            if (options.AllDevices || eligible.Count == 1) return eligible;
        }
        throw new CliException("ambiguous_device", "Multiple cached profiles match. Use --device ID (a unique prefix is OK) or --all-devices. Old pairings can leave stale profiles.");
    }
}
