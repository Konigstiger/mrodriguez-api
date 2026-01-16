namespace func_mrodriguez_portfolio.Models;

public sealed class ArticleListItemDto
{
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Date { get; init; } = "";
    public string Summary { get; init; } = "";
    public string[] Tags { get; init; } = Array.Empty<string>();
}
