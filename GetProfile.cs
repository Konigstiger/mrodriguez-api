using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace func_mrodriguez_portfolio;

public sealed class GetProfile
{
    private readonly ILogger<GetProfile> _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IConfiguration _config;

    public GetProfile(
        ILogger<GetProfile> logger,
        BlobServiceClient blobServiceClient,
        IConfiguration config)
    {
        _logger = logger;
        _blobServiceClient = blobServiceClient;
        _config = config;
    }


    [Function("GetProfile")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profile")] HttpRequestData req)
    {
        _logger.LogInformation("GetProfile triggered.");

        try
        {
            // 1) Read configuration
            var containerName = _config["PROFILE_CONTAINER"];
            var blobName = _config["PROFILE_BLOB_NAME"];

            if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(blobName))
            {
                _logger.LogError("PROFILE_CONTAINER or PROFILE_BLOB_NAME not configured.");
                return CreateText(req, HttpStatusCode.InternalServerError, "Server misconfiguration.");
            }

            // 2) Get blob
            var container = _blobServiceClient.GetBlobContainerClient(containerName);
            var blob = container.GetBlobClient(blobName);

            if (!await blob.ExistsAsync())
            {
                _logger.LogWarning("Profile blob not found: {Container}/{Blob}", containerName, blobName);
                return CreateText(req, HttpStatusCode.NotFound, "Profile not found.");
            }

            // 3) Stream JSON
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.Headers.Add("Cache-Control", "public, max-age=300");

            await blob.DownloadToAsync(response.Body);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProfile failed.");
            return CreateText(req, HttpStatusCode.InternalServerError, "Internal server error.");
        }
    }

    private static HttpResponseData CreateText(HttpRequestData req, HttpStatusCode status, string message)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        res.WriteString(message);
        return res;
    }
}
