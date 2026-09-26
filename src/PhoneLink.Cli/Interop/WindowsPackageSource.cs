using System.Runtime.InteropServices;

namespace PhoneLink.Interop;

internal sealed record RegisteredPackage(string FullName, string Directory, string PathSource);

internal interface IPackageSource
{
    IEnumerable<RegisteredPackage> FindForCurrentUser(string family);
}

// No drive letters, directory scans, registry-layout assumptions, or PowerShell
// subprocesses. AppModel resolves the registered package's actual volume/path.
internal sealed class WindowsPackageSource : IPackageSource
{
    private const int InsufficientBuffer = 122;
    internal delegate int PathQuery(string name, ref uint length, nint buffer);

    public IEnumerable<RegisteredPackage> FindForCurrentUser(string family)
    {
        if (!OperatingSystem.IsWindows()) throw new CliException("windows_required", "Package discovery requires Windows.");
        foreach (string name in PackageNames(family))
        {
            string? directory;
            string source;
            try
            {
                // Effective honors user/machine external and mutable locations,
                // where present, otherwise returning the install location.
                directory = ReadPath(name, EffectivePath);
                source = "GetPackagePathByFullName2 (Effective)";
            }
            catch (EntryPointNotFoundException)
            {
                // Only fall back when the API is absent, not on access denial
                // or another error resolving an effective location.
                directory = ReadPath(name, GetPackagePathByFullName);
                source = "GetPackagePathByFullName";
            }
            if (directory != null) yield return new RegisteredPackage(name, directory, source);
        }
    }

    private static int EffectivePath(string name, ref uint length, nint buffer) =>
        GetPackagePathByFullName2(name, 2, ref length, buffer); // PackagePathType_Effective

    // The required buffer can change between the size query and the read while
    // Store deployment is updating registrations. Retry rather than using stale data.
    internal static string? ReadPath(string name, PathQuery query)
    {
        uint length = 0;
        int result = query(name, ref length, 0);
        for (int attempt = 0; attempt < 3 && result == InsufficientBuffer; attempt++)
        {
            if (length == 0) return null;
            nint buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                result = query(name, ref length, buffer);
                if (result == 0)
                {
                    string? path = Marshal.PtrToStringUni(buffer);
                    return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) ? path : null;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null; // Removed, inaccessible, or still changing; examine other packages.
    }

    private static List<string> PackageNames(string family)
    {
        uint count = 0, length = 0;
        int result = GetPackagesByPackageFamily(family, ref count, 0, ref length, 0);
        if (result == 0 && count == 0) return [];
        for (int attempt = 0; attempt < 3 && result == InsufficientBuffer; attempt++)
        {
            nint names = Marshal.AllocHGlobal(checked((int)count * nint.Size));
            nint buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                result = GetPackagesByPackageFamily(family, ref count, names, ref length, buffer);
                if (result == 0)
                {
                    var output = new List<string>();
                    for (int i = 0; i < count; i++) output.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * nint.Size))!);
                    return output;
                }
            }
            finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
        }
        throw new CliException("package_discovery_failed", "Current-user Windows package enumeration failed (Win32 " + result + ").");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, nint names, ref uint length, nint buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string name, ref uint length, nint path);
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName2(string name, int pathType, ref uint length, nint path);
}
