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
    endpoints = new[] { "/health", "/api/default-chat", "/session", "/api/chat", "/api/video/{videoId}", "/api/video/{videoId}/content", "/swagger" }
}))
    .WithName("Root")
    .WithOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("Health")
    .WithOpenApi();

app.MapGet("/api/default-chat", () => Results.Ok(new
{
    endpoint_id = "reelforge-dotnet",
    endpoint_url = $"{PublicUrl.Get(app.Configuration)}/api/chat",
    model = "reelforge-commercial-blueprint-v1"
}))
    .WithName("GetDefaultChat")
    .WithOpenApi();

app.MapPost("/session", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var model = ValueOrDefault(form["model"].ToString(), "reelforge-commercial-blueprint-v1");
    var endpointId = ValueOrDefault(form["endpoint_id"].ToString(), "reelforge-dotnet");
    var endpointUrl = ValueOrDefault(form["endpoint_url"].ToString(), $"{PublicUrl.Get(app.Configuration)}/api/chat");
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

    var response = await ChatRouter.GetResponseAsync(request.Message, request.Type, requestedModel, httpClientFactory, configuration, cancellationToken);
    return Results.Ok(new
    {
        response,
        session = sessionId,
        model = requestedModel,
        type = ValueOrDefault(request.Type, "auto")
    });
})
    .WithName("Chat")
    .WithOpenApi();

app.MapGet("/api/video/{videoId}", async (string videoId, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var result = await OpenAiVideoGenerator.GetStatusAsync(videoId, httpClientFactory, configuration, cancellationToken);
    return Results.Content(result, "application/json");
})
    .WithName("GetVideoStatus")
    .WithOpenApi();

app.MapGet("/api/video/{videoId}/content", async (string videoId, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var stream = await OpenAiVideoGenerator.DownloadContentAsync(videoId, httpClientFactory, configuration, cancellationToken);
    return Results.File(stream, "video/mp4", $"{videoId}.mp4");
})
    .WithName("DownloadVideoContent")
    .WithOpenApi();

app.Run();

static string ValueOrDefault(string? value, string fallback) =>
    string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

internal static class PublicUrl
{
    public static string Get(IConfiguration configuration)
    {
        var configured = configuration["PUBLIC_BASE_URL"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
        var host = configuration["WEBSITE_HOSTNAME"];
        return string.IsNullOrWhiteSpace(host) ? "" : $"https://{host}";
    }
}

internal sealed record OdysseusSession(string Id, string Model, string EndpointId, string EndpointUrl, DateTimeOffset CreatedAt);

internal sealed record OdysseusChatRequest(
    string Message,
    string? Session,
    string[]? Attachments,
    [property: JsonPropertyName("use_web")] bool UseWeb,
    [property: JsonPropertyName("use_research")] bool UseResearch,
    string? Model,
    string? Type);

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
    public static async Task<string> GetResponseAsync(string message, string? requestedType, string model, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var type = (requestedType ?? "").Trim().ToLowerInvariant();
        if (type is "image" or "img" or "picture")
        {
            if (IsAdvertisementBlueprintRequest(message))
                return await MediaPackageGenerator.GenerateImageAsync(BlueprintGenerator.Generate(message), model, httpClientFactory, configuration, cancellationToken);

            var imagePrompt = message;
            var imageModel = ImageGenerator.IsKnownImageModel(model) ? model : "pollinations";
            return await ImageGenerator.GenerateAsync(imagePrompt, imageModel, httpClientFactory, configuration, cancellationToken);
        }

        if (type is "video" or "reel")
            return await MediaPackageGenerator.GenerateVideoAsync(BlueprintGenerator.Generate(message), model, httpClientFactory, configuration, cancellationToken);

        if (type is "blueprint" or "ad-blueprint")
        {
            var blueprint = BlueprintGenerator.Generate(message);
            return JsonSerializer.Serialize(blueprint, JsonOptions.Default.LocalAdBlueprint);
        }

        if (IsAdvertisementBlueprintRequest(message))
        {
            var blueprint = BlueprintGenerator.Generate(message);
            return JsonSerializer.Serialize(blueprint, JsonOptions.Default.LocalAdBlueprint);
        }

        if (ImageGenerator.IsImageGenerationRequest(message, model))
            return await ImageGenerator.GenerateAsync(message, model, httpClientFactory, configuration, cancellationToken);

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
            || lower.Contains("source topic/supporting information:")
            || lower.Contains("video reel")
            || lower.Contains("media-generation request");
    }
}

internal static class AdvertisementMediaPromptBuilder
{
    public static string BuildImagePrompt(LocalAdBlueprint blueprint)
    {
        var firstScene = blueprint.Scenes.FirstOrDefault();
        var visual = firstScene?.VisualPrompt ?? blueprint.Hook;
        var caption = blueprint.Captions.FirstOrDefault() ?? blueprint.CallToAction;
        return $"Premium commercial social ad image for {blueprint.AdTitle}. {visual}. Include a polished brand-forward composition, modern lighting, clean product/service presentation, subtle text overlay reading \"{caption}\", and a clear CTA: {blueprint.CallToAction}.";
    }

    public static string BuildPhotorealisticImagePrompt(LocalAdBlueprint blueprint)
    {
        var firstScene = blueprint.Scenes.FirstOrDefault();
        var visual = firstScene?.VisualPrompt ?? blueprint.Hook;
        var caption = blueprint.Captions.FirstOrDefault() ?? blueprint.CallToAction;
        return $"""
            Photorealistic premium commercial social ad image for {blueprint.AdTitle}.
            Scene: {visual}
            Style: inviting real photography, modern office breakroom, real people smiling and interacting naturally, premium smart vending/snack service, polished lighting, shallow depth of field, clean brand-forward composition, no cartoon, no illustration, no flat UI mockup.
            Text overlay: "{caption}"
            CTA: "{blueprint.CallToAction}"
            Composition: vertical/social crop safe, realistic people, realistic vending machine or snack display, warm professional atmosphere, high-end advertising photography.
            """;
    }
}

internal static class MediaPackageGenerator
{
    public static async Task<string> GenerateImageAsync(LocalAdBlueprint blueprint, string model, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var imagePrompt = AdvertisementMediaPromptBuilder.BuildPhotorealisticImagePrompt(blueprint);
        var imageModel = ImageGenerator.IsKnownImageModel(model) && !model.Equals("pollinations", StringComparison.OrdinalIgnoreCase)
            ? model
            : "gpt-image-1.5";
        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        object media;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            media = new
            {
                Type = "image",
                Status = "provider_required",
                Provider = "OpenAI Images, Stability AI, Flux, or another photoreal image provider",
                Model = imageModel,
                ImageUrl = (string?)null,
                ImageDataUrl = (string?)null,
                Prompt = imagePrompt,
                Message = "Photorealistic ad images require a real image-generation provider. No image key/provider is configured, so no low-quality SVG or queued free-generator image was returned."
            };
        }
        else
        {
            var imageJson = await ImageGenerator.GenerateOpenAiAsync(imagePrompt, imageModel, apiKey, httpClientFactory, cancellationToken);
            media = JsonSerializer.Deserialize<JsonElement>(imageJson);
        }

        return JsonSerializer.Serialize(new
        {
            Type = "image",
            Blueprint = blueprint,
            Media = media
        });
    }

    public static async Task<string> GenerateVideoAsync(LocalAdBlueprint blueprint, string model, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var baseUrl = PublicUrl.Get(configuration);
        var videoPrompt = BuildVideoPrompt(blueprint);
        var videoModel = IsKnownVideoModel(model) ? model : "sora-2";
        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        object media;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            media = BuildProviderRequiredVideoMedia(blueprint, videoModel, videoPrompt);
        }
        else
        {
            var video = await OpenAiVideoGenerator.CreateAsync(videoPrompt, videoModel, baseUrl, httpClientFactory, configuration, cancellationToken);
            media = video;
        }

        return JsonSerializer.Serialize(new
        {
            Type = "video",
            Blueprint = blueprint,
            Media = media
        });
    }

    private static bool IsKnownVideoModel(string model) =>
        model.Equals("sora-2", StringComparison.OrdinalIgnoreCase)
        || model.Equals("sora-2-pro", StringComparison.OrdinalIgnoreCase);

    private static object BuildProviderRequiredVideoMedia(LocalAdBlueprint blueprint, string model, string prompt) => new
    {
        Type = "video",
        Status = "provider_required",
        Provider = "OpenAI Sora Video API",
        Model = model,
        VideoId = (string?)null,
        VideoUrl = (string?)null,
        Prompt = prompt,
        Message = "OPENAI_API_KEY is not configured on this app service, so Sora video generation could not be started. The blueprint and production-ready Sora prompt are included.",
        CreativeDirection = "Photoreal commercial reel with professional actors moving naturally in an office/breakroom setting, premium smart vending machine interactions, expressive narration, polished music, natural camera movement, and motivated transitions.",
        AspectRatio = "9:16",
        DurationSeconds = 8,
        VoiceoverScript = blueprint.VoiceoverScript,
        TtsDirection = blueprint.TtsDirection,
        MusicDirection = blueprint.MusicDirection,
        ShotList = blueprint.Scenes.Select(scene => new
        {
            scene.SceneNumber,
            scene.StartTime,
            scene.EndTime,
            scene.Purpose,
            ShotPrompt = $"{scene.VisualPrompt}. Show professional actors moving naturally, interacting with the environment, and avoid static slideshow composition.",
            scene.CameraDirection,
            scene.MotionDirection,
            scene.OnScreenText,
            scene.VoiceoverSegment,
            Transition = $"{scene.TransitionIn} -> {scene.TransitionOut}",
            scene.BrollType,
            scene.EmotionalTone
        })
    };

    private static string BuildVideoPrompt(LocalAdBlueprint blueprint)
    {
        var shotList = string.Join("\n", blueprint.Scenes.Select(scene =>
            $"- {scene.StartTime:0.0}s-{scene.EndTime:0.0}s: {scene.VisualPrompt}. Camera: {scene.CameraDirection}. Motion: {scene.MotionDirection}. On-screen text: {scene.OnScreenText}. Transition: {scene.TransitionIn} to {scene.TransitionOut}."));

        return $"""
            Create an 8-second vertical photoreal commercial video for social media.

            Brand/ad: {blueprint.AdTitle}
            Goal: {blueprint.Hook} {blueprint.EmotionalAngle}
            Visual direction: premium modern office breakroom, professional actors moving naturally, happy employees interacting with a smart vending/snack service, realistic lighting, polished camera movement, no slideshow, no static cards, no cartoon, no illustration.
            Audio direction: include synced commercial narration, natural expressive voice, polished upbeat background music, and tasteful audio ducking under narration.
            Narration script: {blueprint.VoiceoverScript}
            Music: {blueprint.MusicDirection}
            CTA: {blueprint.CallToAction}

            Shot list:
            {shotList}

            Keep all people generic professional actors. Do not depict any real named person or public figure. Do not use copyrighted music. Make the result feel like a real filmed commercial with people moving, not a slideshow.
            """;
    }
}

internal static class OpenAiVideoGenerator
{
    public static async Task<object> CreateAsync(string prompt, string model, string baseUrl, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
            return new
            {
                Type = "video",
                Status = "provider_required",
                Provider = "OpenAI Sora Video API",
                Model = model,
                VideoId = (string?)null,
                VideoUrl = (string?)null,
                StatusUrl = (string?)null,
                Prompt = prompt,
                Message = "OPENAI_API_KEY is not configured on this app service, so Sora video generation could not be started."
            };

        using var http = CreateOpenAiClient(httpClientFactory, apiKey);
        var payload = new
        {
            model,
            prompt,
            size = "720x1280",
            seconds = "8"
        };

        using var requestBody = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("https://api.openai.com/v1/videos", requestBody, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new
            {
                Type = "video",
                Status = "generation_error",
                Provider = "OpenAI Sora Video API",
                Model = model,
                VideoId = (string?)null,
                VideoUrl = (string?)null,
                StatusUrl = (string?)null,
                Prompt = prompt,
                Message = $"OpenAI video generation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Shorten(body, 1200)}"
            };

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : "queued";
        var progress = root.TryGetProperty("progress", out var progressElement) && progressElement.TryGetInt32(out var progressValue) ? progressValue : (int?)null;
        var statusUrl = string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/api/video/{id}";
        var contentUrl = string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/api/video/{id}/content";

        return new
        {
            Type = "video",
            Status = status,
            Provider = "OpenAI Sora Video API",
            Model = model,
            VideoId = id,
            VideoUrl = status == "completed" ? contentUrl : null,
            StatusUrl = statusUrl,
            ContentUrl = contentUrl,
            Progress = progress,
            Prompt = prompt,
            Message = status == "completed"
                ? "Sora video generation completed. Download the MP4 from VideoUrl."
                : "Sora video generation started. Poll StatusUrl until status is completed, then download ContentUrl."
        };
    }

    public static async Task<string> GetStatusAsync(string videoId, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
            return JsonSerializer.Serialize(new { status = "provider_required", message = "OPENAI_API_KEY is not configured." });

        using var http = CreateOpenAiClient(httpClientFactory, apiKey);
        using var response = await http.GetAsync($"https://api.openai.com/v1/videos/{Uri.EscapeDataString(videoId)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return JsonSerializer.Serialize(new { status = "status_error", message = $"OpenAI video status failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Shorten(body, 1200)}" });

        return body;
    }

    public static async Task<Stream> DownloadContentAsync(string videoId, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not configured.");

        using var http = CreateOpenAiClient(httpClientFactory, apiKey);
        using var response = await http.GetAsync($"https://api.openai.com/v1/videos/{Uri.EscapeDataString(videoId)}/content", cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI video download failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Encoding.UTF8.GetString(bytes)}");

        return new MemoryStream(bytes);
    }

    private static HttpClient CreateOpenAiClient(IHttpClientFactory httpClientFactory, string apiKey)
    {
        var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        return http;
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";
}

internal static class LocalAdImageRenderer
{
    public static string Generate(LocalAdBlueprint blueprint, string model)
    {
        var headline = EscapeXml(blueprint.Hook);
        var title = EscapeXml(blueprint.AdTitle.Replace(" Commercial Blueprint", "", StringComparison.OrdinalIgnoreCase));
        var caption = EscapeXml(blueprint.Captions.FirstOrDefault() ?? blueprint.EmotionalAngle);
        var cta = EscapeXml(blueprint.CallToAction);
        var visual = EscapeXml(blueprint.Scenes.FirstOrDefault()?.VisualPrompt ?? blueprint.EmotionalAngle);
        var svg = $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="1080" height="1080" viewBox="0 0 1080 1080">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#111827"/>
                  <stop offset="0.55" stop-color="#0f766e"/>
                  <stop offset="1" stop-color="#f59e0b"/>
                </linearGradient>
                <filter id="shadow" x="-20%" y="-20%" width="140%" height="140%">
                  <feDropShadow dx="0" dy="22" stdDeviation="22" flood-color="#000000" flood-opacity="0.35"/>
                </filter>
              </defs>
              <rect width="1080" height="1080" fill="url(#bg)"/>
              <circle cx="830" cy="180" r="180" fill="#ffffff" opacity="0.10"/>
              <circle cx="160" cy="880" r="260" fill="#ffffff" opacity="0.08"/>
              <rect x="86" y="92" width="908" height="896" rx="36" fill="#ffffff" opacity="0.96" filter="url(#shadow)"/>
              <rect x="126" y="132" width="828" height="370" rx="28" fill="#111827"/>
              <rect x="168" y="178" width="210" height="278" rx="24" fill="#f59e0b"/>
              <rect x="198" y="212" width="150" height="128" rx="16" fill="#fff7ed"/>
              <circle cx="226" cy="385" r="16" fill="#10b981"/>
              <circle cx="274" cy="385" r="16" fill="#10b981"/>
              <circle cx="322" cy="385" r="16" fill="#10b981"/>
              <path d="M458 220h330" stroke="#ffffff" stroke-width="28" stroke-linecap="round" opacity="0.95"/>
              <path d="M458 292h410" stroke="#ffffff" stroke-width="18" stroke-linecap="round" opacity="0.70"/>
              <path d="M458 348h360" stroke="#ffffff" stroke-width="18" stroke-linecap="round" opacity="0.55"/>
              <text x="126" y="585" font-family="Arial, Helvetica, sans-serif" font-size="34" font-weight="700" fill="#0f766e">{{title}}</text>
              <foreignObject x="126" y="620" width="828" height="170">
                <div xmlns="http://www.w3.org/1999/xhtml" style="font-family: Arial, Helvetica, sans-serif; font-size: 58px; line-height: 1.04; font-weight: 800; color: #111827;">{{headline}}</div>
              </foreignObject>
              <foreignObject x="126" y="790" width="828" height="92">
                <div xmlns="http://www.w3.org/1999/xhtml" style="font-family: Arial, Helvetica, sans-serif; font-size: 29px; line-height: 1.25; color: #475569;">{{caption}}</div>
              </foreignObject>
              <rect x="126" y="900" width="520" height="78" rx="18" fill="#10b981"/>
              <text x="158" y="950" font-family="Arial, Helvetica, sans-serif" font-size="30" font-weight="800" fill="#ffffff">{{cta}}</text>
              <title>{{visual}}</title>
            </svg>
            """;

        var dataUrl = $"data:image/svg+xml;charset=utf-8,{Uri.EscapeDataString(svg)}";
        return JsonSerializer.Serialize(new ImageGenerationResult(
            Type: "image",
            Model: string.IsNullOrWhiteSpace(model) ? "local-svg-ad-renderer" : model,
            Prompt: AdvertisementMediaPromptBuilder.BuildImagePrompt(blueprint),
            ImageUrl: null,
            ImageDataUrl: dataUrl,
            Message: "Generated a local SVG ad image with no external API key or queued image provider."));
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}

internal static class VideoManifestGenerator
{
    public static string Generate(string message)
    {
        var blueprint = BlueprintGenerator.Generate(message);
        return JsonSerializer.Serialize(new
        {
            Type = "video",
            Model = "reelforge-video-manifest-v1",
            VideoUrl = (string?)null,
            Message = "A no-key hosted video renderer is not configured. Returned a video-ready render manifest with timed scenes, voiceover, music, captions, transitions, and asset prompts.",
            Title = blueprint.AdTitle,
            AspectRatio = "9:16",
            DurationSeconds = 30,
            VoiceoverScript = blueprint.VoiceoverScript,
            MusicDirection = blueprint.MusicDirection,
            Captions = blueprint.Captions,
            Scenes = blueprint.Scenes.Select(scene => new
            {
                scene.SceneNumber,
                scene.StartTime,
                scene.EndTime,
                scene.Purpose,
                scene.VisualPrompt,
                scene.CameraDirection,
                scene.MotionDirection,
                scene.OnScreenText,
                scene.VoiceoverSegment,
                Transition = $"{scene.TransitionIn} -> {scene.TransitionOut}",
                scene.BrollType,
                scene.EmotionalTone
            }),
            CallToAction = blueprint.CallToAction,
            TtsDirection = blueprint.TtsDirection
        });
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
        "pollinations",
        "flux",
        "stable-diffusion",
        "gpt-image-1.5",
        "gpt-image-1",
        "gpt-image-1-mini",
        "dall-e-3",
        "dall-e-2"
    };

    public static bool IsImageGenerationRequest(string message, string model)
    {
        if (IsKnownImageModel(model)) return true;

        var lower = message.ToLowerInvariant();
        return lower.Contains("show me a picture")
            || lower.Contains("generate an image")
            || lower.Contains("create an image")
            || lower.Contains("make an image")
            || lower.Contains("draw ")
            || lower.Contains("picture of")
            || lower.Contains("image of");
    }

    public static bool IsKnownImageModel(string model) => ImageModels.Contains(model);

    public static async Task<string> GenerateAsync(string message, string model, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (TryGetKnownPublicImage(message, model, out var knownImageResponse))
            return knownImageResponse;

        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return GeneratePollinationsResponse(message, model, "OPENAI_API_KEY is not configured, so a no-key Pollinations image URL was returned instead.");
        }

        if (model.Equals("pollinations", StringComparison.OrdinalIgnoreCase)
            || model.Equals("flux", StringComparison.OrdinalIgnoreCase)
            || model.Equals("stable-diffusion", StringComparison.OrdinalIgnoreCase))
            return GeneratePollinationsResponse(message, model, "No-key Pollinations image URL generated.");

        var prompt = NormalizeImagePrompt(message);
        return await GenerateOpenAiAsync(prompt, model, apiKey, httpClientFactory, cancellationToken);
    }

    public static async Task<string> GenerateOpenAiAsync(string prompt, string model, string apiKey, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
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

    private static string GeneratePollinationsResponse(string message, string model, string note)
    {
        var prompt = NormalizeImagePrompt(message);
        var encodedPrompt = Uri.EscapeDataString(prompt);
        var imageUrl = $"https://image.pollinations.ai/prompt/{encodedPrompt}?width=1024&height=1024&nologo=true";

        return JsonSerializer.Serialize(new ImageGenerationResult(
            Type: "image",
            Model: string.IsNullOrWhiteSpace(model) ? "pollinations" : model,
            Prompt: prompt,
            ImageUrl: imageUrl,
            ImageDataUrl: null,
            Message: note));
    }

    private static bool TryGetKnownPublicImage(string message, string model, out string response)
    {
        var normalized = message.ToLowerInvariant();
        if (normalized.Contains("earth") || normalized.Contains("planet"))
        {
            response = JsonSerializer.Serialize(new ImageGenerationResult(
                Type: "image",
                Model: string.IsNullOrWhiteSpace(model) ? "public-image" : model,
                Prompt: NormalizeImagePrompt(message),
                ImageUrl: "https://upload.wikimedia.org/wikipedia/commons/9/97/The_Earth_seen_from_Apollo_17.jpg",
                ImageDataUrl: null,
                Message: "Returned a stable public-domain Earth image instead of a queued no-key generator."));
            return true;
        }

        response = "";
        return false;
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
