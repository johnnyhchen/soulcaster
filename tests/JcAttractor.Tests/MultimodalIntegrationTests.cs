using JcAttractor.UnifiedLlm;

namespace JcAttractor.Tests;

public class MultimodalIntegrationTests
{
    private static string? OpenAiApiKey => Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    private static string? GeminiApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    private static string ArtifactRoot =>
        Environment.GetEnvironmentVariable("SOULCASTER_MULTIMODAL_ARTIFACT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "soulcaster-multimodal-artifacts");

    [SkippableFact]
    public async Task OpenAi_ImageInputAndImageOutput_RoundTrips()
    {
        Skip.If(string.IsNullOrWhiteSpace(OpenAiApiKey), "OPENAI_API_KEY not set.");

        var adapter = new OpenAiAdapter(OpenAiApiKey!);
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

    private static void PersistArtifacts(string provider, Response response)
    {
        var providerDir = Path.Combine(ArtifactRoot, provider);
        Directory.CreateDirectory(providerDir);

        File.WriteAllText(Path.Combine(providerDir, "response.txt"), response.Text);

        for (var i = 0; i < response.Images.Count; i++)
        {
            var image = response.Images[i];
            if (image.Data is null || image.Data.Length == 0)
                continue;

            var extension = GetImageExtension(image.MediaType);
            var path = Path.Combine(providerDir, $"image-{i + 1}{extension}");
            File.WriteAllBytes(path, image.Data);
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
