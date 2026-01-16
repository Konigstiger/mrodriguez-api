using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace func_mrodriguez_portfolio.Models
{
    public sealed class ArticleDto
    {
        public string Slug { get; init; } = "";
        public string Title { get; init; } = "";
        public string Date { get; init; } = "";
        public string[] Tags { get; init; } = Array.Empty<string>();
        public string Summary { get; init; } = "";
        public string ContentHtml { get; init; } = "";
    }
}
