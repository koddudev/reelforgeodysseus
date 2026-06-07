using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
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

app.MapPost("/api/chat", (OdysseusChatRequest request) =>
{
    var sessionId = ValueOrDefault(request.Session, Guid.NewGuid().ToString("N"));
    sessions.TryAdd(sessionId, new OdysseusSession(sessionId, "reelforge-commercial-blueprint-v1", "reelforge-dotnet", "/api/chat", DateTimeOffset.UtcNow));

    var blueprint = BlueprintGenerator.Generate(request.Message);
    return Results.Ok(new
    {
        response = JsonSerializer.Serialize(blueprint, JsonOptions.Default.LocalAdBlueprint),
        session = sessionId,
        model = sessions[sessionId].Model
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
    [property: JsonPropertyName("use_research")] bool UseResearch);

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

[JsonSerializable(typeof(LocalAdBlueprint))]
internal sealed partial class JsonOptions : JsonSerializerContext
{
}
