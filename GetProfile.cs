using System;
using System.IO;
using System.Threading.Tasks;
using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace func_mrodriguez_portfolio
{
    public class GetProfile
    {
        private readonly ILogger _logger;

        public GetProfile(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<GetProfile>();
        }

        [Function("GetProfile")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profile")] HttpRequestData req)
        {
            _logger.LogInformation("GetProfile function triggered.");

            try
            {
                // 1) Read connection string from environment / local.settings.json
                string? connectionString = Environment.GetEnvironmentVariable("BlobConnectionString");
                if (string.IsNullOrEmpty(connectionString))
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await errorResponse.WriteStringAsync("BlobConnectionString is not configured.");
                    return errorResponse;
                }

                // 2) Update these if your container / blob names differ
                string containerName = "site-content";   // <-- change if needed
                string blobName = "profile.json";        // <-- change if needed

                var blobClient = new BlobClient(connectionString, containerName, blobName);

                if (!await blobClient.ExistsAsync())
                {
                    var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundResponse.WriteStringAsync($"Blob {containerName}/{blobName} not found.");
                    return notFoundResponse;
                }

                using var stream = new MemoryStream();
                await blobClient.DownloadToAsync(stream);
                stream.Position = 0;

                using var reader = new StreamReader(stream);
                string json = await reader.ReadToEndAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync(json);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetProfile.");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Internal server error.");
                return errorResponse;
            }
        }
    }
}
