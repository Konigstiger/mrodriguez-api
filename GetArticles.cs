using System.Net;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using func_mrodriguez_portfolio.Models;

namespace func_mrodriguez_portfolio;

public sealed class GetArticles
{
    private readonly ILogger<GetArticles> _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IConfiguration _config;

    public GetArticles(
        ILogger<GetArticles> logger,
        BlobServiceClient blobServiceClient,
        IConfiguration config)
    {
        _logger = logger;
        _blobServiceClient = blobServiceClient;
        _config = config;
    }

    [Function("GetArticles")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "articles")]
        HttpRequestData req)
    {
        _logger.LogInformation("GetArticles triggered.");

        try
        {
            var containerName = _config["ARTICLES_CONTAINER"];
            var basePath = _config["ARTICLES_PATH"] ?? "";

            if (string.IsNullOrWhiteSpace(containerName))
            {
                _logger.LogError("ARTICLES_CONTAINER not configured.");
                return CreateText(req, HttpStatusCode.InternalServerError, "Server misconfiguration.");
            }

            var container = _blobServiceClient.GetBlobContainerClient(containerName);

            var articles = new List<ArticleListItemDto>();
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();

            await foreach (BlobItem blobItem in container.GetBlobsAsync(prefix: basePath))
            {
                if (!blobItem.Name.EndsWith(".md"))
                    continue;

                var blob = container.GetBlobClient(blobItem.Name);
                var content = await blob.DownloadContentAsync();
                var markdown = content.Value.Content.ToString();

                var listItem = ParseFrontmatter(markdown, blobItem.Name);
                if (listItem != null)
                {
                    articles.Add(listItem);
                }
            }

            // Order newest first (string ISO dates sort correctly)
            var ordered = articles
                .OrderByDescending(a => a.Date)
                .ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Cache-Control", "public, max-age=300");

            await response.WriteAsJsonAsync(ordered);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetArticles failed.");
            return CreateText(req, HttpStatusCode.InternalServerError, "Internal server error.");
        }
    }

    private static ArticleListItemDto? ParseFrontmatter(string markdown, string blobName)
    {
        var parts = markdown.Split(new[] { "---" }, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var meta = deserializer.Deserialize<Dictionary<string, object>>(parts[0]);

        // Slug = filename without path or extension
        var slug = Path.GetFileNameWithoutExtension(blobName);

        return new ArticleListItemDto
        {
            Slug = slug,
            Title = meta.TryGetValue("title", out var title) ? title?.ToString() ?? "" : "",
            Date = meta.TryGetValue("date", out var date) ? date?.ToString() ?? "" : "",
            Summary = meta.TryGetValue("summary", out var summary) ? summary?.ToString() ?? "" : "",
            Tags = meta.TryGetValue("tags", out var tags) && tags is IEnumerable<object> t
                ? t.Select(x => x.ToString() ?? "").ToArray()
                : Array.Empty<string>()
        };
    }

    private static HttpResponseData CreateText(HttpRequestData req, HttpStatusCode status, string message)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        res.WriteString(message);
        return res;
    }
}
