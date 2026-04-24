using Soulcaster.UnifiedLlm;

namespace Soulcaster.Tests;

public class MultimodalIntegrationTests
{
    private static string? OpenAIApiKey => Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    private static string? GeminiApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    private static string ArtifactRoot =>
        Environment.GetEnvironmentVariable("SOULCASTER_MULTIMODAL_ARTIFACT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "soulcaster-multimodal-artifacts");

    [SkippableFact]
    public async Task OpenAI_ImageInputAndImageOutput_RoundTrips()
    {
        Skip.If(string.IsNullOrWhiteSpace(OpenAIApiKey), "OPENAI_API_KEY not set.");

        var adapter = new OpenAIAdapter(OpenAIApiKey!);
        var response = await adapter.CompleteAsync(new Request
        {
            Model = Environment.GetEnvironmentVariable("OPENAI_IMAGE_MODEL") ?? "gpt-5.4",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart(
                        "Use the attached tiny red sample as inspiration. Return a simple badge-style icon that keeps a strong red center and also include a one-sentence caption."),
                    ContentPart.ImagePart(ImageData.FromBytes(
                        Convert.FromBase64String(UnifiedLlmTestAssets.TestImageBase64),
                        "image/png")))
            ],
            OutputModalities = [ResponseModality.Text, ResponseModality.Image],
            MaxTokens = 512
        });

        Assert.NotNull(response);
        Assert.Equal("openai", response.Provider);
        Assert.NotEmpty(response.Images);
        Assert.All(response.Images, image => Assert.True(image.Data?.Length > 0));

        PersistArtifacts("openai", response);
    }

    [SkippableFact]
    public async Task OpenAI_ImageOutput_FollowUpTurn_RoundTrips()
    {
        Skip.If(string.IsNullOrWhiteSpace(OpenAIApiKey), "OPENAI_API_KEY not set.");

        var adapter = new OpenAIAdapter(OpenAIApiKey!);
        var model = Environment.GetEnvironmentVariable("OPENAI_IMAGE_MODEL") ?? "gpt-5.4";
        var initialUser = Message.UserMsg("Create a simple badge-style icon and include one image.");
        var firstResponse = await adapter.CompleteAsync(new Request
        {
            Model = model,
            Messages = [initialUser],
            OutputModalities = [ResponseModality.Text, ResponseModality.Image],
            MaxTokens = 512
        });

        Assert.NotEmpty(firstResponse.Images);
        Assert.NotNull(firstResponse.Images[0].ProviderState);

        var secondResponse = await adapter.CompleteAsync(new Request
        {
            Model = model,
            Messages =
            [
                initialUser,
                firstResponse.Message,
                Message.UserMsg("Continue the same conversation and produce a refined variation of the earlier image.")
            ],
            OutputModalities = [ResponseModality.Text, ResponseModality.Image],
            MaxTokens = 512
        });

        Assert.Equal("openai", secondResponse.Provider);
        Assert.NotEmpty(secondResponse.Images);

        PersistArtifacts("openai-followup", firstResponse, secondResponse);
    }

    [SkippableFact]
    public async Task Gemini_ImageInputAndImageOutput_RoundTrips()
    {
        Skip.If(string.IsNullOrWhiteSpace(GeminiApiKey), "GEMINI_API_KEY not set.");

        var adapter = new GeminiAdapter(GeminiApiKey!);
        var response = await adapter.CompleteAsync(new Request
        {
            Model = Environment.GetEnvironmentVariable("GEMINI_IMAGE_MODEL") ?? "gemini-3.1-flash-image-preview",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart(
                        "Using the attached tiny red sample, create a playful polished icon variation and return both a caption and an image."),
                    ContentPart.ImagePart(ImageData.FromBytes(
                        Convert.FromBase64String(UnifiedLlmTestAssets.TestImageBase64),
                        "image/png")))
            ],
            OutputModalities = [ResponseModality.Text, ResponseModality.Image],
            MaxTokens = 1024
        });

        Assert.NotNull(response);
        Assert.Equal("gemini", response.Provider);
        Assert.NotEmpty(response.Images);
        Assert.All(response.Images, image => Assert.True(image.Data?.Length > 0));

        PersistArtifacts("gemini", response);
    }

    [SkippableFact]
    public async Task Gemini_ImageOutput_FollowUpTurn_RoundTrips()
    {
        Skip.If(string.IsNullOrWhiteSpace(GeminiApiKey), "GEMINI_API_KEY not set.");

        var adapter = new GeminiAdapter(GeminiApiKey!);
        var model = Environment.GetEnvironmentVariable("GEMINI_IMAGE_MODEL") ?? "gemini-3.1-flash-image-preview";
        var initialUser = Message.UserMsg("Create a simple badge-style icon and include one image.");
        var firstResponse = await adapter.CompleteAsync(new Request
        {
            Model = model,
            Messages = [initialUser],
            OutputModalities = [ResponseModality.Text, ResponseModality.Image],
            MaxTokens = 1024
        });

        Assert.NotEmpty(firstResponse.Images);
        Assert.NotNull(firstResponse.Images[0].ProviderState);

        var secondResponse = await adapter.CompleteAsync(new Request
        {
            Model = model,
            Messages =
            [
                initialUser,
                firstResponse.Message,
                Message.UserMsg("Continue the same conversation and produce a refined variation of the earlier image.")
            ],
            OutputModalities = [ResponseModality.Text, ResponseModality.Image],
            MaxTokens = 1024
        });

        Assert.Equal("gemini", secondResponse.Provider);
        Assert.NotEmpty(secondResponse.Images);

        PersistArtifacts("gemini-followup", firstResponse, secondResponse);
    }

    private static void PersistArtifacts(string provider, params Response[] responses)
    {
        var providerDir = Path.Combine(ArtifactRoot, provider);
        Directory.CreateDirectory(providerDir);

        for (var responseIndex = 0; responseIndex < responses.Length; responseIndex++)
        {
            var response = responses[responseIndex];
            var prefix = responses.Length == 1 ? string.Empty : $"response-{responseIndex + 1}-";
            File.WriteAllText(Path.Combine(providerDir, $"{prefix}response.txt"), response.Text);

            for (var imageIndex = 0; imageIndex < response.Images.Count; imageIndex++)
            {
                var image = response.Images[imageIndex];
                if (image.Data is null || image.Data.Length == 0)
                    continue;

                var extension = GetImageExtension(image.MediaType);
                var path = Path.Combine(providerDir, $"{prefix}image-{imageIndex + 1}{extension}");
                File.WriteAllBytes(path, image.Data);
            }
        }
    }

    private static string GetImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".png"
    };
}
