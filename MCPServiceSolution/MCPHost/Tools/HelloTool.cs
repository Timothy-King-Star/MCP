using ModelContextProtocol.Server;
using System.ComponentModel;

namespace MCPHost.Tools
{
    [McpServerToolType]
    public class HelloTool
    {
        [McpServerTool]
        [Description("测试MCP是否正常工作")]
        public static string Hello()
        {
            return "Hello SqlServer MCP!";
        }
    }
}
