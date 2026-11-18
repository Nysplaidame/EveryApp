namespace Ferry.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Problems = 1;
    public const int Blocked = 2;
    public const int Cancelled = 3;
    public const int InvalidArguments = 4;
    public const int InternalError = 10;
}
