using System.IdentityModel.Tokens.Jwt;
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
using PrintLogApi.Services;
using PrintLogApi.TestData;
using PrintLogApi.Users;
using Prometheus;

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

            ConfigureAuthentication(services);

            services.AddDbContext<PrintLogContext>(opts =>
            {
                opts.UseLazyLoadingProxies();
                opts.UseSqlServer(Configuration["ConnectionString:PrintLogDb"]);
            });

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Print Log Api", Version = "v1" });
            });

            services.AddSingleton<IAuthorizationHandler, HasScopeHandler>();
            services.AddTransient<IClaimsTransformation, ClaimsTransformer>();
            services.AddHttpContextAccessor();
            services.AddScoped<IPrincipal>(
                (sp) => sp.GetService<IHttpContextAccessor>().HttpContext.User
            );
            services.AddTransient<UserService>();
            services.AddTransient<IPrintService, PrintService>();
            services.AddTransient<ICommentService, CommentService>();
            services.AddTransient<IPrintImageService, PrintImageService>();
            services.AddApplicationInsightsTelemetry();

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
            }

            app.UseCors(builder =>
            {
                builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            });

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthentication();
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

            });

            app.UseMetricServer();
            app.UseHttpMetrics();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

        }
    }
}
