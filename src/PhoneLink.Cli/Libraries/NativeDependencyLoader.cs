using System.Runtime.InteropServices;

namespace PhoneLink.Libraries;

internal static class NativeDependencyLoader
{
    // Returning zero from AssemblyLoadContext.LoadUnmanagedDll would fall back
    // to the runtime/OS's broader search. Fail closed instead: only the chosen
    // package directory or Windows System32 may satisfy these P/Invokes.
    public static nint Load(string packageDirectory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new DllNotFoundException("Native dependencies must use a simple file name.");
        string fileName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        string path = Path.Combine(packageDirectory, fileName);
        if (File.Exists(path)) return NativeLibrary.Load(path);
        if (NativeLibrary.TryLoad(name, typeof(NativeDependencyLoader).Assembly, DllImportSearchPath.System32, out nint handle)) return handle;
        throw new DllNotFoundException("Native dependency was not found in the selected package or System32: " + fileName);
    }
}
