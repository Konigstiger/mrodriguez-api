using System.Net;
using System.Text;
using Azure.Storage.Blobs;
using Markdig;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using func_mrodriguez_portfolio.Models;

namespace func_mrodriguez_portfolio;

public sealed class GetArticle
{
    private readonly ILogger<GetArticle> _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IConfiguration _config;

    public GetArticle(
        ILogger<GetArticle> logger,
        BlobServiceClient blobServiceClient,
        IConfiguration config)
    {
        _logger = logger;
        _blobServiceClient = blobServiceClient;
        _config = config;
    }

    [Function("GetArticle")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "articles/{slug}")]
        HttpRequestData req,
        string slug)
    {
        _logger.LogInformation("GetArticle triggered for slug: {Slug}", slug);

        try
        {
            var containerName = _config["ARTICLES_CONTAINER"];
            var basePath = _config["ARTICLES_PATH"] ?? "";

            if (string.IsNullOrWhiteSpace(containerName))
            {
                _logger.LogError("ARTICLES_CONTAINER not configured.");
                return CreateText(req, HttpStatusCode.InternalServerError, "Server misconfiguration.");
            }

            var blobName = $"{basePath}{slug}.md";

            var container = _blobServiceClient.GetBlobContainerClient(containerName);
            var blob = container.GetBlobClient(blobName);

            if (!await blob.ExistsAsync())
            {
                _logger.LogWarning("Article not found: {Container}/{Blob}", containerName, blobName);
                return CreateText(req, HttpStatusCode.NotFound, "Article not found.");
            }

            // Read markdown
            var download = await blob.DownloadContentAsync();
            var markdown = download.Value.Content.ToString();

            var article = ParseArticle(slug, markdown);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Cache-Control", "public, max-age=300");

            await response.WriteAsJsonAsync(article);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetArticle failed.");
            return CreateText(req, HttpStatusCode.InternalServerError, "Internal server error.");
        }
    }

    private static ArticleDto ParseArticle(string slug, string markdown)
    {
        // Split frontmatter
        var parts = markdown.Split(new[] { "---" }, 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Invalid article format: missing frontmatter.");
        }

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var meta = deserializer.Deserialize<Dictionary<string, object>>(parts[0]);

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        return new ArticleDto
        {
            Slug = slug,
            Title = meta.TryGetValue("title", out var title) ? title?.ToString() ?? "" : "",
            Date = meta.TryGetValue("date", out var date) ? date?.ToString() ?? "" : "",
            Summary = meta.TryGetValue("summary", out var summary) ? summary?.ToString() ?? "" : "",
            Tags = meta.TryGetValue("tags", out var tags) && tags is IEnumerable<object> t
                ? t.Select(x => x.ToString() ?? "").ToArray()
                : Array.Empty<string>(),
            ContentHtml = Markdown.ToHtml(parts[1], pipeline)
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
