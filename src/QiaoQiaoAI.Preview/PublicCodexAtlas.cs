using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace QiaoQiaoAI.Preview;

internal static class PublicCodexAtlas
{
    private const string FileName = "codex-spritesheet-v4.webp";
    private const string SourceUrl = "https://persistent.oaistatic.com/codex/pets/v1/" + FileName;
    private const int SourceSize = 887954;
    private static readonly byte[] ExpectedHash = Convert.FromHexString("5CD7AF9CFEAE88FE9C6E47A72460E0DC4C6FB04BAF7FDC6CCA14A212F0BF44D1");

    public static BitmapSource Load()
    {
        string cacheDirectory = Environment.GetEnvironmentVariable("AI_REPAIR_SHOP_ASSET_CACHE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiaoQiaoAI", "assets");
        if (!Path.IsPathFullyQualified(cacheDirectory)) throw new ArgumentException("AI_REPAIR_SHOP_ASSET_CACHE 需要指定绝对目录。");
        string destination = Path.Combine(cacheDirectory, FileName);
        if (ReadVerified(destination) is { } cached) return Decode(cached);

        // The TUI keeps its copy here. Read only this published asset, never change the Codex cache.
        string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        byte[]? bytes = Path.IsPathFullyQualified(codexHome)
            ? ReadVerified(Path.Combine(codexHome, "cache", "tui-pets", "v1", "assets", FileName))
            : null;
        bytes ??= Download(destination);
        BitmapSource atlas = Decode(bytes);
        Directory.CreateDirectory(cacheDirectory);
        string staging = Path.Combine(cacheDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(staging, bytes);
            // All concurrent writers publish the same verified bytes on the same volume.
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
        return atlas;
    }

    private static byte[]? ReadVerified(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length != SourceSize) return null;
            byte[] bytes = new byte[SourceSize];
            stream.ReadExactly(bytes);
            return IsVerified(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsVerified(byte[] bytes) =>
        bytes.Length == SourceSize && SHA256.HashData(bytes).AsSpan().SequenceEqual(ExpectedHash);

    private static byte[] Download(string destination)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = SourceSize
        };
        byte[] bytes;
        try
        {
            bytes = client.GetByteArrayAsync(SourceUrl).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new IOException($"首次启动需要从 OpenAI 下载 Codex 角色。请联网后重试，也可手动将 {SourceUrl} 保存到 {destination}。", ex);
        }
        if (!IsVerified(bytes)) throw new InvalidDataException("OpenAI 图集的 SHA-256 校验失败。请重新启动；若问题持续，请检查项目更新。");
        return bytes;
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        var format = new SKImageInfo(1536, 1872, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(bytes, format) ?? throw new InvalidDataException("无法解码 Codex 角色图集。");
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null,
            bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        source.Freeze();
        return source;
    }
}
