using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Desktop.Views.Workspace;

/// <summary>Renders Telegram custom emoji still previews in text without changing the original message.</summary>
public static class CustomEmojiText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(CustomEmojiText), new PropertyMetadata(string.Empty, Rebuild));
    public static readonly DependencyProperty EntitiesProperty = DependencyProperty.RegisterAttached(
        "Entities", typeof(string), typeof(CustomEmojiText), new PropertyMetadata(string.Empty, Rebuild));

    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetEntities(DependencyObject target, string value) => target.SetValue(EntitiesProperty, value);
    public static string GetEntities(DependencyObject target) => (string)target.GetValue(EntitiesProperty);

    private static void Rebuild(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock block) return;
        block.Inlines.Clear();
        var text = GetText(block) ?? string.Empty;
        TelegramCustomEmoji[] entities;
        try { entities = JsonSerializer.Deserialize<TelegramCustomEmoji[]>(GetEntities(block) ?? "") ?? []; }
        catch (JsonException) { entities = []; }
        var position = 0;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatPulse", "Data", "custom-emoji");
        foreach (var entity in entities.OrderBy(item => item.Offset))
        {
            // Telegram offsets are UTF-16 indices (the same indexing used by .NET strings).
            if (entity.Offset < position || entity.Offset < 0 || entity.Length <= 0 ||
                entity.Offset > text.Length || entity.Length > text.Length - entity.Offset) continue;
            if (entity.Offset > position) block.Inlines.Add(new Run(text[position..entity.Offset]));
            var path = new[] { Path.Combine(directory, $"{entity.DocumentId}.png"),
                               Path.Combine(directory, $"{entity.DocumentId}.jpg") }
                .FirstOrDefault(File.Exists);
            if (path is null)
                block.Inlines.Add(new Run(text.Substring(entity.Offset, entity.Length)));
            else
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    var image = new Image { Source = bitmap, Width = 17, Height = 17, Stretch = System.Windows.Media.Stretch.Uniform,
                        ToolTip = text.Substring(entity.Offset, entity.Length) };
                    block.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
                }
                catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
                {
                    block.Inlines.Add(new Run(text.Substring(entity.Offset, entity.Length)));
                }
            }
            position = entity.Offset + entity.Length;
        }
        if (position < text.Length) block.Inlines.Add(new Run(text[position..]));
    }
}
