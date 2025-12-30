using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Add configuration sources (local.settings.json is loaded by Functions runtime for local dev)
builder.Services.AddOptions();

// HttpClient via factory (best practice; avoids socket exhaustion)
builder.Services.AddHttpClient("turnstile", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// BlobServiceClient via connection string from env/config
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var conn = config["BlobConnectionString"];

    if (string.IsNullOrWhiteSpace(conn))
        throw new InvalidOperationException("BlobConnectionString is not configured.");

    return new BlobServiceClient(conn);
});

builder.Build().Run();
