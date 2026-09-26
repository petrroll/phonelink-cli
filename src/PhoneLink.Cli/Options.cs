using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneLink;

internal sealed class Options
{
    public string Command { get; private set; } = "help";
    public string? CacheRoot { get; private set; }
    public string? LibraryPath { get; private set; }
    public string? Device { get; private set; }
    public bool AllDevices { get; private set; }
    public bool Pretty { get; private set; }
    public bool IncludePayload { get; private set; }
    public int Limit { get; private set; } = 50;
    public DateTimeOffset? Since { get; private set; }
    public long? Thread { get; private set; }
    public string? App { get; private set; }
    public string? Kind { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h")) return options;
        options.Command = args[0] == "--version" ? "version" : args[0];
        string[] shared = ["--cache-root", "--library-path", "--pretty"];
        var allowed = new HashSet<string>(shared, StringComparer.Ordinal);
        switch (options.Command)
        {
            case "help": case "version": allowed = new(["--pretty"]); break;
            case "devices": case "doctor": break;
            case "messages": allowed.UnionWith(["--device", "--all-devices", "--limit", "--since", "--thread", "--kind"]); break;
            case "notifications": allowed.UnionWith(["--device", "--all-devices", "--limit", "--since", "--app", "--include-payload"]); break;
            case "conversations": allowed.UnionWith(["--device", "--all-devices", "--limit", "--since"]); break;
            default: throw CliException.Usage("Unknown command. Run 'phonelink --help'.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (!allowed.Contains(option)) throw CliException.Usage("Unknown or inapplicable option: " + option);
            if (!seen.Add(option)) throw CliException.Usage("Duplicate option: " + option);
            switch (option)
            {
                case "--all-devices": options.AllDevices = true; continue;
                case "--pretty": options.Pretty = true; continue;
                case "--include-payload": options.IncludePayload = true; continue;
            }
            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                throw CliException.Usage("Missing value for " + option);
            string value = args[i];
            switch (option)
            {
                case "--cache-root": options.CacheRoot = value; break;
                case "--library-path": options.LibraryPath = value; break;
                case "--device": options.Device = value; break;
                case "--app": options.App = value; break;
                case "--kind":
                    if (value is not ("sms" or "mms" or "rcs_chat" or "rcs_filetransfer"))
                        throw CliException.Usage("--kind must be sms, mms, rcs_chat, or rcs_filetransfer.");
                    options.Kind = value;
                    break;
                case "--limit":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int limit) || limit is < 1 or > 10000)
                        throw CliException.Usage("--limit must be an integer from 1 to 10000.");
                    options.Limit = limit;
                    break;
                case "--thread":
                    if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long thread))
                        throw CliException.Usage("--thread must be a signed 64-bit integer.");
                    options.Thread = thread;
                    break;
                case "--since":
                    if (!Regex.IsMatch(value, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z") ||
                        !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var since))
                        throw CliException.Usage("--since requires an ISO 8601 timestamp with a timezone, e.g. 2026-09-01T00:00:00Z.");
                    options.Since = since.ToUniversalTime();
                    break;
            }
        }
        if (options.AllDevices && options.Device != null)
            throw CliException.Usage("Use --device or --all-devices, not both.");
        return options;
    }
}
