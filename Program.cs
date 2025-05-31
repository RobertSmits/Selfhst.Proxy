using System.Text.RegularExpressions;

public partial class Program
{
    const int PORT = 4050;
    const string CDN_ROOT = "https://cdn.jsdelivr.net/gh/selfhst/icons";
    const string CDN_PATH = "svg";
    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase) { "png", "webp", "svg" };

    [GeneratedRegex("fill=\"#fff\"", RegexOptions.IgnoreCase, "en-BE")]
    private static partial Regex AttributeRegex();

    public static void Main(string[] args)
    {

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHttpClient("cdn", client =>
        {
            client.BaseAddress = new Uri(CDN_ROOT);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SelfHostedIconServer/1.0");
        });

        var app = builder.Build();

        app.MapGet("/", () => "Self-hosted icon server");

        app.MapGet("/{**urlPath}", async (IHttpClientFactory httpClientFactory, HttpContext context, string urlPath) =>
        {
            var httpClient = httpClientFactory.CreateClient("cdn");
            var extMatch = Regex.Match(urlPath, @"\.(\w+)$");

            if (!extMatch.Success)
            {
                return Results.NotFound("File not found");
            }

            var ext = extMatch.Groups[1].Value;
            if (!_imageExtensions.Contains(ext))
            {
                return Results.NotFound("Format not supported");
            }

            var filename = urlPath.TrimStart('/');
            var lowerFilename = filename.ToLower();

            var isSuffix = lowerFilename.EndsWith("-light.svg") || lowerFilename.EndsWith("-dark.svg");
            if (isSuffix)
            {
                return await FetchAndPipeToResult(httpClient, $"{CDN_ROOT}/{CDN_PATH}/{filename}", context.Response);
            }

            var mainUrl = ext switch
            {
                "png" => $"{CDN_ROOT}/png/{filename}",
                "webp" => $"{CDN_ROOT}/webp/{filename}",
                "svg" => $"{CDN_ROOT}/svg/{filename}",
                _ => null
            };

            var colorQuery = context.Request.Query["color"].ToString();
            var hasColor = !string.IsNullOrWhiteSpace(colorQuery);

            if (ext != "svg" || !hasColor)
            {
                return await FetchAndPipeToResult(httpClient, mainUrl, context.Response);
            }

            var baseName = Regex.Replace(filename, @"\.(png|webp|svg)$", "", RegexOptions.IgnoreCase);
            var suffixUrl = $"{CDN_ROOT}/{CDN_PATH}/{baseName}-light.svg";

            var suffixResult = await httpClient.GetAsync(suffixUrl);
            if (!suffixResult.IsSuccessStatusCode)
            {
                return await FetchAndPipeToResult(httpClient, mainUrl, context.Response);
            }

            var svgContent = await suffixResult.Content.ReadAsStringAsync();
            var color = colorQuery.StartsWith("#") ? colorQuery : $"#{colorQuery}";

            svgContent = AttributeRegex().Replace(svgContent, $"fill=\"{color}\"");

            svgContent = Regex.Replace(svgContent, @"style=""[^""]*fill:\s*#fff[^""]*""", match =>
            {
                var updated = Regex.Replace(match.Value, @"fill:\s*#fff", $"fill:{color}", RegexOptions.IgnoreCase);
                return updated;
            }, RegexOptions.IgnoreCase);

            svgContent = AttributeRegex().Replace(svgContent, $"fill=\"{color}\"");


            context.Response.ContentType = "image/svg+xml";
            await context.Response.WriteAsync(svgContent);
            return Results.Empty;
        });

        app.Run($"http://0.0.0.0:{PORT}");

        async Task<IResult> FetchAndPipeToResult(HttpClient client, string url, HttpResponse response)
        {
            var result = await client.GetAsync(url);
            if (!result.IsSuccessStatusCode)
            {
                return Results.NotFound("File not found");
            }

            var ext = Path.GetExtension(url).TrimStart('.').ToLower();
            response.ContentType = ext switch
            {
                "svg" => "image/svg+xml",
                "png" => "image/png",
                "webp" => "image/webp",
                _ => "application/octet-stream"
            };

            await result.Content.CopyToAsync(response.Body);
            return Results.Empty;
        }
    }
}