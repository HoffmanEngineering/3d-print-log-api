using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrintLogApi.OpenApi;

/// <summary>
/// Generation of the OpenAPI document served at <c>/swagger/v1/swagger.json</c> (#126).
///
/// The document is read by agents turning it into function-calling tools, not only by people in
/// Swagger UI, so it has to be valid OpenAPI rather than merely renderable.
/// <c>OpenApiDocumentTests</c> pins every rule here, and CI lints the generated document with
/// Redocly.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentName = "v1";

    public const string OAuthScheme = "oauth2";
    public const string ApiKeyHeaderScheme = "apikey";
    public const string ApiKeyQueryScheme = "apikeyQuery";

    /// <summary>
    /// A second route on <c>MaterialTypesController</c>, kept for the web app. Documenting it
    /// would publish the same operation twice under two ids, and "Materials" is the name the
    /// product uses for filament, so an agent reading it would reach for the wrong endpoint.
    /// </summary>
    private static readonly HashSet<string> UndocumentedRouteAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "api/Materials",
    };

    private const string Description = @"HTTP API powering <https://www.3dprintlog.com>, allowing users to manage their prints, printers, and filaments.

For additional documentation, please visit <https://www.3dprintlog.com/docs/getting-started>. Please contact us at <hello@3dprintlog.com> with any questions or comments.

## Authentication

Requests authenticate as a user, either with a personal API key or with an OAuth 2.0 access token.

### Personal API key

After creating an account on the 3D Print Log website, create an API key using the following:
- Navigate to the [Personal Api Keys](https://www.3dprintlog.com/api-keys) page by clicking on your User Profile Picture at the top-left, and selecting ""Personal Api Keys"".
- Click Create new API Key.
- Enter a new description (such as ""API Access Key"").
- Click Submit to generate a new key.
- Copy the new 32-character key.
   - Note: The API Key cannot be retrieved after you leave the page, so copy it to a secure location, otherwise you will have to generate a new key

The API key can be used either by adding a **X-Api-Key header** with the key, or by including a **api_key query param** to each request. A few account-management endpoints (registering push devices, reading the account's email address) accept only an OAuth token, and say so in their security requirements.

### OAuth 2.0

The authorization code flow with PKCE, against the authorization server in the `oauth2` security scheme. Send the access token as `Authorization: Bearer <token>`.

### AI agents

Agents should prefer the MCP server at <https://api.3dprintlog.com/mcp>, which is built for tool calling and advertises its own OAuth metadata (RFC 9728).
";

    public static IServiceCollection AddPrintLogOpenApi(this IServiceCollection services, IConfiguration configuration)
    {
        var authority = $"https://{configuration["Auth0:Domain"]}";

        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "3D Print Log Api",
                Version = DocumentName,
                Description = Description,
                Contact = new OpenApiContact
                {
                    Email = "hello@3dprintlog.com",
                    Name = "Christopher Hoffman",
                    Url = new Uri("https://www.hoffman.engineering")
                },
                License = new OpenApiLicense
                {
                    Name = "AGPL-3.0",
                    Url = new Uri("https://github.com/HoffmanEngineering/3d-print-log-api/blob/main/LICENSE")
                },
            });

            c.DocInclusionPredicate((documentName, apiDesc) =>
                (apiDesc.GroupName == null || apiDesc.GroupName == documentName)
                && !UndocumentedRouteAliases.Contains(apiDesc.RelativePath ?? string.Empty));

            c.CustomOperationIds(OperationId);

            c.AddSecurityDefinition(OAuthScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = $"OAuth 2.0 through Auth0. Use the authorization code flow with PKCE, and pass "
                    + $"`audience={configuration["Auth0:ApiIdentifier"]}` to the authorization endpoint, or the "
                    + "issued token is not one this API accepts. The scopes listed are the OpenID Connect ones; "
                    + "this API grants a token's user full access to their own data and checks no scope of its own.",
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl = new Uri($"{authority}/authorize"),
                        TokenUrl = new Uri($"{authority}/oauth/token"),
                        RefreshUrl = new Uri($"{authority}/oauth/token"),
                        Scopes = new Dictionary<string, string>
                        {
                            { "openid", "Sign in, and identify the user." },
                            { "profile", "Read the user's name and picture." },
                            { "email", "Read the user's email address." },
                            { "offline_access", "Issue a refresh token." },
                        }
                    }
                },
            });

            c.AddSecurityDefinition(ApiKeyHeaderScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Description = "A personal API key, created at https://www.3dprintlog.com/api-keys.",
                In = ParameterLocation.Header,
                Name = "X-Api-Key"
            });

            c.AddSecurityDefinition(ApiKeyQueryScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Description = "The same personal API key, for clients that cannot set a header.",
                In = ParameterLocation.Query,
                Name = "api_key"
            });

            c.OperationFilter<SecurityRequirementsOperationFilter>();

            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            c.IncludeXmlComments(xmlPath, true);

            c.CustomSchemaIds(SchemaId);
        });

        return services;
    }

    /// <summary>
    /// Points the document's <c>servers</c> at the configured public base URL, so a client can
    /// read where to send requests from the document itself. Falls back to the request's own
    /// origin where none is configured (local development).
    /// </summary>
    public static void UsePrintLogOpenApi(this IApplicationBuilder app, IConfiguration configuration)
    {
        var serverUrl = configuration["OpenApi:ServerUrl"];

        app.UseSwagger(options =>
        {
            options.PreSerializeFilters.Add((document, request) =>
            {
                document.Servers =
                [
                    new OpenApiServer
                    {
                        Url = string.IsNullOrWhiteSpace(serverUrl)
                            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
                            : serverUrl,
                    }
                ];
            });
        });

        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", "Print Log API V1");
            c.OAuthClientId(configuration["Auth0:SwaggerClientId"]);
            c.OAuthUsePkce();
            c.OAuthScopes("openid", "profile", "email");
            c.OAuthAdditionalQueryStringParams(new Dictionary<string, string>
            {
                { "audience", configuration["Auth0:ApiIdentifier"] ?? string.Empty }
            });
        });
    }

    /// <summary>
    /// <c>{Controller}_{Action}</c>. The bare method name collided across controllers
    /// (<c>GetMaterials</c> on five of them), and OpenAPI requires operation ids to be unique —
    /// a tool generator keyed on them silently drops or overwrites all but one.
    /// </summary>
    internal static string? OperationId(Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription apiDesc) =>
        apiDesc.ActionDescriptor is ControllerActionDescriptor action
            ? $"{action.ControllerName}_{action.ActionName}"
            : null;

    /// <summary>
    /// The full type name, made legal as an OpenAPI component name (<c>^[a-zA-Z0-9.\-_]+$</c>).
    /// <c>Type.ToString()</c>, used before, emitted <c>+</c> for nested types and
    /// <c>`1[...]</c> for generics, which made 15 component names invalid.
    /// </summary>
    internal static string SchemaId(Type type)
    {
        if (!type.IsGenericType)
        {
            return (type.FullName ?? type.Name).Replace('+', '_');
        }

        var definition = type.GetGenericTypeDefinition();
        var name = definition.FullName ?? definition.Name;
        name = name[..name.IndexOf('`')].Replace('+', '_');
        return $"{name}Of{string.Join("And", type.GetGenericArguments().Select(SchemaId))}";
    }
}

/// <summary>
/// Declares, per operation, which credentials it accepts — derived from the same authorization
/// metadata the endpoint enforces, so the document cannot drift from it.
///
/// No operation requires a scope: the REST API grants a token's user access to their own data
/// and enforces no scope of its own. <c>read:printdata</c>/<c>write:printdata</c> belong to the
/// separate MCP audience, so advertising them here would describe a check that is not made.
/// </summary>
public class SecurityRequirementsOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var authorizeData = metadata.OfType<IAuthorizeData>().ToList();
        if (authorizeData.Count == 0)
        {
            return;
        }

        var allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();

        // ApiKeyMiddleware signs an API key in under the "ApiUser" role, which this policy
        // rejects. Only an interactive (OAuth) session gets through.
        var interactiveOnly = authorizeData.Any(a => a.Policy == "InteractiveUserOnly");

        if (!allowsAnonymous)
        {
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Unauthorized" });
            operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Forbidden" });
        }

        var requirements = new List<OpenApiSecurityRequirement>();

        // An empty requirement is how OpenAPI says "no credentials needed": these endpoints
        // serve public data anonymously and the owner's private data when authenticated.
        if (allowsAnonymous)
        {
            requirements.Add(new OpenApiSecurityRequirement());
        }

        requirements.Add(Requirement(OpenApiSetup.OAuthScheme));

        if (!interactiveOnly)
        {
            requirements.Add(Requirement(OpenApiSetup.ApiKeyHeaderScheme));
            requirements.Add(Requirement(OpenApiSetup.ApiKeyQueryScheme));
        }

        operation.Security = requirements;
    }

    private static OpenApiSecurityRequirement Requirement(string schemeId) => new()
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = schemeId }
        }] = []
    };
}
