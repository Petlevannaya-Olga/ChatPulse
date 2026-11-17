using System.IO;
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
            // A valid Lottie document needs a drawable layer. An empty `layers` array
            // can be rejected by Skottie because it describes no animation content.
            const string json = """
                {
                  "v":"5.5.7", "fr":30, "ip":0, "op":30,
                  "w":64, "h":64, "nm":"test-square", "ddd":0, "assets":[],
                  "layers":[{
                    "ddd":0, "ind":1, "ty":4, "nm":"square", "sr":1,
                    "ks":{
                      "o":{"a":0,"k":100}, "r":{"a":0,"k":0},
                      "p":{"a":0,"k":[32,32,0]}, "a":{"a":0,"k":[0,0,0]},
                      "s":{"a":0,"k":[100,100,100]}
                    },
                    "ao":0,
                    "shapes":[
                      {"ty":"rc", "d":1,
                       "s":{"a":0,"k":[32,32]}, "p":{"a":0,"k":[0,0]},
                       "r":{"a":0,"k":0}, "nm":"Rectangle"},
                      {"ty":"fl", "c":{"a":0,"k":[0,0.5,1,1]},
                       "o":{"a":0,"k":100}, "r":1, "nm":"Fill"}
                    ],
                    "ip":0, "op":30, "st":0, "bm":0
                  }]
                }
                """;
            using (var file = File.Create(input))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            {
                gzip.Write(Encoding.UTF8.GetBytes(json));
            }

            Assert.True(TelegramCustomEmojiPreview.TryRenderTgs(input, output),
                "Rendering a valid Lottie rectangle failed; check the Skottie runtime and native dependencies.");
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
