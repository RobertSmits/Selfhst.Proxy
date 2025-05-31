using System.Text.RegularExpressions;

public partial class Program
{
    const int PORT = 4050;
    const string CDN_ROOT = "https://cdn.jsdelivr.net/gh/selfhst/icons";
    const string CDN_PATH = "svg";
    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase) { "png", "webp", "svg" };

    [GeneratedRegex(@"fill:\s*#fff", RegexOptions.IgnoreCase, "en-BE")]
    private static partial Regex StyleRegex();

    [GeneratedRegex("fill=\"#fff\"", RegexOptions.IgnoreCase, "en-BE")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"stop-color:\s*#fff(?![a-fA-F0-9])", RegexOptions.IgnoreCase, "en-BE")]
    private static partial Regex StopColorRegex();

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
            var extMatch = Regex.Match(urlPath, @"\.(\w+)$");
            var colorQuery = context.Request.Query["color"].ToString();
            var externalUrl = context.Request.Query["external"].ToString();

            var hasColor = !string.IsNullOrWhiteSpace(colorQuery);
            var color = colorQuery.StartsWith("#") ? colorQuery : $"#{colorQuery}";

            var httpClient = httpClientFactory.CreateClient("cdn");

            // Support external SVG
            if (!string.IsNullOrWhiteSpace(externalUrl) && Uri.TryCreate(externalUrl, UriKind.Absolute, out var externalUri))
            {
                var externalResponse = await httpClient.GetAsync(externalUri);
                if (!externalResponse.IsSuccessStatusCode || !externalUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound("Invalid external SVG");
                }

                var svgContent = await externalResponse.Content.ReadAsStringAsync();
                if (hasColor)
                {
                    svgContent = ApplyColorToSvg(svgContent, color);
                }

                context.Response.ContentType = "image/svg+xml";
                await context.Response.WriteAsync(svgContent);
                return Results.Empty;
            }

            // Local CDN fallback logic
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

            var localSvgContent = await suffixResult.Content.ReadAsStringAsync();
            localSvgContent = ApplyColorToSvg(localSvgContent, color);

            context.Response.ContentType = "image/svg+xml";
            await context.Response.WriteAsync(localSvgContent);
            return Results.Empty;
        });

        app.Run($"http://0.0.0.0:{PORT}");
    }

    private static async Task<IResult> FetchAndPipeToResult(HttpClient client, string url, HttpResponse response)
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

    private static string ApplyColorToSvg(string svg, string color)
    {
        svg = AttributeRegex().Replace(svg, $"fill=\"{color}\"");
        svg = StyleRegex().Replace(svg, $"fill:{color}");
        svg = StopColorRegex().Replace(svg, $"stop-color:{color}");
        return svg;
    }
}
