using System.Text;
using SkarpSharp.Commands;

// The command line lives in SkarpSharp.Core (Configuration.CommandLine, Commands.SkarpCommand) so it is tested end to end.
Console.OutputEncoding = new UTF8Encoding(false);
// `skarp mcp` serves MCP over standard input and output (dispatched here, so the server does not sit in the
// Commands namespace it calls into).
if (args is ["mcp", ..]) return await SkarpSharp.Mcp.McpCommand.RunAsync(args);
return await SkarpCommand.RunAsync(args, new CommandIO
{
    Out = Console.Out,
    Error = Console.Error,
    OpenStdout = Console.OpenStandardOutput,
    In = Console.In,
});
