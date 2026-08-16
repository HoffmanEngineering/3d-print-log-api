using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.OpenApi.Models;
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

namespace PrintLogApi;

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

        ConfigureResponseCompression(services);

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
            c.SwaggerDoc("v1", new OpenApiInfo
            {
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

        ConfigureHealthChecks(services);

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
            // IHttpContextAccessor is registered above, so the first dereference is safe.
            // HttpContext is not guaranteed: it would be null if IPrincipal were ever resolved
            // outside a request. Nothing resolves IPrincipal from DI today, so this factory
            // does not currently run at all. Both were already unguarded before nullable
            // analysis was enabled, so the null-forgive changes nothing.
            (sp) => sp.GetService<IHttpContextAccessor>()!.HttpContext!.User
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
        services.AddTransient<IFeedbackService, FeedbackService>();
        services.AddTransient<IMcpStatisticsService, McpStatisticsService>();
        services.AddScoped<Services.Analytics.IAnalyticsService, Services.Analytics.AnalyticsService>();
        services.AddScoped<Services.Analytics.IActivityAnalyticsService, Services.Analytics.ActivityAnalyticsService>();
        services.AddScoped<Services.Analytics.IPrinterAnalyticsService, Services.Analytics.PrinterAnalyticsService>();
        services.AddScoped<Services.Analytics.IMaterialAnalyticsService, Services.Analytics.MaterialAnalyticsService>();
        services.AddScoped<Services.Analytics.ICostAnalyticsService, Services.Analytics.CostAnalyticsService>();
        services.AddScoped<Services.Analytics.IAccuracyAnalyticsService, Services.Analytics.AccuracyAnalyticsService>();

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

            // Per-caller rate limiting for the REST controllers. Two separate budgets, because
            // the two populations behave nothing alike:
            //
            //  - Authenticated callers partition on the internal user id, which is exact. This
            //    is the budget that bounds a runaway slicer plugin or agent loop.
            //  - Anonymous callers partition on the remote IP. This bounds unauthenticated
            //    traffic to the public endpoints.
            //
            // Note what this policy does NOT cover: a request bearing an invalid API key never
            // reaches it. ApiKeyMiddleware short-circuits with a 401 and this middleware runs
            // later in the pipeline, so key guessing is throttled by that middleware's own
            // per-address failed-attempt guard (Api:InvalidApiKeyAttemptsPerMinute).
            //
            // Either budget is disabled by configuring it to 0 or less, which is how the
            // integration suite opts out (see appsettings.IntegrationTesting.json).
            options.AddPolicy("api", httpContext =>
            {
                var userId = httpContext.User.GetUserId();

                // Media endpoints get their own, much larger budget in their own partition.
                // A gallery page may hold up to 100 images, and on a cold cache the browser
                // requests all of them within a couple of seconds — so a handful of pages is
                // normal behaviour that would otherwise look exactly like a flood. Separate
                // partitions matter as much as the larger number: a burst of thumbnails must
                // not spend the budget the actual data calls on the same page need.
                var isMedia = httpContext.GetEndpoint()?.Metadata
                    .GetMetadata<MediaEndpointAttribute>() is not null;

                if (userId.HasValue)
                {
                    var authenticatedLimit = isMedia
                        ? Configuration.GetValue("Api:MediaRateLimitPerMinute", 1200)
                        : Configuration.GetValue("Api:RateLimitPerMinute", 300);

                    return authenticatedLimit <= 0
                        ? System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("api-user-unlimited")
                        : System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: isMedia ? $"api-user-media:{userId.Value}" : $"api-user:{userId.Value}",
                            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                            {
                                PermitLimit = authenticatedLimit,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                            });
                }

                var anonymousLimit = isMedia
                    ? Configuration.GetValue("Api:MediaRateLimitPerMinute", 1200)
                    : Configuration.GetValue("Api:AnonymousRateLimitPerMinute", 600);

                if (anonymousLimit <= 0)
                {
                    return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("api-anon-unlimited");
                }

                // NOTE: this is the socket peer, not necessarily the browser. Nothing in the
                // pipeline calls UseForwardedHeaders, so if the App Service front end terminates
                // the connection every anonymous caller collapses into one shared bucket. The
                // default is set high enough that a shared bucket still clears normal public
                // traffic; tune Api:AnonymousRateLimitPerMinute (or set it to 0) rather than
                // trusting a client-supplied X-Forwarded-For, which a brute-forcer would rotate.
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: isMedia ? $"api-anon-media:{ip}" : $"api-anon:{ip}",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = anonymousLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });
    }

    /// <summary>
    /// Brotli-first response compression for the JSON the browser and mobile clients read.
    ///
    /// ENABLED OVER HTTPS, WHICH IS NOT THE FRAMEWORK DEFAULT. <c>EnableForHttps</c> defaults to
    /// false because compressing a TLS response leaks plaintext length, which is the lever BREACH
    /// pulls. That attack needs three things at once: a response body that mixes attacker-chosen
    /// input with a secret, an attacker able to make the victim's client issue that request
    /// repeatedly, and a way to observe the resulting sizes. The middle one does not hold here.
    /// This API is token-authenticated — a bearer token or an X-Api-Key header/query value — and
    /// nothing is attached to a cross-site request ambiently the way a cookie is, so an attacker
    /// cannot cause the victim's browser to issue an authenticated request at all. (The CORS
    /// policy is AllowAnyOrigin *without* AllowCredentials, which is the same statement from the
    /// other direction.) Leaving the default in place would mean shipping compression that never
    /// runs in production, since everything outside Development is behind UseHttpsRedirection.
    ///
    /// The body-shape check the issue asked for came back clean. The <c>searchText</c> family of
    /// query parameters is the obvious candidate for reflected input, but no summary response
    /// echoes it: <c>PagedList</c> carries only <c>Paging</c> and <c>Items</c>. The one endpoint
    /// that does return a secret alongside caller-supplied text is
    /// <c>POST /api/UserApiKeys</c> — <c>NewUserApiKeyDto</c> carries the generated key next to
    /// the caller's own Description — and it is safe for the reason above rather than by
    /// accident: only the key's owner can ever elicit that response. If cookie authentication is
    /// ever added, this decision has to be revisited before it ships, and that endpoint is where
    /// to start.
    ///
    /// COMPRESSION LEVELS ARE MEASURED, NOT GUESSED. On a 52 KB payload shaped like a print
    /// summary page (200 rows of repetitive JSON):
    ///
    ///     brotli Fastest        9.0% of raw     0.15 ms
    ///     brotli Optimal        9.0% of raw     0.42 ms
    ///     brotli SmallestSize   6.1% of raw    90.19 ms   (never use this)
    ///     gzip   Fastest       14.4% of raw     0.09 ms
    ///     gzip   Optimal        9.3% of raw     0.30 ms
    ///
    /// Brotli quality 1 (Fastest) already matches what gzip only reaches at Optimal, so paying
    /// for a higher brotli level buys nothing — and SmallestSize costs 600x the CPU for three
    /// percentage points, which on a request thread is a self-inflicted outage.
    ///
    /// Both providers are therefore on Fastest, including gzip, where the tempting choice is
    /// Optimal: it sends 35% fewer bytes for 0.2 ms more. That trade was taken and then
    /// reversed, because the two populations it applies to are not the same. Gzip is only
    /// reached by clients that do not offer brotli — a shrinking set of real users — but it is
    /// also whichever codec an attacker names in Accept-Encoding, and they will always name the
    /// expensive one. Paying 3x the CPU on a path selected mostly by adversaries is the wrong
    /// side of that trade, and gzip Fastest still removes ~86% of the bytes.
    ///
    /// That reasoning is bounded by response size, and response size here is not bounded:
    /// PagedRequest.PageSize is an unconstrained int that flows into Take(), so a caller can
    /// ask any of the paged endpoints for an arbitrarily large body. Compression is not the
    /// origin of that (the SQL and the serialization on that path cost far more than any
    /// codec) and capping it is a caller-visible change that belongs in its own issue — but it
    /// is the reason not to spend extra CPU per byte here.
    ///
    /// Brotli is registered first so it wins when a client offers both at equal quality.
    ///
    /// MIME types are the framework defaults (which already include application/json) plus
    /// application/problem+json, the content type of every validation and error response MVC
    /// produces. Note what is deliberately absent: text/event-stream. Compressing a streaming
    /// body buffers it, and /mcp negotiates that content type for its Streamable HTTP
    /// responses — adding it here would stall an agent's tool call until the response ended.
    /// </summary>
    private static void ConfigureResponseCompression(IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json"]);
        });

        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
    }

    /// <summary>
    /// Two health checks with deliberately different jobs.
    ///
    /// "live" answers only "is this process serving requests", and is what Azure App Service
    /// is meant to poll. It must NOT touch the database: App Service pulls a failing instance
    /// from rotation and replaces it after a sustained failure, so a check that fails on a SQL
    /// blip would report every instance unhealthy at once and turn a recoverable database
    /// outage into a restart loop across the whole app.
    ///
    /// "ready" is the one that actually probes SQL Server — where knowing the database is
    /// unreachable is the entire point. Nothing polls it automatically today; it is for manual
    /// checks, and is the endpoint a post-deploy smoke test should call if one is ever added
    /// to the deploy workflow.
    /// </summary>
    private static void ConfigureHealthChecks(IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck(LiveCheckName, () => HealthCheckResult.Healthy("Process is serving requests."), tags: [LiveTag])
            .AddDbContextCheck<PrintLogContext>(ReadyCheckName, tags: [ReadyTag]);
    }

    /// <summary>
    /// Writes the readiness report as JSON: overall status plus one entry per check.
    ///
    /// Only the check name, status, and duration are emitted. A failing check's Exception and
    /// Description are deliberately left out — this endpoint is anonymous, and a SQL connection
    /// failure message carries the server name and often the credentials it tried.
    /// </summary>
    private static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }

    private const string LiveTag = "live";
    private const string ReadyTag = "ready";
    private const string LiveCheckName = "self";
    private const string ReadyCheckName = "database";

    private void ConfigureMcpServer(IServiceCollection services)
    {
        services.AddSingleton<Mcp.IMcpToolTelemetry, Mcp.McpToolTelemetry>();

        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .AddAuthorizationFilters()
            .WithTools<Mcp.PrintLogReadTools>()
            .WithTools<Mcp.PrintLogWriteTools>()
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
                    catch (System.Exception ex) when (ex is System.ArgumentException or System.Text.Json.JsonException)
                    {
                        // Argument binding/shape failures from the MCP SDK (missing required
                        // parameter, wrong type, unknown property). Our own validation always
                        // throws McpToolException, so a raw ArgumentException/JsonException here
                        // describes the caller's payload, not an internal fault. Surface a fixed,
                        // detail-free hint so the caller corrects the call instead of retrying.
                        Record("error");
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "invalid_arguments: One or more arguments were invalid — check the tool's parameter names and types." }],
                            IsError = true,
                        };
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
            // null displayName is the supported "no display name" value; the SDK's parameter
            // is simply not annotated as nullable.
            .AddMcp("McpChallenge", null!, options =>
            {
                options.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = Configuration["Auth0:McpIdentifier"]!,
                    AuthorizationServers = { domain },
                    ScopesSupported = { "read:printdata", "write:printdata" },
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

            // MCP endpoint baseline: the dedicated MCP bearer (or dev bypass), a mapped internal
            // user, AND at least one MCP data scope (read OR write). The specific read/write gate
            // is applied per tool class (McpRead / McpWrite), so a write-only agent still reaches
            // the endpoint but a completely unscoped token cannot even list tools.
            options.AddPolicy("Mcp", policy =>
            {
                policy.AuthenticationSchemes.Add(bypassAuth ? "DevAuth" : "Mcp");
                policy.Requirements.Add(new McpUserRequirement());
                policy.RequireAssertion(ctx =>
                {
                    if (bypassAuth)
                    {
                        return true; // dev bypass token carries no scopes
                    }

                    var scopes = (ctx.User.FindFirst("scope")?.Value ?? string.Empty).Split(' ');
                    return scopes.Contains("read:printdata") || scopes.Contains("write:printdata");
                });
            });

            // Read tools: baseline + the read:printdata scope.
            options.AddPolicy("McpRead", policy =>
            {
                policy.AuthenticationSchemes.Add(bypassAuth ? "DevAuth" : "Mcp");
                policy.Requirements.Add(new HasScopeRequirement("read:printdata", domain));
                policy.Requirements.Add(new McpUserRequirement());
            });

            // Write tools: baseline + the write:printdata scope. A write agent SHOULD also
            // request read:printdata to use the read tools, but is not required to for writes.
            options.AddPolicy("McpWrite", policy =>
            {
                policy.AuthenticationSchemes.Add(bypassAuth ? "DevAuth" : "Mcp");
                policy.Requirements.Add(new HasScopeRequirement("write:printdata", domain));
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

        // Inside the developer exception page so it sees an aborted request first and can
        // absorb it, and outside everything below so it covers any endpoint that awaits a
        // slow query — not just analytics, where the abort race was first noticed.
        app.UseClientAbortHandling();

        // Outside every middleware that writes a response body, which is the actual constraint:
        // this swaps IHttpResponseBodyFeature for a wrapper, so anything that has already
        // started writing would bypass it. That puts it above routing, endpoints, Swagger and
        // /metrics, and below the HTTPS redirect (no point compressing a 307 with no body).
        //
        // It also sits inside UseClientAbortHandling so that anything thrown while finalizing
        // the compressed frame passes back through that handler. Do not read more into that
        // than it says. ResponseCompressionMiddleware calls FinishCompressionAsync inside its
        // try, NOT in a finally (verified against dotnet/aspnetcore release/10.0), so when a
        // downstream abort throws, finalization is skipped and compression adds no exception of
        // its own. The case this ordering covers is the narrower one where the handler ran to
        // completion but the socket is already gone. Even then the coverage is partial:
        // ClientAbortMiddleware matches only OperationCanceledException and
        // InvalidOperationException, so an IOException surfacing from a dead-socket write would
        // still escape. Widening that is a change to that middleware, not to this line.
        //
        // Ordering against UseHttpMetrics was checked (issue #66 raised it): prometheus-net 8's
        // UseHttpMetrics records request counts, in-flight gauge and duration — it has no
        // response-size metric, so no recorded byte count can be distorted by compression.
        // Duration is a different story and worth stating accurately: the codec runs during the
        // WriteAsync calls the endpoint makes, which happen while UseHttpMetrics is still
        // awaiting, so request duration INCLUDES compression. Only the trailing finalization
        // falls outside it. At the measured 0.15 ms that is under the noise floor of these
        // handlers, but a dashboard reading duration is not reading pure handler latency.
        app.UseResponseCompression();

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
            endpoints.MapControllers().RequireRateLimiting("api");

            // Liveness: the path to configure under App Service > Monitoring > Health check.
            // Plain text, no dependencies — see ConfigureHealthChecks for why it stays shallow.
            endpoints.MapHealthChecks("/health", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(LiveTag),
            }).AllowAnonymous();

            // Readiness: probes SQL Server and reports per-check detail. Used by people and by
            // the deploy workflow, not by the platform's own restart logic.
            endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(ReadyTag),
                ResponseWriter = WriteHealthResponse,
            }).AllowAnonymous();

            // MCP endpoint (Streamable HTTP, stateless). The endpoint-level "Mcp" policy
            // enforces the dedicated MCP bearer + a mapped user before dispatch; the read/write
            // scope gate is applied per tool class (McpRead / McpWrite).
            endpoints.MapMcp("/mcp").RequireAuthorization("Mcp").RequireRateLimiting("mcp");
        });

    }
}

public class AuthorizeCheckOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var hasAuthorize =
          // DeclaringType is never null for a controller action method.
          context.MethodInfo.DeclaringType!.GetCustomAttributes(true).OfType<AuthorizeAttribute>().Any()
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
