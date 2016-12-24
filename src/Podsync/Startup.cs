﻿using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Podsync.Services;
using Podsync.Services.Builder;
using Podsync.Services.Links;
using Podsync.Services.Patreon;
using Podsync.Services.Resolver;
using Podsync.Services.Storage;
using Podsync.Services.Videos.YouTube;

namespace Podsync
{
    public class Startup
    {
        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", true, true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", true)
                .AddEnvironmentVariables();

            if (env.IsDevelopment())
            {
                builder.AddUserSecrets();
            }

            Configuration = builder.Build();
        }

        public IConfigurationRoot Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<PodsyncConfiguration>(Configuration.GetSection("Podsync"));

            // Register core services
            services.AddSingleton<ILinkService, LinkService>();
            services.AddSingleton<IYouTubeClient, YouTubeClient>();
            services.AddSingleton<IResolverService, YtdlWrapper>();
            services.AddSingleton<IStorageService, RedisStorage>();
            services.AddSingleton<IRssBuilder, CompositeRssBuilder>();
            services.AddSingleton<IPatreonApi, PatreonApi>();

            // Add authentication services
            services.AddAuthentication(config => config.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme);

            // Add framework services
            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseBrowserLink();
            }

            app.UseStaticFiles();

            app.UseCookieAuthentication(new CookieAuthenticationOptions
            {
                AutomaticAuthenticate = true,
                AutomaticChallenge = true,
                LoginPath = new PathString("/login"),
                LogoutPath = new PathString("/logout")
            });

            // Patreon authentication
            app.UseOAuthAuthentication(new OAuthOptions
            {
                AuthenticationScheme = PatreonConstants.AuthenticationScheme,

                ClientId = Configuration[$"Podsync:{nameof(PodsyncConfiguration.PatreonClientId)}"],
                ClientSecret = Configuration[$"Podsync:{nameof(PodsyncConfiguration.PatreonSecret)}"],

                CallbackPath = new PathString("/oauth-patreon"),

                AuthorizationEndpoint = PatreonConstants.AuthorizationEndpoint,
                TokenEndpoint = PatreonConstants.TokenEndpoint,

                SaveTokens = true,

                Scope = { "users", "pledges-to-me", "my-campaign" },

                Events = new OAuthEvents
                {
                    OnCreatingTicket = async context =>
                    {
                        var patreonApi = app.ApplicationServices.GetService<IPatreonApi>();

                        var tokens = new Tokens
                        {
                            AccessToken = context.AccessToken,
                            RefreshToken = context.RefreshToken
                        };

                        var user = await patreonApi.FetchUserAndPledges(tokens);

                        context.Identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id));
                        context.Identity.AddClaim(new Claim(ClaimTypes.Name, user.Name));
                        context.Identity.AddClaim(new Claim(ClaimTypes.Email, user.Email));
                        context.Identity.AddClaim(new Claim(ClaimTypes.Uri, user.Url));

                        var amountCents = user.Pledges.Sum(x => x.AmountCents);
                        context.Identity.AddClaim(new Claim(PatreonConstants.AmountDonated, amountCents.ToString()));
                    }
                }
            });

            app.Map("/login", builder =>
            {
                builder.Run(async context =>
                {
                    // Return a challenge to invoke the Patreon authentication scheme
                    await context.Authentication.ChallengeAsync(PatreonConstants.AuthenticationScheme, new AuthenticationProperties { RedirectUri = "/" });
                });
            });

            app.Map("/logout", builder =>
            {
                builder.Run(async context =>
                {
                    // Sign the user out of the authentication middleware (i.e. it will clear the Auth cookie)
                    await context.Authentication.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                    // Redirect the user to the home page after signing out
                    context.Response.Redirect("/");
                });
            });

            app.UseMvc(routes =>
            {
                routes.MapRoute("default", "{controller=Home}/{action=Index}/{id?}");
            });
        }
    }
}
