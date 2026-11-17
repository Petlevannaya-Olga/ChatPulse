using System.IO.Compression;
using System.Text;
using ChatPulse.Infrastructure.Telegram;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramCustomEmojiPreviewTests
{
    [Fact]
    public void RenderTgs_CreatesPngFromMinimalLottieDocument()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Tgs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "emoji.tgs");
            var output = Path.Combine(directory, "emoji.png");
            const string json = """
                {"v":"5.5.7","fr":30,"ip":0,"op":30,"w":64,"h":64,"nm":"test","ddd":0,"assets":[],"layers":[]}
                """;
            using (var file = File.Create(input))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            {
                gzip.Write(Encoding.UTF8.GetBytes(json));
            }

            Assert.True(TelegramCustomEmojiPreview.TryRenderTgs(input, output));
            Assert.True(File.Exists(output));
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, File.ReadAllBytes(output)[..4]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RenderTgs_RejectsInvalidGzipWithoutWritingImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Tgs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "bad.tgs");
            var output = Path.Combine(directory, "bad.png");
            File.WriteAllText(input, "not a tgs");
            Assert.False(TelegramCustomEmojiPreview.TryRenderTgs(input, output));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
