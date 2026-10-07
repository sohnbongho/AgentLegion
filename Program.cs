using AgentLegion.Data;
using System.Text;
using AgentLegion.Services;
using AgentLegion.Services.Redis;
using AgentLegion.Services.Wsl;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace AgentLegion
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // Redis values are decoded as EUC-KR
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorPages();
            builder.Services.AddServerSideBlazor();
            builder.Services.AddSingleton<WeatherForecastService>();
            builder.Services.AddSingleton<LegionService>();
            builder.Services.AddSingleton<JobStore>();
            builder.Services.AddSingleton<SessionManager>();
            builder.Services.AddSingleton<WslInfoService>();
            builder.Services.AddSingleton<ServerOpsService>();
            // per browser circuit: each tab has its own Redis connection
            builder.Services.AddScoped<RedisConnectionService>();
            builder.Services.AddScoped<RedisKeyService>();
            builder.Services.AddScoped<KeyBrowserState>();
            builder.Services.AddSingleton<WslFileService>();
            builder.Services.AddSingleton<FileBrowserMemory>();
            builder.Services.AddScoped<FileBrowserState>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            app.UseStaticFiles();

            app.UseRouting();

            app.MapBlazorHub();
            app.MapFallbackToPage("/_Host");

            app.Run();
        }
    }
}
