using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using PhoneLink.Interop;

namespace PhoneLink.Libraries;

internal sealed record TableSpec(string Kind, string Assembly, string TypeName, string Database)
{
    public static readonly TableSpec[] Messages = [
        Message("sms", "SmsTable"), Message("mms", "MmsTable"),
        Message("rcs_chat", "RcsChatTable"), Message("rcs_filetransfer", "RcsFileTransferTable")
    ];
    public static readonly TableSpec[] Conversations = [Message("sms_mms", "ConversationTable"), Message("rcs", "RcsConversationTable")];
    public static readonly TableSpec MmsParts = Message("mms_part", "MmsPartTable");
    public static readonly TableSpec Notifications = new("notification", "YourPhone.Notifications.Managed", "YourPhone.Notifications.WinRT.DataStore.NotificationsTable", "notifications");
    private static TableSpec Message(string kind, string type) => new(kind, "YourPhone.Messaging.Managed", "YourPhone.Messaging.WinRT.DataStore." + type, "phone");
}

internal sealed class LibraryCatalog
{
    private readonly VendorContext context;
    private readonly Type connectionInterface;
    private readonly Type statementInterface;
    private readonly Dictionary<TableSpec, Type> types = [];
    public LibraryLocation Location { get; }

    public LibraryCatalog(LibraryLocation location)
    {
        Location = location;
        context = new VendorContext(location.Directory);
        connectionInterface = Guard("load connection contract", () => context.Library("YourPhone.AppCore.Managed").GetType("YourPhone.AppCore.WinRT.DataStore.ISqliteConnection", true)!);
        statementInterface = Guard("load statement contract", () => context.Library("YourPhone.AppCore.Managed").GetType("YourPhone.AppCore.WinRT.DataStore.ISqliteStatement", true)!);
    }

    private Type TableType(TableSpec spec)
    {
        if (!types.TryGetValue(spec, out var type))
            types.Add(spec, type = Guard("load " + spec.TypeName, () => context.Library(spec.Assembly).GetType(spec.TypeName, true)!));
        return type;
    }

    public object CreateTable(TableSpec spec, ReadOnlyDatabase database)
    {
        return Guard("construct " + spec.TypeName, () => {
            using var scope = context.EnterContextualReflection();
            var adapter = (ReadOnlyConnectionAdapter)DispatchProxy.Create(connectionInterface, typeof(ReadOnlyConnectionAdapter));
            adapter.Database = database;
            adapter.StatementType = statementInterface;
            // Only table constructors taking our read-only interface, never the
            // app's SqliteConnection/DatabaseBase/IDeviceData constructors.
            var constructor = TableType(spec).GetConstructor([connectionInterface]) ??
                throw CliException.Contract("Missing read-adapter constructor for " + spec.TypeName + ".");
            return constructor.Invoke([adapter]);
        });
    }

    public IReadOnlyList<object> CallRows(object table, string method, params object[] args)
    {
        if (method is not ("GetEntitiesFromIds" or "GetMessagesInThread" or "GetPartsForMessage"))
            throw CliException.Contract("This method is not on the read-only invocation allowlist.");
        return Guard(table.GetType().FullName + "." + method, () => {
            using var scope = context.EnterContextualReflection();
            var candidates = table.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == method &&
                m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(args[i])).All(x => x)).ToArray();
            if (candidates.Length != 1) throw CliException.Contract("Missing or ambiguous read method: " + method + ".");
            object? result = candidates[0].Invoke(table, args);
            if (result is not IEnumerable rows) throw CliException.Contract("Unexpected return type from " + method + ".");
            return rows.Cast<object>().ToArray();
        });
    }

    public object? Property(object entity, string name, bool required = false)
    {
        return Guard(entity.GetType().FullName + "." + name, () => {
            using var scope = context.EnterContextualReflection();
            var property = entity.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                if (required) throw CliException.Contract("Missing required property " + entity.GetType().Name + "." + name + ".");
                return null;
            }
            return property.GetValue(entity);
        });
    }

    public (string Table, string Key) TableNames(object table)
    {
        if (Property(table, "TableName", true) is not string name || string.IsNullOrEmpty(name) ||
            Property(table, "PrimaryKeyName", true) is not string key || string.IsNullOrEmpty(key))
            throw CliException.Contract("The library did not supply table/key metadata.");
        return (name, key);
    }

    public object Describe()
    {
        var capabilities = new List<object>();
        foreach (var spec in TableSpec.Messages.Concat(TableSpec.Conversations).Append(TableSpec.Notifications).Append(TableSpec.MmsParts))
        {
            try
            {
                Type type = TableType(spec);
                capabilities.Add(new { kind = spec.Kind, type = spec.TypeName,
                    read_by_ids = type.GetMethods().Any(m => m.Name == "GetEntitiesFromIds"),
                    read_by_thread = type.GetMethods().Any(m => m.Name == "GetMessagesInThread") });
            }
            catch (CliException error) { capabilities.Add(new { kind = spec.Kind, error = error.Code }); }
        }
        return new { directory = Location.Directory, version = Location.Version, discovery = Location.Discovery,
            package_full_name = Location.PackageFullName, package_version = Location.PackageVersion, architecture = Location.Architecture,
            signature_verified_by_cli = false, connection_contract = connectionInterface.FullName, statement_contract = statementInterface.FullName, capabilities };
    }

    private static T Guard<T>(string operation, Func<T> action)
    {
        try { return action(); }
        catch (Exception error)
        {
            while (error is TargetInvocationException or TypeInitializationException && error.InnerException != null) error = error.InnerException;
            if (error is CliException known) throw known;
            // Vendor exceptions (especially JSON parsers) can contain private
            // content. Never put their raw messages or stack traces on stderr.
            throw new CliException("library_call_failed", $"Phone Link library operation '{operation}' failed ({error.GetType().Name}, 0x{error.HResult:X8}). This private API/version may be incompatible; run 'doctor'.");
        }
    }

    private sealed class VendorContext : AssemblyLoadContext
    {
        private readonly string directory;
        private readonly HashSet<string> framework;
        private readonly Assembly host = typeof(LibraryCatalog).Assembly;
        public VendorContext(string directory) : base("PhoneLink vendor libraries", isCollectible: false)
        {
            this.directory = directory;
            framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        public Assembly Library(string name) => LoadFromAssemblyName(new AssemblyName(name));
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == host.GetName().Name) return host;
            if (name.Name != null && framework.Contains(name.Name)) return null;
            if (string.IsNullOrWhiteSpace(name.Name) || name.Name is "." or ".." ||
                Path.GetFileName(name.Name) != name.Name || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new FileNotFoundException("Invalid vendor dependency name.");
            string path = Path.Combine(directory, name.Name + ".dll");
            if (!File.Exists(path)) throw new FileNotFoundException("Vendor dependency not found in the selected package.", name.Name);
            return LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string name) => NativeDependencyLoader.Load(directory, name);
    }
}
