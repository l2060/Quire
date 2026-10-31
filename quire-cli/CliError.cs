using System.Security.Cryptography;

namespace Quire.Cli;

internal static class CliError
{
    internal static Exception Cause(Exception error)
    {
        while (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            error = aggregate.InnerExceptions[0];
        return error;
    }

    internal static string Code(Exception error) => Cause(error) switch
    {
        OperationCanceledException => "canceled",
        FileNotFoundException or DirectoryNotFoundException => "not_found",
        UnauthorizedAccessException => "access_denied",
        InvalidDataException => "invalid_data",
        CryptographicException => "authentication_failed",
        ArgumentException or FormatException or OverflowException => "invalid_argument",
        IOException => "io_error",
        InvalidOperationException => "invalid_operation",
        _ => "operation_failed"
    };

    internal static string Message(Exception error)
    {
        error = Cause(error);
        if (error is OperationCanceledException) return "Operation canceled.";
        if (error is AggregateException aggregate)
            return Message(aggregate.InnerExceptions[0]);
        return string.IsNullOrWhiteSpace(error.Message) ? "The operation could not be completed." : error.Message;
    }

    internal static void Print(Exception error)
    {
        try { Console.Error.WriteLine($"Error: {Message(error)}"); }
        catch (Exception) { /* An unavailable diagnostic stream must not prevent process exit. */ }
    }
}
