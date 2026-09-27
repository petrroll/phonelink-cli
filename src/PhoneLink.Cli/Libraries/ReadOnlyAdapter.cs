using System.Globalization;
using System.Reflection;
using System.Text;
using PhoneLink.Interop;

namespace PhoneLink.Libraries;

// Public/non-sealed only because DispatchProxy generates derived classes.
// These adapters are not a supported external API of this project.
public class ReadOnlyConnectionAdapter : DispatchProxy
{
    internal ReadOnlyDatabase Database { get; set; } = null!;
    internal Type StatementType { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Database.Observe(() => InvokeCore(targetMethod, args));

    private object? InvokeCore(MethodInfo? targetMethod, object?[]? args)
    {
        string method = targetMethod?.Name ?? throw CliException.Contract("Missing connection method.");
        switch (method)
        {
            case "CreateStatement":
                if (args is not [string sql]) throw CliException.Contract("Unexpected CreateStatement signature.");
                var adapter = (ReadOnlyStatementAdapter)Create(StatementType, typeof(ReadOnlyStatementAdapter));
                adapter.Statement = Database.Prepare(sql);
                return adapter;
            case "get_FileName": return Database.Path;
            case "AddTable": case "RemoveTable": case "Dispose": return null;
            default: throw CliException.Contract("Unsupported connection method: " + method + ".");
        }
    }
}

public class ReadOnlyStatementAdapter : DispatchProxy
{
    internal ReadOnlyDatabase.Statement Statement { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Statement.Owner.Observe(() => InvokeCore(targetMethod, args));

    private object? InvokeCore(MethodInfo? targetMethod, object?[]? args)
    {
        string method = targetMethod?.Name ?? throw CliException.Contract("Missing statement method.");
        args ??= [];
        switch (method)
        {
            case "Dispose": Statement.Dispose(); return null;
            case "get_Statement": return Statement.Sql;
            case "Step": return Statement.Step();
            case "Reset": Statement.Reset(); return null;
            case "HasColumn": return Statement.HasColumn(Column(args));
            case "IsDbNull":
                string name = Column(args);
                return Statement.HasColumn(name) && Statement.Get(name) == null;
            case "BindNull": Statement.Bind(Index(args), null); return null;
            case "BindBool": case "BindInt": case "BindInt64": case "BindDouble": case "BindDateTime": case "BindText16":
                if (args.Length != 2) throw CliException.Contract("Unexpected " + method + " signature.");
                Statement.Bind(Index(args), args[1]);
                return null;
            case "BindText":
                if (args is not [int index, byte[] bytes]) throw CliException.Contract("Unexpected BindText signature.");
                Statement.Bind(index, Encoding.UTF8.GetString(bytes));
                return null;
            case "ReadBlob": return Blob(targetMethod!, args);
            case "ReadText16":
                object? text = Statement.Get(Column(args));
                if (text is ReadOnlyDatabase.BlobInfo) throw CliException.Contract("A binary column was requested as text.");
                return text == null ? "" : Convert.ToString(text, CultureInfo.InvariantCulture);
            case "ReadInt": return Convert.ToInt32(Required(args), CultureInfo.InvariantCulture);
            case "ReadInt64": return Convert.ToInt64(Required(args), CultureInfo.InvariantCulture);
            case "ReadDouble": return Convert.ToDouble(Required(args), CultureInfo.InvariantCulture);
            case "ReadBool": return Convert.ToInt64(Required(args), CultureInfo.InvariantCulture) != 0;
            case "ReadDateTime": return WindowsTime.FromFileTime(Convert.ToInt64(Required(args), CultureInfo.InvariantCulture));
            default: throw CliException.Contract("Unsupported statement method: " + method + ".");
        }
    }
    private object Required(object?[] args) => Statement.Get(Column(args)) ?? throw CliException.Contract("Null in a required numeric result column.");
    private static string Column(object?[] args) => args.Length > 0 && args[0] is string name ? name : throw CliException.Contract("Unexpected result-column argument.");
    private static int Index(object?[] args) => args.Length > 0 && args[0] is int index ? index : throw CliException.Contract("Unexpected binding-index argument.");
    private object? Blob(MethodInfo method, object?[] args)
    {
        string name = Column(args);
        if (args.Length == 3 && method.ReturnType.IsInterface && Statement.HasColumn(name + "_length"))
        {
            object? length = Statement.Get(name + "_length");
            if (length == null || Convert.ToInt64(length, CultureInfo.InvariantCulture) == 0) return null;
            // The vendor entities hold lazy blob references. Keep this fact but
            // never open them, copy binary bytes, or run a vendor DB constructor.
            var reference = (MetadataOnlyBlob)Create(method.ReturnType, typeof(MetadataOnlyBlob));
            reference.Database = Statement.Owner;
            return reference;
        }
        if (Statement.HasColumn(name) && Statement.Get(name) is null or ReadOnlyDatabase.BlobInfo { Length: 0 }) return null;
        throw new CliException("binary_not_supported", "Binary attachment extraction is not implemented.");
    }
}

public class MetadataOnlyBlob : DispatchProxy
{
    internal ReadOnlyDatabase Database { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Database.Observe<object?>(() => throw new CliException("binary_not_supported", "The CLI exposes attachment metadata, not attachment streams."));
}
