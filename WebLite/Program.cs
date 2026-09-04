using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using BrokenNes;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
// Emulator's constructor takes the NON-generic ILogger, which the DI container does not register
// by default (it only provides ILogger<T>). Without this the app dies at startup with
// "CannotResolveService, Microsoft.Extensions.Logging.ILogger, BrokenNes.Emulator".
builder.Services.AddScoped<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("BrokenNes"));
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<NesEmulator.Shaders.IShaderProvider, NesEmulator.Shaders.ShaderProvider>();
builder.Services.AddScoped<Emulator>();
builder.Services.AddScoped<BrokenNes.Services.InputSettingsService>();
builder.Services.AddScoped<BrokenNes.Services.GameSaveService>();
// Cartridge battery/flash persistence (IndexedDB). Scoped, like GameSaveService, because it holds
// per-session state: which game its "already stored" trackers describe, and the write in flight.
builder.Services.AddScoped<BrokenNes.Services.BatterySaveService>();

// Warning-level only. The original build ran at Debug and added a per-category filter, which on a
// low-end device means the logging pipeline formats and marshals strings across the JS boundary
// every frame for messages nobody reads.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var logger = app.Services.GetService<ILogger<Program>>();
    logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception occurred");
};

await app.RunAsync();
