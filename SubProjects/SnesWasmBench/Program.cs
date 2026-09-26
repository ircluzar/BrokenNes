using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SnesWasmBench;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Bench>("#app");
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
await builder.Build().RunAsync();
