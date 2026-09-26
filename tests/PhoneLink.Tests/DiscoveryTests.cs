using System.Runtime.InteropServices;
using PhoneLink.Interop;
using PhoneLink.Libraries;

namespace PhoneLink.Tests;

internal static partial class Suite
{
    private sealed class PackageFixture : IPackageSource
    {
        public List<RegisteredPackage> Packages { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Enumerations { get; private set; }
        public void Add(string version, string directory, string cpu = "x64", string resource = "", string? dllVersion = null)
        {
            Packages.Add(new RegisteredPackage($"Microsoft.YourPhone_{version}_{cpu}_{resource}_8wekyb3d8bbwe", directory, "synthetic effective path"));
            Files.Add(Path.Combine(directory, "YourPhone.AppCore.Managed.dll"));
            string messages = Path.Combine(directory, "YourPhone.Messaging.Managed.dll");
            Files.Add(messages);
            Versions[messages] = dllVersion ?? version;
        }
        public IEnumerable<RegisteredPackage> FindForCurrentUser(string family)
        {
            Require(family == Discovery.Family, "queried the wrong package family");
            Enumerations++;
            return Packages;
        }
        public LibraryLocation Locate(string? path = null) => Discovery.Libraries(path, this, Files.Contains, file => Versions.GetValueOrDefault(file));
    }

    private static void DiscoveryTests()
    {
        Test("automatic discovery accepts a non-C WindowsApps volume", () => {
            var fixture = new PackageFixture();
            const string moved = @"D:\WindowsApps\Microsoft.YourPhone_3.0.0.0_x64__8wekyb3d8bbwe";
            fixture.Add("3.0.0.0", moved);
            var result = fixture.Locate();
            Require(result.Directory == moved && fixture.Enumerations == 1, "did not use the registered path");
            Require(result.PackageVersion == "3.0.0.0" && result.Architecture == "x64", "package identity metadata lost");
        });
        Test("effective paths need not be named WindowsApps; spaces/Unicode survive", () => {
            var fixture = new PackageFixture();
            const string external = @"E:\Application locations\žluťoučký\Phone Link";
            fixture.Add("3.0.0.0", external);
            Require(fixture.Locate().Directory == external, "reconstructed or hard-coded the install folder");
        });
        Test("Windows-returned UNC paths are preserved, not reconstructed", () => {
            var fixture = new PackageFixture();
            const string external = @"\\registered-location\apps\Phone Link";
            fixture.Add("3.0.0.0", external);
            Require(fixture.Locate().Directory == external, "UNC path changed");
        });
        Test("selection uses package versions, not directory order or DLL versions", () => {
            var fixture = new PackageFixture();
            fixture.Add("2.0.0.0", @"E:\older", dllVersion: "99.0.0.0");
            fixture.Add("10.0.0.0", @"D:\newer", dllVersion: "1.0.0.0");
            var result = fixture.Locate();
            Require(result.Directory == @"D:\newer" && result.Version == "1.0.0.0", "wrong version source/order");
        });
        Test("resource and incompatible architecture packages are not loaded", () => {
            var fixture = new PackageFixture();
            fixture.Add("20.0.0.0", @"D:\resource", resource: "en-us");
            fixture.Add("20.0.0.0", @"D:\arm", cpu: "arm64");
            fixture.Add("20.0.0.0", @"D:\x86", cpu: "x86");
            fixture.Add("2.0.0.0", @"D:\correct");
            Require(fixture.Locate().Directory == @"D:\correct", "incompatible package chosen");
        });
        Test("neutral packages work; native architecture wins equal-version ties", () => {
            var fixture = new PackageFixture();
            fixture.Add("2.0.0.0", @"D:\neutral", cpu: "neutral");
            Require(fixture.Locate().Directory == @"D:\neutral", "neutral package rejected");
            fixture.Add("2.0.0.0", @"E:\native");
            Require(fixture.Locate().Directory == @"E:\native", "native tie preference wrong");
        });
        Test("an incomplete update candidate does not hide a valid installation", () => {
            var fixture = new PackageFixture();
            fixture.Add("9.0.0.0", @"D:\incomplete");
            fixture.Files.Remove(@"D:\incomplete\YourPhone.AppCore.Managed.dll");
            fixture.Add("2.0.0.0", @"E:\complete");
            Require(fixture.Locate().Directory == @"E:\complete", "incomplete candidate blocked discovery");
        });
        Test("package removal/access failures during metadata inspection are skipped", () => {
            var fixture = new PackageFixture();
            fixture.Add("9.0.0.0", @"D:\removed");
            fixture.Add("8.0.0.0", @"D:\denied");
            fixture.Add("2.0.0.0", @"E:\complete");
            string? ReadVersion(string file)
            {
                if (file.Contains("removed")) throw new FileNotFoundException();
                if (file.Contains("denied")) throw new UnauthorizedAccessException();
                return "2.0.0.0";
            }
            Require(Discovery.Libraries(null, fixture, fixture.Files.Contains, ReadVersion).Directory == @"E:\complete", "update race blocked discovery");
        });
        Test("malformed identities, other families, and relative paths are ignored", () => {
            var fixture = new PackageFixture();
            fixture.Add("8.0.0.0", @"D:\wrong-family");
            fixture.Packages[0] = fixture.Packages[0] with { FullName = "Other.Application_8.0.0.0_x64__8wekyb3d8bbwe" };
            fixture.Add("broken-version", @"D:\malformed");
            fixture.Add("9.0.0.0", "relative-folder");
            Throws(() => fixture.Locate(), "phone_link_not_installed");
        });
        Test("no registrations means no filesystem/PATH/current-folder fallback", () => {
            var fixture = new PackageFixture();
            fixture.Files.Add(Path.Combine(Environment.CurrentDirectory, "YourPhone.Messaging.Managed.dll"));
            int fileProbes = 0;
            Throws(() => Discovery.Libraries(null, fixture, _ => { fileProbes++; return true; }, _ => "1.0.0.0"), "phone_link_not_installed");
            Require(fileProbes == 0, "scanned files outside package registration");
        });
        Test("explicit library override is separate from automatic discovery", () => {
            var fixture = new PackageFixture();
            const string directory = @"E:\trusted test libraries";
            fixture.Add("1.0.0.0", directory);
            var result = fixture.Locate(directory);
            Require(fixture.Enumerations == 0 && result.PackageFullName == null && result.Discovery == "explicit library path (unverified)", "override unexpectedly enumerated packages");
        });
        Test("cache location follows LocalApplicationData, not the install drive", () => {
            const string local = @"E:\Profiles\Synthetic User\AppData\Local";
            Require(Discovery.CacheRootFor(local) == Path.Combine(local, "Packages", Discovery.Family, "LocalCache", "Indexed"), "cache path was hard-coded");
            Throws(() => Discovery.CacheRootFor(""), "cache_location_unavailable");
            Throws(() => Discovery.CacheRootFor("relative"), "cache_location_unavailable");
        });
        Test("native package path reader retries a growing UTF-16 buffer", () => {
            const string expected = @"D:\Relocated packages\žluťoučký\Phone Link";
            int fills = 0;
            int Query(string name, ref uint length, nint buffer)
            {
                if (buffer == 0) { length = 4; return 122; }
                if (fills++ == 0) { length = (uint)expected.Length + 1; return 122; }
                char[] text = (expected + '\0').ToCharArray();
                Require(length >= text.Length, "retry did not grow the buffer");
                Marshal.Copy(text, 0, buffer, text.Length);
                length = (uint)text.Length;
                return 0;
            }
            Require(WindowsPackageSource.ReadPath("synthetic", Query) == expected && fills == 2, "buffer retry failed");
        });
        Test("failed path queries and endless size races fail boundedly", () => {
            int deniedCalls = 0;
            int Denied(string name, ref uint length, nint buffer) { deniedCalls++; return 5; }
            Require(WindowsPackageSource.ReadPath("synthetic", Denied) == null && deniedCalls == 1, "retried denied lookup as another source");
            int growingCalls = 0;
            int Growing(string name, ref uint length, nint buffer) { growingCalls++; length += 8; return 122; }
            Require(WindowsPackageSource.ReadPath("synthetic", Growing) == null && growingCalls == 4, "unbounded path query retries");
        });
        NativeResolutionTests();
    }

    private static void NativeResolutionTests()
    {
        string approved = Path.Combine(temporary, "approved native DLLs");
        string untrusted = Path.Combine(temporary, "untrusted native DLLs");
        Directory.CreateDirectory(approved);
        Directory.CreateDirectory(untrusted);
        string systemDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
        Test("native dependency resolver uses the selected directory or System32", () => {
            File.Copy(systemDll, Path.Combine(approved, "fixture-approved.dll"));
            nint handle = NativeDependencyLoader.Load(approved, "fixture-approved.dll");
            Require(handle != 0, "approved dependency not loaded");
            NativeLibrary.Free(handle);
            handle = NativeDependencyLoader.Load(approved, "version.dll");
            Require(handle != 0, "System32 dependency not loaded");
            NativeLibrary.Free(handle);
        });
        Test("native dependency resolver rejects relative/absolute dependency paths", () => {
            foreach (string name in new[] { @"..\version.dll", systemDll, "bad.dll:stream", ".", ".." })
            {
                try { nint handle = NativeDependencyLoader.Load(approved, name); NativeLibrary.Free(handle); }
                catch (DllNotFoundException) { continue; }
                throw new Exception("accepted a dependency path");
            }
        });
        Test("native dependency resolver cannot fall back to working directory/PATH", () => {
            File.Copy(systemDll, Path.Combine(untrusted, "fixture-untrusted.dll"));
            string beforeDirectory = Environment.CurrentDirectory;
            string? beforePath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.CurrentDirectory = untrusted;
                Environment.SetEnvironmentVariable("PATH", untrusted + Path.PathSeparator + beforePath);
                try { nint handle = NativeDependencyLoader.Load(approved, "fixture-untrusted.dll"); NativeLibrary.Free(handle); }
                catch (DllNotFoundException) { return; }
                throw new Exception("loaded a dependency from the working directory/PATH");
            }
            finally { Environment.CurrentDirectory = beforeDirectory; Environment.SetEnvironmentVariable("PATH", beforePath); }
        });
    }
}
