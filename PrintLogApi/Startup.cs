using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication;

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
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);

            ConfigureAuthentication(services);

            services.AddSingleton<IAuthorizationHandler, HasScopeHandler>();
        }

        private void ConfigureAuthentication(IServiceCollection services)
        {
            string domain = $"https://{Configuration["Auth0:Domain"]}/";
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
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseAuthentication();
            app.UseMvc();

            
        }
    }
}
