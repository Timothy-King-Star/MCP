using MCPHost;
using MCPHost.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// 无论 MCP 客户端从哪个工作目录启动进程，都从 exe 所在目录读取 appsettings.json
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

// 关闭所有 Console Logger
builder.Logging.ClearProviders();

builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddTransient<DatabaseTool>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

await host.RunAsync();