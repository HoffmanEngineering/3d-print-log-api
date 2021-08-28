using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Principal;
using AutoMapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using PrintLogApi.Authentication;
using PrintLogApi.Authentication.Handlers;
using PrintLogApi.Extensions;
using PrintLogApi.Models.SendGrid;
using PrintLogApi.Services;
using PrintLogApi.TestData;
using PrintLogApi.Users;
using Prometheus;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrintLogApi
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();

            services.AddAutoMapper(typeof(Startup));

            services.AddCors();

            services.AddHttpClient();

            ConfigureAuthentication(services);

            services.AddDbContext<PrintLogContext>(opts =>
            {
                opts.UseSqlServer(Configuration["ConnectionString:PrintLogDb"]);
            });

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { 
                    Title = "3D Print Log Api", 
                    Version = "v1",
                    Description = "API powering https://3dprintlog.com, allowing users to manage their prints, printers, and filaments.",
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
                c.IncludeXmlComments(xmlPath);

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
            services.AddTransient<ICommentService, CommentService>();
            services.AddTransient<IPrintImageService, PrintImageService>();
            services.AddTransient<IFilamentService, FilamentService>();
            services.AddTransient<IUserApiKeyService, UserApiKeyService>();
            services.AddTransient<IUserDeletionService, UserDeletionService>();
            services.AddTransient<IAuth0Service, Auth0Service>();
            services.AddApplicationInsightsTelemetry();


            services.AddTransient<IEmailSender, SendGridEmailSender>();
            services.Configure<SendGridEmailSenderOptions>(options =>
            {
                options.ApiKey = Configuration["ExternalProviders:SendGrid:ApiKey"];
                options.SenderEmail = Configuration["ExternalProviders:SendGrid:SenderEmail"];
                options.SenderName = Configuration["ExternalProviders:SendGrid:SenderName"];
            });

        }

        private void ConfigureAuthentication(IServiceCollection services)
        {
            var domain = $"https://{Configuration["Auth0:Domain"]}/";
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwtOptions =>
            {
                jwtOptions.Authority = domain;
                jwtOptions.Audience = Configuration["Auth0:ApiIdentifier"];

                //jwtOptions.Events = new JwtBearerEvents
                //{
                //    OnAuthenticationFailed = c =>
                //    {
                //        c.NoResult();
                //        c.Response.StatusCode = 401;
                //        c.Response.ContentType = "text/plain";
                //        c.Response.WriteAsync(c.Exception.ToString()).Wait();
                //        return Task.CompletedTask;
                //    },
                //    OnTokenValidated = async ctx =>
                //    {
                //        var clientId = ctx.Principal.FindFirst("appid");
                //    }
                //};
            });

            services.AddAuthorization(options =>
            {
                options.AddPolicy("read:messages", policy =>
                {
                    policy.Requirements.Add(new HasScopeRequirement("read:messages", domain));
                });

                options.AddPolicy("ViewPrint", policy =>
                    policy.Requirements.Add(new PublicOrCreatorRequirement()));

                options.AddPolicy("ViewUserProfile", policy =>
                    policy.Requirements.Add(new PublicOrUnlistedUserProfileRequirement()));
            });

            services.AddSingleton<IAuthorizationHandler, PrintViewStatusAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, UserProfileViewStatusAuthorizationHandler>();

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, PrintLogContext context)
        {
            if (env.IsEnvironment("E2ETesting"))
            {
                context.Database.EnsureDeleted(); // Delete the test database
            }

            // Automatically apply any migrations.
            context.Database.Migrate();

            if (env.IsEnvironment("E2ETesting"))
            {
                DataSeeder.Seed(context);
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
            // Map the Auth0 user id to the Upn, so we can add in our custom user ID as the NameIdentifier later.
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap[JwtRegisteredClaimNames.Sub] = ClaimTypes.Upn;

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
