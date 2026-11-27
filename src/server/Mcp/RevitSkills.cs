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

        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config,
            McpSessionObserver sessionObserver = null)
        {
            mcp.WithSkillPackages(CreatePackages(enabled, config));
            mcp.WithSkills(options =>
            {
                options.ListSkillsFilters.Add(next => async (request, ct) =>
                {
                    sessionObserver?.Observe(request.Server, request.JsonRpcRequest);
                    var count = -1;
                    try
                    {
                        var result = await next(request, ct);
                        count = result?.Skills?.Count ?? 0;
                        return result;
                    }
                    finally
                    {
                        PromptRequestLogging.LogSkillList(count, request.Server?.SessionId ?? "stdio", request.Server?.ClientInfo?.Name);
                    }
                });
                options.GetSkillFilters.Add(next => async (request, ct) =>
                {
                    sessionObserver?.Observe(request.Server, request.JsonRpcRequest);
                    PromptRequestLogging.LogSkillGet(request.Params?.Uri, request.Server?.SessionId ?? "stdio", request.Server?.ClientInfo?.Name);
                    return await next(request, ct);
                });
            });
            mcp.WithRequestFilters(filters =>
            {
                filters.AddReadResourceFilter(next => async (request, ct) =>
                {
                    sessionObserver?.Observe(request.Server, request.JsonRpcRequest);
                    PromptRequestLogging.LogSkillRead(request.Params?.Uri, request.Server?.SessionId ?? "stdio", request.Server?.ClientInfo?.Name);
                    return await next(request, ct);
                });
            });
            return mcp;
        }
    }
#pragma warning restore MCPEXP005
}
