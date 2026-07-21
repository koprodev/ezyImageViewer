#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SourcePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $repositoryRoot 'icon.png'
}
$source = Get-Item -LiteralPath ([IO.Path]::GetFullPath($SourcePath)) -Force -ErrorAction Stop
if ($source.PSIsContainer -or $source.Extension -cne '.png') {
    throw 'SourcePath must reference a PNG file.'
}

Add-Type -AssemblyName System.Drawing
if (-not ('EzyImageViewer.Packaging.BrandAssetGenerator' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace EzyImageViewer.Packaging
{
    public static class BrandAssetGenerator
    {
        public static void Generate(string sourcePath, string assetDirectory, string iconPath)
        {
            using (Bitmap source = new Bitmap(sourcePath))
            using (Bitmap transparent = RemoveConnectedCheckerboard(source))
            {
                Directory.CreateDirectory(assetDirectory);
                SavePng(transparent, Path.Combine(assetDirectory, "Square44x44Logo.png"), 44);
                SavePng(transparent, Path.Combine(assetDirectory, "StoreLogo.png"), 50);
                SavePng(transparent, Path.Combine(assetDirectory, "Square150x150Logo.png"), 150);
                SaveIcon(transparent, iconPath, new int[] { 16, 24, 32, 48, 64, 128, 256 });
            }
        }

        private static Bitmap RemoveConnectedCheckerboard(Bitmap source)
        {
            Bitmap result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.DrawImageUnscaled(source, 0, 0);
            }

            Rectangle bounds = new Rectangle(0, 0, result.Width, result.Height);
            BitmapData data = result.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int byteCount = Math.Abs(data.Stride) * data.Height;
                byte[] pixels = new byte[byteCount];
                Marshal.Copy(data.Scan0, pixels, 0, byteCount);
                bool[] background = new bool[result.Width * result.Height];
                Queue<int> queue = new Queue<int>();

                for (int x = 0; x < result.Width; x++)
                {
                    EnqueueBackground(x, 0, result.Width, result.Height, data.Stride, pixels, background, queue);
                    EnqueueBackground(x, result.Height - 1, result.Width, result.Height, data.Stride, pixels, background, queue);
                }
                for (int y = 1; y < result.Height - 1; y++)
                {
                    EnqueueBackground(0, y, result.Width, result.Height, data.Stride, pixels, background, queue);
                    EnqueueBackground(result.Width - 1, y, result.Width, result.Height, data.Stride, pixels, background, queue);
                }

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    int x = index % result.Width;
                    int y = index / result.Width;
                    EnqueueBackground(x - 1, y, result.Width, result.Height, data.Stride, pixels, background, queue);
                    EnqueueBackground(x + 1, y, result.Width, result.Height, data.Stride, pixels, background, queue);
                    EnqueueBackground(x, y - 1, result.Width, result.Height, data.Stride, pixels, background, queue);
                    EnqueueBackground(x, y + 1, result.Width, result.Height, data.Stride, pixels, background, queue);
                }

                for (int index = 0; index < background.Length; index++)
                {
                    if (!background[index])
                    {
                        continue;
                    }
                    int x = index % result.Width;
                    int y = index / result.Width;
                    int offset = y * data.Stride + x * 4;
                    pixels[offset] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                    pixels[offset + 3] = 0;
                }
                Marshal.Copy(pixels, 0, data.Scan0, byteCount);
            }
            finally
            {
                result.UnlockBits(data);
            }
            return result;
        }

        private static void EnqueueBackground(int x, int y, int width, int height, int stride,
            byte[] pixels, bool[] background, Queue<int> queue)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                return;
            }
            int index = y * width + x;
            if (background[index])
            {
                return;
            }
            int offset = y * stride + x * 4;
            int blue = pixels[offset];
            int green = pixels[offset + 1];
            int red = pixels[offset + 2];
            int alpha = pixels[offset + 3];
            int minimum = Math.Min(red, Math.Min(green, blue));
            int maximum = Math.Max(red, Math.Max(green, blue));
            if (alpha == 0 || (minimum >= 235 && maximum - minimum <= 8))
            {
                background[index] = true;
                queue.Enqueue(index);
            }
        }

        private static Bitmap Resize(Bitmap source, int size)
        {
            Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(source, new Rectangle(0, 0, size, size),
                    new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return result;
        }

        private static void SavePng(Bitmap source, string path, int size)
        {
            using (Bitmap resized = Resize(source, size))
            {
                resized.Save(path, ImageFormat.Png);
            }
        }

        private static void SaveIcon(Bitmap source, string path, int[] sizes)
        {
            List<byte[]> frames = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (Bitmap resized = Resize(source, size))
                using (MemoryStream stream = new MemoryStream())
                {
                    resized.Save(stream, ImageFormat.Png);
                    frames.Add(stream.ToArray());
                }
            }

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)frames.Count);
                int offset = 6 + frames.Count * 16;
                for (int index = 0; index < frames.Count; index++)
                {
                    int size = sizes[index];
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)frames[index].Length);
                    writer.Write((uint)offset);
                    offset += frames[index].Length;
                }
                foreach (byte[] frame in frames)
                {
                    writer.Write(frame);
                }
            }
        }
    }
}
'@
}

$assetDirectory = Join-Path $repositoryRoot 'packaging\Assets'
$iconPath = Join-Path $repositoryRoot 'EzyImageViewer.App\Assets\ezyImageViewer.ico'
[EzyImageViewer.Packaging.BrandAssetGenerator]::Generate(
    $source.FullName, $assetDirectory, $iconPath)

Get-ChildItem -LiteralPath $assetDirectory -File |
    Where-Object Name -in @('Square44x44Logo.png', 'StoreLogo.png', 'Square150x150Logo.png') |
    Sort-Object Name |
    Select-Object Name, Length
Get-Item -LiteralPath $iconPath | Select-Object Name, Length
