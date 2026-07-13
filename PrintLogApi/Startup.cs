using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.OpenApi.Models;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using PrintLogApi.Authentication;
using PrintLogApi.Authentication.Handlers;
using PrintLogApi.Extensions;
using PrintLogApi.Models.Smtp;
using PrintLogApi.Models.Stripe;
using PrintLogApi.Services;
using PrintLogApi.TestData;
using PrintLogApi.Users;
using Prometheus;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrintLogApi
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            Configuration = configuration;
            Environment = env;
        }

        public IConfiguration Configuration { get; }
        public IWebHostEnvironment Environment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();

            services.AddAutoMapper(cfg => cfg.AddMaps(typeof(Startup).Assembly));

            services.AddCors();

            services.AddHttpClient();

            ConfigureAuthentication(services);

            services.AddDbContext<PrintLogContext>(opts =>
            {
                opts.UseSqlServer(
                    Configuration["ConnectionString:PrintLogDb"],
                    sqlServerOptionsAction: sqlOptions =>
                    {
                        sqlOptions.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(30),
                            errorNumbersToAdd: null);
                    });
            });

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { 
                    Title = "3D Print Log Api", 
                    Version = "v1",
                    Description = @"HTTP API powering <https://3dprintlog.com>, allowing users to manage their prints, printers, and filaments.

For additional documentation, please visit <https://www.3dprintlog.com/docs/getting-started>. Please contact us at <hello@3dprintlog.com> with any questions or comments.

Authentication can be done using a personal API Key. After creating an account on the 3D Print Log website, create an API key using the following:
- Navigate to the [Personal Api Keys](https://www.3dprintlog.com/api-keys) page by clicking on your User Profile Picture at the top-left, and selecting ""Personal Api Keys"".
- Click Create new API Key.
- Enter a new description(such a ""API Access Key"").
- Click Submit to generate a new key.
- Copy the new 32 - character key.
   - Note: The API Key cannot be retrieved after you leave the page, so copy it to a secure location, otherwise you will have to generate a new key

The API key can be used either by adding a **X-Api-Key header** with the key, or by including a **api_key query param** to each request.
",
                    Contact = new OpenApiContact
                    {
                        Email = "hello@3dprintlog.com",
                        Name = "Christopher Hoffman",
                        Url = new Uri("https://www.hoffman.engineering")
                    }
                });

                c.CustomOperationIds(apiDesc =>
                {
                    return apiDesc.TryGetMethodInfo(out MethodInfo methodInfo) ? methodInfo.Name : null;
                });

                c.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.OAuth2,
                    Flows = new OpenApiOAuthFlows
                    {
                        Implicit = new OpenApiOAuthFlow
                        {
                            AuthorizationUrl = new Uri($"https://{Configuration["Auth0:Domain"]}/authorize"),
                            
                            Scopes = new Dictionary<string, string>
                            {
                                {"api1", "Demo API - full access"}
                            }
                        }
                    },
                    In = ParameterLocation.Header,
                    Scheme = "bearer",
                    BearerFormat = "JWT"
                });

                c.AddSecurityDefinition("apikey", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    
                    In = ParameterLocation.Header,
                    Name = "X-Api-Key"
                });

                c.OperationFilter<AuthorizeCheckOperationFilter>();

                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                c.IncludeXmlComments(xmlPath, true);

                c.CustomSchemaIds(type => type.ToString());

            });

            // Configure memory cache with size limit
            services.AddMemoryCache(options =>
            {
                options.SizeLimit = 8192; // 8192 "units" - each unit ~= 1KB
                options.CompactionPercentage = 0.25; // Remove 25% of entries when limit reached
                options.ExpirationScanFrequency = TimeSpan.FromMinutes(2);
            });

            services.AddSingleton<IAuthorizationHandler, HasScopeHandler>();
            services.AddTransient<IClaimsTransformation, ClaimsTransformer>();
            services.AddHttpContextAccessor();
            services.AddScoped<IPrincipal>(
                (sp) => sp.GetService<IHttpContextAccessor>().HttpContext.User
            );
            services.AddTransient<IUserService, UserService>();
            services.AddTransient<IPrintService, PrintService>();
            services.AddTransient<IPrinterService, PrinterService>();
            services.AddTransient<IPrinterCategoryService, PrinterCategoryService>();
            services.AddTransient<ICommentService, CommentService>();
            services.AddTransient<IPrintImageService, PrintImageService>();
            services.AddTransient<IFilamentService, FilamentService>();
            services.AddTransient<IUserApiKeyService, UserApiKeyService>();
            services.AddTransient<IUserDeletionService, UserDeletionService>();
            services.AddTransient<IAuth0Service, Auth0Service>();
            services.AddTransient<IPrinterMaintenanceService, PrinterMaintenanceService>();
            services.AddTransient<INotificationService, NotificationService>();
            services.AddTransient<ISubscriptionService, SubscriptionService>();
            services.AddTransient<IFileAttachmentService, FileAttachmentService>();
            services.AddTransient<IProjectService, ProjectService>();
            services.AddTransient<IMcpStatisticsService, McpStatisticsService>();

            services.AddTransient<IBlobStorageService, AzureBlobStorageService>();

            services.AddSingleton<ICacheVersionService, CacheVersionService>();
            services.AddApplicationInsightsTelemetry();


            services.AddTransient<IEmailSender, SmtpEmailSender>();
            services.Configure<SmtpEmailSenderOptions>(options =>
            {
                options.Host = Configuration["ExternalProviders:Smtp:Host"];
                options.Port = int.Parse(Configuration["ExternalProviders:Smtp:Port"] ?? "587");
                options.Username = Configuration["ExternalProviders:Smtp:Username"];
                options.Password = Configuration["ExternalProviders:Smtp:Password"];
                options.SenderEmail = Configuration["ExternalProviders:Smtp:SenderEmail"];
                options.SenderName = Configuration["ExternalProviders:Smtp:SenderName"];
            });

            services.Configure<StripeOptions>(Configuration.GetSection("Stripe"));
            Stripe.StripeConfiguration.ApiKey = Configuration["Stripe:SecretKey"];

            ConfigureMcpServer(services);

            // Per-user rate limiting for /mcp. The unit is HTTP requests (not tool calls); the
            // budget is partitioned by the authenticated internal user id.
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = (context, _) =>
                {
                    if (context.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return System.Threading.Tasks.ValueTask.CompletedTask;
                };
                options.AddPolicy("mcp", httpContext =>
                    System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: httpContext.User.GetUserId()?.ToString() ?? "anon",
                        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                        {
                            PermitLimit = Configuration.GetValue("Mcp:RateLimitPerMinute", 60),
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }));
            });
        }

        private void ConfigureMcpServer(IServiceCollection services)
        {
            services.AddSingleton<Mcp.IMcpToolTelemetry, Mcp.McpToolTelemetry>();

            services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .AddAuthorizationFilters()
                .WithTools<Mcp.PrintLogTools>()
                .WithRequestFilters(requestFilters =>
                {
                    // Single choke point for tool errors AND telemetry: map our typed codes to safe
                    // IsError results, replace any other exception with a generic detail-free message,
                    // and record Mcp_ToolCalled with only non-sensitive fields.
                    requestFilters.AddCallToolFilter(next => async (context, cancellationToken) =>
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var toolName = context.Params?.Name ?? "unknown";
                        var telemetry = context.Services?.GetService<Mcp.IMcpToolTelemetry>();
                        var http = context.Services?.GetService<IHttpContextAccessor>();
                        var subjectHash = Mcp.McpUserContext.HashSubject(http?.HttpContext?.User);

                        void Record(string outcome) =>
                            telemetry?.ToolCalled(toolName, outcome, stopwatch.ElapsedMilliseconds, subjectHash);

                        try
                        {
                            var result = await next(context, cancellationToken);
                            Record(result.IsError == true ? "error" : "success");
                            return result;
                        }
                        catch (Mcp.McpToolException ex)
                        {
                            Record("error");
                            return new ModelContextProtocol.Protocol.CallToolResult
                            {
                                Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = $"{ex.Code}: {ex.Message}" }],
                                IsError = true,
                            };
                        }
                        catch (System.OperationCanceledException)
                        {
                            throw;
                        }
                        catch (System.Exception)
                        {
                            Record("error");
                            return new ModelContextProtocol.Protocol.CallToolResult
                            {
                                Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "An unexpected error occurred while processing the tool call." }],
                                IsError = true,
                            };
                        }
                    });
                });
        }

        private void ConfigureAuthentication(IServiceCollection services)
        {
            var domain = $"https://{Configuration["Auth0:Domain"]}/";
            var bypassAuth = Environment.IsDevelopment() || Environment.IsEnvironment("E2ETesting");

            if (bypassAuth)
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "DevAuth";
                    options.DefaultChallengeScheme = "DevAuth";
                })
                .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>("DevAuth", null);
            }
            else
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                // Default bearer scheme accepts ONLY the app API audience.
                .AddJwtBearer(jwtOptions =>
                {
                    jwtOptions.Authority = domain;
                    jwtOptions.Audience = Configuration["Auth0:ApiIdentifier"];
                })
                // Isolated MCP bearer accepts ONLY the dedicated MCP audience. Never accept
                // MCP-audience tokens through the default scheme, nor app tokens through this one.
                .AddJwtBearer("McpBearer", jwtOptions =>
                {
                    jwtOptions.Authority = domain;
                    jwtOptions.Audience = Configuration["Auth0:McpIdentifier"];
                })
                // Policy scheme the McpAccess policy authenticates against. Challenges are routed
                // to the RFC 9728 metadata-advertising scheme so 401s reference protected-resource
                // metadata; forbids stay on McpBearer to emit a plain 403.
                .AddPolicyScheme("Mcp", null, options =>
                {
                    options.ForwardAuthenticate = "McpBearer";
                    options.ForwardChallenge = "McpChallenge";
                    options.ForwardForbid = "McpBearer";
                })
                // RFC 9728 protected-resource metadata for the dedicated MCP resource.
                .AddMcp("McpChallenge", null, options =>
                {
                    options.ResourceMetadata = new ProtectedResourceMetadata
                    {
                        Resource = Configuration["Auth0:McpIdentifier"]!,
                        AuthorizationServers = { domain },
                        ScopesSupported = { "read:printdata" },
                    };
                });

                // Scope-based policy only applies outside Development (dev bypass token has no scopes)
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("read:messages", policy =>
                        policy.Requirements.Add(new HasScopeRequirement("read:messages", domain)));
                });
            }

            // These policies use custom requirements (not scope-based) and are needed in all environments
            services.AddAuthorization(options =>
            {
                options.AddPolicy("ViewPrint", policy =>
                    policy.Requirements.Add(new PublicOrCreatorRequirement()));

                options.AddPolicy("ViewUserProfile", policy =>
                    policy.Requirements.Add(new PublicOrUnlistedUserProfileRequirement()));

                // MCP access: the dedicated MCP bearer (or dev bypass), the read:printdata
                // scope, AND a mapped internal user. Registered in every environment.
                options.AddPolicy("McpAccess", policy =>
                {
                    policy.AuthenticationSchemes.Add(bypassAuth ? "DevAuth" : "Mcp");
                    policy.Requirements.Add(new HasScopeRequirement("read:printdata", domain));
                    policy.Requirements.Add(new McpUserRequirement());
                });
            });

            services.AddSingleton<IAuthorizationHandler, PrintViewStatusAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, UserProfileViewStatusAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, McpUserHandler>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, PrintLogContext context)
        {
            if (env.IsEnvironment("E2ETesting"))
            {
                context.Database.EnsureDeleted(); // Delete the test database
            }

            // Apply migrations for non-production environments (Production applies via pipeline)
            if (!env.IsEnvironment("IntegrationTesting") && !env.IsProduction())
            {
                context.Database.Migrate();
            }

            if (env.IsEnvironment("E2ETesting"))
            {
                E2EDataSeeder.Seed(context);
            }

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            app.UseCors(builder =>
            {
                builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            });

            

            app.UseRouting();

            app.UseAuthentication();
            app.UseApiKeyAuthentication();
            app.UseAuthorization();
            // After authorization so the /mcp partition key sees the authenticated MCP user and
            // unauthenticated requests are rejected (401/403) before consuming any budget.
            app.UseRateLimiter();
            // Map the Auth0 user id to the Upn, so we can add in our custom user ID as the NameIdentifier later.
            JsonWebTokenHandler.DefaultMapInboundClaims = true;
            JsonWebTokenHandler.DefaultInboundClaimTypeMap[JwtRegisteredClaimNames.Sub] = ClaimTypes.Upn;

            // Enable middleware to serve generated Swagger as a JSON endpoint.
            app.UseSwagger();

            // Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.),
            // specifying the Swagger JSON endpoint.
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Print Log API V1");
                c.OAuthClientId(Configuration["Auth0:SwaggerClientId"]);
                c.OAuthAdditionalQueryStringParams(new Dictionary<string, string>() { { "audience", "https://dev.3dprintlog.com/api" } });
            });

            app.UseMetricServer();
            app.UseHttpMetrics();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();

                // Read-only MCP endpoint (Streamable HTTP, stateless). The McpAccess policy
                // enforces the dedicated MCP bearer + read:printdata + a mapped user before dispatch.
                endpoints.MapMcp("/mcp").RequireAuthorization("McpAccess").RequireRateLimiting("mcp");
            });

        }
    }

    public class AuthorizeCheckOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var hasAuthorize =
              context.MethodInfo.DeclaringType.GetCustomAttributes(true).OfType<AuthorizeAttribute>().Any()
              || context.MethodInfo.GetCustomAttributes(true).OfType<AuthorizeAttribute>().Any();

            if (hasAuthorize)
            {
                if (!operation.Responses.Any(kvp => kvp.Key == "401"))
                {
                    operation.Responses.Add("401", new OpenApiResponse { Description = "Unauthorized" });
                }

                if (!operation.Responses.Any(kvp => kvp.Key == "403"))
                {
                    operation.Responses.Add("403", new OpenApiResponse { Description = "Forbidden" });
                }
                

                operation.Security = new List<OpenApiSecurityRequirement>
            {
                new OpenApiSecurityRequirement
                {
                    [
                        new OpenApiSecurityScheme {Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "oauth2"}
                        }
                    ] = new[] {"api1"}
                },
                new OpenApiSecurityRequirement
                {
                    [
                        new OpenApiSecurityScheme {
                            Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "apikey"}
                        }
                    ] = new[] {"api1"}
                }
            };

            }
        }
    }
}
