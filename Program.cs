using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

public partial class Program
{
    const int PORT = 4050;
    const string CDN_ROOT = "https://cdn.jsdelivr.net/gh/selfhst/icons";
    const string CDN_PATH = "svg";

    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "webp", "svg" };

    [GeneratedRegex(@"fill\s*:\s*#[0-9a-fA-F]{3,6}", RegexOptions.IgnoreCase, "en-BE")]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"fill\s*=\s*""#[0-9a-fA-F]{3,6}""", RegexOptions.IgnoreCase, "en-BE")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"stop-color\s*:\s*#[0-9a-fA-F]{3,6}", RegexOptions.IgnoreCase, "en-BE")]
    private static partial Regex StopColorRegex();

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHttpClient("cdn", client =>
        {
            client.BaseAddress = new Uri(CDN_ROOT);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SelfHostedIconServer/1.0");
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });

        var app = builder.Build();

        app.MapGet("/", () => "Self-hosted icon server");

        app.MapGet("/{**urlPath}", async (IHttpClientFactory httpClientFactory, HttpContext context,
            [FromRoute] string urlPath, [FromQuery] string? color) =>
        {
            var externalUri = default(Uri);
            color ??= string.Empty;
            urlPath = Uri.UnescapeDataString(urlPath);
            var extMatch = Regex.Match(urlPath, @"\.(\w+)$");
            var isExternal = urlPath.StartsWith("http") &&
                             Uri.TryCreate(urlPath, UriKind.Absolute, out externalUri);
            var hasColor = !string.IsNullOrWhiteSpace(color);
            var hexColor = color.StartsWith("#") ? color : $"#{color}";
            var httpClient = httpClientFactory.CreateClient("cdn");

            // Support external SVG
            if (isExternal)
            {
                var externalResponse = await httpClient.GetAsync(externalUri);
                if (!externalResponse.IsSuccessStatusCode ||
                    !externalUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound("Invalid external SVG");
                }

                var svgContent = await externalResponse.Content.ReadAsStringAsync();
                if (hasColor)
                {
                    svgContent = ApplyColorToSvg(svgContent, hexColor);
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
            localSvgContent = ApplyColorToSvg(localSvgContent, hexColor);

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

[JsonSerializable(typeof(string))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}