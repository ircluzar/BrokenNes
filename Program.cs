using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using BrokenNes;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<NesEmulator.Shaders.IShaderProvider, NesEmulator.Shaders.ShaderProvider>();
builder.Services.AddScoped<Emulator>();
builder.Services.AddScoped<BrokenNes.Services.InputSettingsService>();

// Add comprehensive logging
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Add console logging for browser developer tools
if (builder.HostEnvironment.IsDevelopment())
{
    builder.Logging.AddFilter("Microsoft.AspNetCore.Components.WebAssembly", LogLevel.Information);
}

var app = builder.Build();

// Global exception handling
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var logger = app.Services.GetService<ILogger<Program>>();
    logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception occurred");
    #if DIAG_LOG // Controlled via -p:EnableDiagLog=true
    Console.WriteLine($"Unhandled exception: {e.ExceptionObject}");
    #endif
};

await app.RunAsync();
