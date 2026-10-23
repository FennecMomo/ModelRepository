using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModuleRepository.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
// Bind one instance: plans must survive across separate tools/call requests.
using var tools = new ModuleTools();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools);
await builder.Build().RunAsync();
