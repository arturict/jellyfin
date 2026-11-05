using System;
using System.IO;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Drawing.Skia.Tests;

/// <summary>
/// The reduced-scale JPEG decode must give the same size and, within resampling noise, the
/// same picture as the full-size decode it replaces.
/// </summary>
public sealed class SkiaEncoderScaledDecodeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jf-scaled-decode").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height),
                [new SKColor(20, 40, 160), new SKColor(240, 180, 40), new SKColor(30, 160, 90)],
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(SKRect.Create(width, height), paint);
            using var stripe = new SKPaint { Color = SKColors.White };
            for (var x = 0; x < width; x += width / 10)
            {
                canvas.DrawRect(SKRect.Create(x, 0, width / 40f, height), stripe);
            }
        }

        var path = Path.Combine(_dir, $"src-{width}x{height}.jpg");
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>The upstream pipeline: full-size decode, resize with sharpening, WebP.</summary>
    private static SKBitmap FullSizeReference(string path, int width, int height, int quality)
    {
        using var full = SKBitmap.Decode(path);
        var info = new SKImageInfo(width, height, full.ColorType, full.AlphaType, full.ColorSpace);
        using var resized = SkiaEncoder.ResizeImage(full, info);
        using var resizedBitmap = SKBitmap.FromImage(resized);
        using var encoded = resizedBitmap.Encode(SKEncodedImageFormat.Webp, quality);
        return SKBitmap.Decode(encoded);
    }

    internal static double Psnr(SKBitmap a, SKBitmap b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        double sum = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                sum += Math.Pow(p.Red - q.Red, 2) + Math.Pow(p.Green - q.Green, 2) + Math.Pow(p.Blue - q.Blue, 2);
            }
        }

        var mse = sum / (a.Width * a.Height * 3.0);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    [Theory]
    [InlineData(2000, 3000, 300, 96)] // poster at a grid size: decoded at 1/4 (500x750)
    [InlineData(1000, 1500, 300, 96)] // poster: decoded at 1/2
    [InlineData(1920, 1080, 1280, 80)] // backdrop: no smaller DCT scale fits, full-size path
    [InlineData(3840, 2160, 1280, 80)] // 4K backdrop: decoded at 1/2
    public void EncodeImage_JpegDownscale_MatchesFullSizePath(int width, int height, int fillWidth, int quality)
    {
        var source = WriteJpeg(width, height);
        var output = Path.Combine(_dir, $"out-{width}-{fillWidth}.webp");
        var encoder = new SkiaEncoder(NullLogger<SkiaEncoder>.Instance, null!);
        var options = new ImageProcessingOptions { FillWidth = fillWidth, Quality = quality, SupportedOutputFormats = [ImageFormat.Webp] };
        var expectedSize = ImageHelper.GetNewImageSize(options, new ImageDimensions(width, height));

        var result = encoder.EncodeImage(source, DateTime.UtcNow, output, false, null, quality, options, ImageFormat.Webp);

        Assert.Equal(output, result);
        using var actual = SKBitmap.Decode(result);
        Assert.Equal(expectedSize.Width, actual.Width);
        Assert.Equal(expectedSize.Height, actual.Height);
        using var reference = FullSizeReference(source, expectedSize.Width, expectedSize.Height, quality);
        // Different decode scale, same filter and sharpening: well above visible-difference levels.
        Assert.True(Psnr(reference, actual) > 32, $"PSNR {Psnr(reference, actual):F1} dB");
    }

    [Fact]
    public void EncodeImage_DefaultOptions_ReturnsSourcePath()
    {
        var source = WriteJpeg(400, 600);
        var encoder = new SkiaEncoder(NullLogger<SkiaEncoder>.Instance, null!);
        var options = new ImageProcessingOptions { Quality = 90, SupportedOutputFormats = [ImageFormat.Jpg] };

        var result = encoder.EncodeImage(source, DateTime.UtcNow, Path.Combine(_dir, "unused.webp"), false, null, 90, options, ImageFormat.Jpg);

        Assert.Equal(source, result);
    }
}
