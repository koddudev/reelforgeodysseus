using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ReelForge Odysseus API",
        Version = "v1",
        Description = "Odysseus-compatible .NET workspace endpoints for ReelForge media generation."
    });
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();
var sessions = new ConcurrentDictionary<string, OdysseusSession>();
var swaggerEnabled = app.Configuration.GetValue("Swagger:Enabled", true);

app.UseCors();

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Json(new
{
    service = "ReelForge Odysseus .NET Workspace",
    status = "running",
    endpoints = new[] { "/health", "/api/default-chat", "/session", "/api/chat", "/swagger" }
}))
    .WithName("Root")
    .WithOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("Health")
    .WithOpenApi();

app.MapGet("/api/default-chat", () => Results.Ok(new
{
    endpoint_id = "reelforge-dotnet",
    endpoint_url = $"{GetPublicBaseUrl(app.Configuration)}/api/chat",
    model = "reelforge-commercial-blueprint-v1"
}))
    .WithName("GetDefaultChat")
    .WithOpenApi();

app.MapPost("/session", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var model = ValueOrDefault(form["model"].ToString(), "reelforge-commercial-blueprint-v1");
    var endpointId = ValueOrDefault(form["endpoint_id"].ToString(), "reelforge-dotnet");
    var endpointUrl = ValueOrDefault(form["endpoint_url"].ToString(), $"{GetPublicBaseUrl(app.Configuration)}/api/chat");
    var id = Guid.NewGuid().ToString("N");

    sessions[id] = new OdysseusSession(id, model, endpointId, endpointUrl, DateTimeOffset.UtcNow);
    return Results.Ok(new { id, model, endpoint_id = endpointId, endpoint_url = endpointUrl });
})
    .Accepts<IFormCollection>("application/x-www-form-urlencoded")
    .WithName("CreateSession")
    .WithOpenApi();

app.MapPost("/api/chat", async (OdysseusChatRequest request, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var sessionId = ValueOrDefault(request.Session, Guid.NewGuid().ToString("N"));
    var existing = sessions.GetValueOrDefault(sessionId);
    var requestedModel = ValueOrDefault(request.Model, existing?.Model ?? "reelforge-commercial-blueprint-v1");
    sessions.AddOrUpdate(
        sessionId,
        new OdysseusSession(sessionId, requestedModel, "reelforge-dotnet", "/api/chat", DateTimeOffset.UtcNow),
        (_, current) => current with { Model = requestedModel });

    var response = await ChatRouter.GetResponseAsync(request.Message, requestedModel, httpClientFactory, configuration, cancellationToken);
    return Results.Ok(new
    {
        response,
        session = sessionId,
        model = requestedModel
    });
})
    .WithName("Chat")
    .WithOpenApi();

app.Run();

static string GetPublicBaseUrl(IConfiguration configuration)
{
    var configured = configuration["PUBLIC_BASE_URL"];
    if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
    var host = configuration["WEBSITE_HOSTNAME"];
    return string.IsNullOrWhiteSpace(host) ? "" : $"https://{host}";
}

static string ValueOrDefault(string? value, string fallback) =>
    string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

internal sealed record OdysseusSession(string Id, string Model, string EndpointId, string EndpointUrl, DateTimeOffset CreatedAt);

internal sealed record OdysseusChatRequest(
    string Message,
    string? Session,
    string[]? Attachments,
    [property: JsonPropertyName("use_web")] bool UseWeb,
    [property: JsonPropertyName("use_research")] bool UseResearch,
    string? Model);

internal sealed record LocalAdBlueprint(
    string AdTitle,
    string TargetAudience,
    string Hook,
    string EmotionalAngle,
    string VoiceoverScript,
    IReadOnlyList<LocalAdScene> Scenes,
    IReadOnlyList<string> Captions,
    IReadOnlyList<string> VisualPrompts,
    string MusicDirection,
    string PacingNotes,
    string TransitionNotes,
    string CallToAction,
    string TtsDirection);

internal sealed record LocalAdScene(
    int SceneNumber,
    decimal StartTime,
    decimal EndTime,
    string Purpose,
    string VisualPrompt,
    string CameraDirection,
    string MotionDirection,
    string OnScreenText,
    string VoiceoverSegment,
    string TransitionIn,
    string TransitionOut,
    string BrollType,
    string EmotionalTone);

internal static class BlueprintGenerator
{
    public static LocalAdBlueprint Generate(string message)
    {
        var business = ExtractValue(message, "businessName") ?? ExtractTopic(message) ?? "Client Brand";
        var product = ExtractValue(message, "productOrService") ?? ExtractValue(message, "Source topic/supporting information") ?? message;
        var audience = ExtractValue(message, "targetAudience") ?? "busy decision makers and social media viewers";
        var cta = StrongCallToAction(ExtractValue(message, "callToAction"), business);
        var cleanProduct = Shorten(Clean(product), 140);

        var hook = $"What if {business} made this easier before the next scroll?";
        var captions = new[]
        {
            "Stop settling for ordinary.",
            "Built for a smoother experience.",
            "See the difference in seconds.",
            cta
        };

        var scenes = new[]
        {
            new LocalAdScene(
                1,
                0m,
                2.1m,
                "hook",
                $"cinematic opening shot that introduces {business} with premium lighting and immediate visual contrast around {cleanProduct}",
                "tight moving close-up with a quick reveal",
                "bold caption snaps in with subtle brand motion",
                captions[0],
                $"Ever notice how the small friction points add up? {business} changes that.",
                "none",
                "match cut into problem moment",
                "lifestyle/product",
                "curious and polished"),
            new LocalAdScene(
                2,
                2.1m,
                8.4m,
                "pain point",
                $"real customer environment showing the old way feeling slow, cluttered, or less premium before {business} enters the frame",
                "medium handheld shot that follows the customer's moment",
                "muted overlay labels the pain point, then clears for the solution",
                "The old way feels slow.",
                $"When the experience feels ordinary, people feel it right away. The fix should feel simple.",
                "match cut",
                "smooth motion wipe",
                "lifestyle",
                "relatable and slightly tense"),
            new LocalAdScene(
                3,
                8.4m,
                16.2m,
                "solution",
                $"premium product and service b-roll for {business}, showing {cleanProduct} as clean, reliable, and easy to choose",
                "slow push-in with refined commercial framing",
                "brand color accents trace the benefit path without crowding the image",
                "A better experience, fast.",
                $"{business} brings the experience into focus: cleaner, faster, and made to feel effortless.",
                "motion wipe",
                "soft dissolve into proof",
                "product/benefit",
                "relieved and confident"),
            new LocalAdScene(
                4,
                16.2m,
                24.4m,
                "proof and transformation",
                $"happy audience using or benefiting from {business}, with credible visual proof, polished environment, and warm human reaction",
                "wide lifestyle shot moving into a confident hero frame",
                "caption rhythm follows the voiceover while logo appears as a small corner lockup",
                "Premium, practical, memorable.",
                "You see it in the details: less hassle, stronger presentation, and a moment people remember.",
                "soft dissolve",
                "branded ramp into CTA",
                "proof/lifestyle",
                "warm and premium"),
            new LocalAdScene(
                5,
                24.4m,
                30m,
                "CTA",
                $"clean branded end card for {business} with clear offer, logo placement, and conversion-focused call to action",
                "locked-off hero composition with clear CTA hierarchy",
                "logo resolves, CTA button animates once, captions settle",
                cta,
                $"{cta}. Make the next impression feel like the one you meant to create.",
                "branded ramp",
                "end",
                "CTA",
                "confident and motivating")
        };

        return new LocalAdBlueprint(
            AdTitle: $"{business} Commercial Blueprint",
            TargetAudience: audience,
            Hook: hook,
            EmotionalAngle: "Move viewers from friction and uncertainty into confidence, polish, and a clear next step.",
            VoiceoverScript: string.Join(" ", scenes.Select(scene => scene.VoiceoverSegment)),
            Scenes: scenes,
            Captions: captions,
            VisualPrompts: scenes.Select(scene => scene.VisualPrompt).ToArray(),
            MusicDirection: "modern premium music bed with a confident pulse, light percussion, and tasteful ducking under voiceover",
            PacingNotes: "Open fast, slow slightly for clarity, build through proof, then land the CTA with a polished branded end card.",
            TransitionNotes: "Use match cuts, motion wipes, soft dissolves, and one branded CTA ramp so it feels like a commercial instead of a slideshow.",
            CallToAction: cta,
            TtsDirection: "warm confident human voice, medium pace, slight smile, short pauses after the hook and before the final CTA");
    }

    private static string? ExtractValue(string source, string key)
    {
        var quotedKey = $"\"{key}\"";
        var keyIndex = source.IndexOf(quotedKey, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0) return null;
        var colonIndex = source.IndexOf(':', keyIndex);
        if (colonIndex < 0) return null;
        var firstQuote = source.IndexOf('"', colonIndex + 1);
        if (firstQuote < 0) return null;
        var secondQuote = source.IndexOf('"', firstQuote + 1);
        return secondQuote > firstQuote ? source[(firstQuote + 1)..secondQuote] : null;
    }

    private static string? ExtractTopic(string source)
    {
        var marker = "Source topic/supporting information:";
        var index = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var text = source[(index + marker.Length)..].Trim();
        return string.IsNullOrWhiteSpace(text) ? null : Shorten(Clean(text), 60);
    }

    private static string StrongCallToAction(string? requested, string business)
    {
        if (!string.IsNullOrWhiteSpace(requested) && requested.Trim().Length > 12)
            return requested.Trim();

        return $"Request your {business} demo today";
    }

    private static string Clean(string value) =>
        string.Join(' ', value.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";
}

internal static class ChatRouter
{
    public static async Task<string> GetResponseAsync(string message, string model, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (ImageGenerator.IsImageGenerationRequest(message, model))
            return await ImageGenerator.GenerateAsync(message, model, httpClientFactory, configuration, cancellationToken);

        if (IsAdvertisementBlueprintRequest(message))
        {
            var blueprint = BlueprintGenerator.Generate(message);
            return JsonSerializer.Serialize(blueprint, JsonOptions.Default.LocalAdBlueprint);
        }

        return GeneralChatResponder.Answer(message);
    }

    private static bool IsAdvertisementBlueprintRequest(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("advertisement blueprint")
            || lower.Contains("commercial-quality advertisement")
            || lower.Contains("return only valid json")
            || lower.Contains("\"businessname\"")
            || lower.Contains("\"productorservice\"")
            || lower.Contains("creative brief:")
            || lower.Contains("source topic/supporting information:");
    }
}

internal static class GeneralChatResponder
{
    public static string Answer(string message)
    {
        var normalized = message.Trim().ToLowerInvariant();

        if (normalized.Contains("age of the earth") || normalized.Contains("old is the earth"))
            return "The Earth is about 4.54 billion years old, based mainly on radiometric dating of meteorites, Moon rocks, and Earth minerals.";

        if (normalized.Contains("health") || normalized.Contains("status"))
            return "The ReelForge Odysseus .NET workspace is running.";

        return "I can answer general questions and also generate ReelForge ad blueprints. For media generation, send a prompt that includes a creative brief or asks for an advertisement blueprint.";
    }
}

internal static class ImageGenerator
{
    private static readonly HashSet<string> ImageModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-image-1.5",
        "gpt-image-1",
        "gpt-image-1-mini",
        "dall-e-3",
        "dall-e-2"
    };

    public static bool IsImageGenerationRequest(string message, string model)
    {
        if (ImageModels.Contains(model)) return true;

        var lower = message.ToLowerInvariant();
        return lower.Contains("show me a picture")
            || lower.Contains("generate an image")
            || lower.Contains("create an image")
            || lower.Contains("make an image")
            || lower.Contains("draw ")
            || lower.Contains("picture of")
            || lower.Contains("image of");
    }

    public static async Task<string> GenerateAsync(string message, string model, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return JsonSerializer.Serialize(new ImageGenerationResult(
                Type: "image_generation_error",
                Model: model,
                Prompt: message,
                ImageUrl: null,
                ImageDataUrl: null,
                Message: "OPENAI_API_KEY is not configured on this app service. Add it as an Azure App Service application setting to generate real images."));
        }

        var prompt = NormalizeImagePrompt(message);
        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        var payload = new
        {
            model,
            prompt,
            size = "1024x1024",
            n = 1
        };

        using var requestBody = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("https://api.openai.com/v1/images/generations", requestBody, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return JsonSerializer.Serialize(new ImageGenerationResult(
                Type: "image_generation_error",
                Model: model,
                Prompt: prompt,
                ImageUrl: null,
                ImageDataUrl: null,
                Message: $"OpenAI image generation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Shorten(body, 800)}"));
        }

        using var document = JsonDocument.Parse(body);
        var firstImage = document.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        var imageUrl = firstImage.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        var b64 = firstImage.TryGetProperty("b64_json", out var b64Element) ? b64Element.GetString() : null;
        var revisedPrompt = firstImage.TryGetProperty("revised_prompt", out var revisedElement) ? revisedElement.GetString() : null;

        return JsonSerializer.Serialize(new ImageGenerationResult(
            Type: "image",
            Model: model,
            Prompt: revisedPrompt ?? prompt,
            ImageUrl: imageUrl,
            ImageDataUrl: string.IsNullOrWhiteSpace(b64) ? null : $"data:image/png;base64,{b64}",
            Message: string.IsNullOrWhiteSpace(b64) && string.IsNullOrWhiteSpace(imageUrl)
                ? "The image provider succeeded, but no image URL or base64 image was returned."
                : "Image generated successfully."));
    }

    private static string NormalizeImagePrompt(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.EndsWith("?")) trimmed = trimmed[..^1];
        return trimmed
            .Replace("Can you show me", "Show", StringComparison.OrdinalIgnoreCase)
            .Replace("Could you show me", "Show", StringComparison.OrdinalIgnoreCase);
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";
}

internal sealed record ImageGenerationResult(
    string Type,
    string Model,
    string Prompt,
    string? ImageUrl,
    string? ImageDataUrl,
    string Message);

[JsonSerializable(typeof(LocalAdBlueprint))]
internal sealed partial class JsonOptions : JsonSerializerContext
{
}
