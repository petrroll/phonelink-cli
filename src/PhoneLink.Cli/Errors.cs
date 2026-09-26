namespace PhoneLink;

internal sealed class CliException(string code, string message, int exitCode = 1) : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
    public static CliException Usage(string message) => new("invalid_arguments", message, 2);
    public static CliException Contract(string detail) => new("unsupported_library", detail + " Phone Link's non-public APIs may have changed; run 'doctor'.");
}
