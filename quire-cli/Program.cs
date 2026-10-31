using Quire.Cli;
using System.Text;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
    return await CommandLine.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException error)
{ if (!CommandLine.UsesProgress(args)) CliError.Print(error); return 130; }
catch (Exception error)
{ CliError.Print(error); return 1; }
