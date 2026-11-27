using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Server;
using RvtMcp.Plugin;
using RvtMcp.Server.Prompts;

namespace RvtMcp.Server
{
#pragma warning disable MCPEXP005
    internal static class RevitSkills
    {
        internal static McpServerSkillPackage[] CreatePackages(HashSet<string> enabled, RvtMcpConfig config)
        {
            return WorkflowCatalog.All
                .Select(def => McpServerSkillPackage.Create(
                    new[]
                    {
                        McpServerSkillFile.FromText(
                            SkillsProtocol.SkillFileName,
                            WorkflowCatalog.BuildSkillMarkdown(def, enabled, config))
                    },
                    new McpServerSkillPackageCreateOptions
                    {
                        Uri = def.SkillUri
                    }))
                .ToArray();
        }

        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config)
        {
            mcp.WithSkillPackages(CreatePackages(enabled, config));
            mcp.WithSkills(options =>
            {
                options.GetSkillFilters.Add(next => async (request, ct) =>
                {
                    PromptRequestLogging.LogSkillGet(request.Params?.Uri, request.Server?.SessionId ?? "stdio");
                    return await next(request, ct);
                });
            });
            mcp.WithRequestFilters(filters =>
            {
                filters.AddReadResourceFilter(next => async (request, ct) =>
                {
                    PromptRequestLogging.LogSkillRead(request.Params?.Uri, request.Server?.SessionId ?? "stdio");
                    return await next(request, ct);
                });
            });
            return mcp;
        }
    }
#pragma warning restore MCPEXP005
}
