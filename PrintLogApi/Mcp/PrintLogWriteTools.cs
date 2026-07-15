using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PrintLogApi.Services;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// The write MCP tool surface. Every tool runs as the token-derived user (resolved from the
    /// token, never a tool argument), enforces ownership in the service query, bounds its blast
    /// radius on the server, and never mutates printer loaded-state. The class-level
    /// <see cref="AuthorizeAttribute"/> requires the write:printdata scope on top of the endpoint's
    /// authentication policy.
    /// </summary>
    [McpServerToolType]
    [Authorize(Policy = "McpWrite")]
    public class PrintLogWriteTools
    {
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IPrintService printService;
        private readonly IFilamentService filamentService;
        private readonly IProjectService projectService;

        public PrintLogWriteTools(
            IHttpContextAccessor httpContextAccessor,
            IPrintService printService,
            IFilamentService filamentService,
            IProjectService projectService)
        {
            this.httpContextAccessor = httpContextAccessor;
            this.printService = printService;
            this.filamentService = filamentService;
            this.projectService = projectService;
        }

        private long CurrentUserId =>
            McpUserContext.RequireUserId(httpContextAccessor.HttpContext!.User);

        [McpServerTool(Name = "whoami"), Description("Confirms write access is granted. Returns your internal user id.")]
        public long WhoAmI() => CurrentUserId;
    }
}
