using System;
using System.Runtime.InteropServices;

namespace LiquidMonitor;


/// <summary>
/// Wraps a tightly packed BGRA buffer in a 24bpp BITMAPINFOHEADER image.
/// </summary>
/// <remarks>
/// This exists because WinUI 3's WriteableBitmap exposes no way to fill itself
/// from a byte array in this SDK: neither CopyFromBuffer nor CopyFromPixels
/// appear in Microsoft.UI.Xaml.winmd, so there is no direct pixel upload path.
/// A BMP is about fifty four bytes of header, needs no compression, and decodes
/// through the same imaging pipeline BitmapImage already uses.
///
/// 24bpp BGR is deliberate. A 32bpp BI_RGB file is structurally valid and is
/// produced here byte for byte correctly, but both GDI+ and the Windows imaging
/// pipeline that BitmapImage uses reject it unless the header carries explicit
/// colour masks. That decode fails silently inside SetSourceAsync, leaving the
/// image blank and the panel looking like an opaque plate. 24bpp needs no masks
/// and is accepted everywhere.
/// </remarks>
internal static class BmpEncoder
{
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        // Rows are padded to a 4 byte boundary, which 24bpp does not guarantee.
        int stride = (width * 3 + 3) & ~3;

        int dataOffset = 14 + 40;
        int pixelBytes = stride * height;
        int fileSize = dataOffset + pixelBytes;

        var file = new byte[fileSize];

        file[0] = (byte)'B';
        file[1] = (byte)'M';

        WriteInt32(file, 2, fileSize);
        WriteInt32(file, 10, dataOffset);

        // BITMAPINFOHEADER. Note that biPlanes and biBitCount are 16-bit
        // shorts, so they sit at 26 and 28 and biCompression starts at 30.
        // Writing them as 32-bit ints shifts every following field by four
        // bytes, which produces a file that looks plausible in a hex dump but
        // declares biCompression = 24 (BI_RLE24) and is rejected by the
        // decoder.
        WriteInt32(file, 14, 40);
        WriteInt32(file, 18, width);

        // Positive height: BMP rows are stored bottom-up, which is the
        // opposite of the top-down order the capture produces.
        WriteInt32(file, 22, height);
        WriteInt16(file, 26, 1);
        WriteInt16(file, 28, 24);
        WriteInt32(file, 30, 0);
        WriteInt32(file, 34, pixelBytes);

        int destination = dataOffset;

        for (int y = height - 1; y >= 0; y--)
        {
            int source = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int read = source + (x * 4);
                int write = destination + (x * 3);

                // Drop alpha: the grab is opaque, and 24bpp has no room for it.
                file[write] = bgra[read];
                file[write + 1] = bgra[read + 1];
                file[write + 2] = bgra[read + 2];
            }

            destination += stride;
        }

        return file;
    }


    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }


    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}


/// <summary>
/// A separable box blur, run several times to approximate a Gaussian.
/// Cheap enough to run on a few hundred pixels every refresh, and it keeps the
/// result in a plain byte array that can be uploaded straight to a
/// WriteableBitmap with no intermediate surfaces.
/// </summary>
internal static class FastBlur
{
    /// <summary>
    /// Blurs a BGRA buffer in place using <paramref name="passes"/> box
    /// iterations of the given radius. Three iterations is a close enough
    /// stand-in for a true Gaussian at the strength glass needs.
    /// </summary>
    public static void Blur(byte[] pixels, int width, int height, int radius, int passes)
    {
        if (radius < 1 || passes < 1)
            return;

        var scratch = new byte[pixels.Length];

        for (int pass = 0; pass < passes; pass++)
        {
            BlurHorizontal(pixels, scratch, width, height, radius);
            BlurVertical(scratch, pixels, width, height, radius);
        }
    }


    private static void BlurHorizontal(
        byte[] source, byte[] destination, int width, int height, int radius)
    {
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width * 4;

            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                int window = radius * 2 + 1;

                for (int i = -radius; i <= radius; i++)
                {
                    sum += source[rowStart + (Clamp(i, width - 1) * 4) + channel];
                }

                for (int x = 0; x < width; x++)
                {
                    destination[rowStart + (x * 4) + channel] = (byte)(sum / window);

                    int remove = Clamp(x - radius, width - 1) * 4 + channel;
                    int add = Clamp(x + radius + 1, width - 1) * 4 + channel;

                    sum -= source[rowStart + remove];
                    sum += source[rowStart + add];
                }
            }

            // Alpha is meaningless here: the source is an opaque screen grab.
            // Copying it through avoids a black fringe at the blurred edges.
            for (int x = 0; x < width; x++)
                destination[rowStart + (x * 4) + 3] = source[rowStart + (x * 4) + 3];
        }
    }


    private static void BlurVertical(
        byte[] source, byte[] destination, int width, int height, int radius)
    {
        int stride = width * 4;

        for (int x = 0; x < width; x++)
        {
            int columnStart = x * 4;

            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                int window = radius * 2 + 1;

                for (int i = -radius; i <= radius; i++)
                {
                    sum += source[(Clamp(i, height - 1) * stride) + columnStart + channel];
                }

                for (int y = 0; y < height; y++)
                {
                    destination[(y * stride) + columnStart + channel] =
                        (byte)(sum / window);

                    int remove = Clamp(y - radius, height - 1) * stride + columnStart + channel;
                    int add = Clamp(y + radius + 1, height - 1) * stride + columnStart + channel;

                    sum -= source[remove];
                    sum += source[add];
                }
            }

            for (int y = 0; y < height; y++)
                destination[(y * stride) + columnStart + 3] = source[(y * stride) + columnStart + 3];
        }
    }


    private static int Clamp(int value, int max)
    {
        if (value < 0)
            return 0;

        return value > max ? max : value;
    }
}


/// <summary>
/// Grabs a rectangle of the screen as raw 32bpp BGRA pixels.
///
/// The desktop behind the widget cannot be sampled by DWM on this machine
/// (see MainWindow.CreateGlassTintBrush), so the glass effect is built by
/// capturing the screen ourselves and blurring it. WinUI's BlurEffect then does
/// the blur on the GPU, so this only has to move pixels.
///
/// GDI is used rather than Windows.Graphics.Capture because it needs no extra
/// package and no device to be created; the region is tiny (a few hundred px).
///
/// IMPORTANT: the caller must make sure this window is not visible in the
/// captured rectangle, or the capture will contain the widget's own pixels.
/// </summary>
internal static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors0;
        public int bmiColors1;
        public int bmiColors2;
    }


    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hDC,
        ref BITMAPINFO pbmi,
        int usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        int offset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int rop);


    /// <summary>
    /// Halves a BGRA buffer by averaging each 2x2 block.
    ///
    /// The glass is blurred to a sigma of about 17px, so blurring a half size
    /// copy and letting the Image scale it back up is visually identical and
    /// four times cheaper. That matters because the expanded card's capture
    /// region is roughly four times the area of the pill's, which is what made
    /// the expanded background sluggish: every stage of the pipeline scaled
    /// with area, from the crop through the blur to the encode and the decode.
    /// </summary>
    public static byte[] Downscale2(byte[] source, int width, int height)
    {
        int targetWidth = width / 2;
        int targetHeight = height / 2;

        if (targetWidth <= 0 || targetHeight <= 0)
            return source;

        var result = new byte[targetWidth * targetHeight * 4];

        for (int y = 0; y < targetHeight; y++)
        {
            int topRow = (y * 2) * width;
            int bottomRow = ((y * 2) + 1) * width;
            int targetRow = y * targetWidth;

            for (int x = 0; x < targetWidth; x++)
            {
                int left = x * 2;
                int right = left + 1;

                int a = (topRow + left) * 4;
                int b = (topRow + right) * 4;
                int c = (bottomRow + left) * 4;
                int d = (bottomRow + right) * 4;

                int target = (targetRow + x) * 4;

                for (int channel = 0; channel < 3; channel++)
                {
                    result[target + channel] = (byte)(
                        (source[a + channel] +
                         source[b + channel] +
                         source[c + channel] +
                         source[d + channel]) >> 2);
                }

                result[target + 3] = 255;
            }
        }

        return result;
    }


    /// <summary>
    /// Fills a rectangle of the buffer with a smooth interpolation of the
    /// pixels immediately around it.
    ///
    /// This is what makes a self refreshing glass possible without any
    /// clipping. A screen grab contains this window, and the usual fix is to
    /// hide or clip the window for a frame, which visibly blanks it. Instead
    /// the window's rectangle is simply reconstructed from its border.
    ///
    /// Reconstruction is good enough here because everything is then blurred
    /// with a radius of about 17px, so the filled area is only ever seen as
    /// broad colour and brightness. It removes the only operation that could
    /// ever produce a blank frame, so there is nothing left to flicker.
    /// </summary>
    /// <param name="expand">
    /// Grows the filled rectangle outward, so the window's drop shadow is
    /// covered too rather than being sampled as the border.
    /// </param>
    /// <param name="skip">
    /// How far outside the grown rectangle the border samples are taken from.
    /// This has to clear the shadow, otherwise dark corner pixels are dragged
    /// into the reconstruction and show up as black corners on the glass.
    /// </param>
    public static void InpaintRegion(
        byte[] buffer,
        int width,
        int height,
        int x,
        int y,
        int regionWidth,
        int regionHeight,
        int expand,
        int skip)
    {
        if (regionWidth <= 1 || regionHeight <= 1)
            return;

        int left = x - expand;
        int top = y - expand;
        int right = x + regionWidth + expand;
        int bottom = y + regionHeight + expand;

        int sample = expand + skip;

        // Need a real, shadow free border on every side to sample from.
        if (left - sample <= 0 || top - sample <= 0 ||
            right + sample >= width || bottom + sample >= height)
        {
            return;
        }

        for (int row = top; row < bottom; row++)
        {
            float v = (row - top + 0.5f) / (bottom - top);
            int line = row * width;

            for (int column = left; column < right; column++)
            {
                float u = (column - left + 0.5f) / (right - left);

                int leftIndex = (line + (column - sample)) * 4;
                int rightIndex = (line + (column + sample)) * 4;
                int topIndex = ((row - sample) * width + column) * 4;
                int bottomIndex = ((row + sample) * width + column) * 4;

                int target = (line + column) * 4;

                for (int channel = 0; channel < 3; channel++)
                {
                    float horizontal =
                        buffer[leftIndex + channel] +
                        (buffer[rightIndex + channel] - buffer[leftIndex + channel]) * u;

                    float vertical =
                        buffer[topIndex + channel] +
                        (buffer[bottomIndex + channel] - buffer[topIndex + channel]) * v;

                    buffer[target + channel] =
                        (byte)((horizontal + vertical) * 0.5f);
                }

                // Keep alpha opaque so nothing downstream treats this as a hole.
                buffer[target + 3] = 255;
            }
        }
    }


    /// <summary>
    /// Copies a rectangle out of a larger BGRA buffer, clamping anything that
    /// falls outside the source to the nearest edge pixel.
    ///
    /// The clamping is the point. The glass needs a margin around the card so
    /// the blur has room and the drop shadow is covered, which means the
    /// requested rectangle routinely hangs off the edge of the screen whenever
    /// the card is near a boundary. Returning null in that case silently
    /// stopped the backdrop refreshing for the whole time the card sat within
    /// one margin of the right or bottom edge, which is most obvious in the
    /// bottom right corner where both edges fail at once.
    ///
    /// Edge extension is safe here because the overflowing part of the
    /// rectangle is off screen by definition. Every pixel that is actually
    /// visible still comes from its true screen position; only the invisible
    /// remainder is smeared outwards, which also gives the blur real content
    /// to work against instead of a transparent edge.
    /// </summary>
    public static byte[]? Crop(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        if (sourceWidth <= 0 || sourceHeight <= 0)
            return null;

        var result = new byte[width * height * 4];

        // Fast path: the whole rectangle is inside, so every row is a straight
        // copy. This is the common case and it stays a memcpy per row.
        bool inside =
            x >= 0 &&
            y >= 0 &&
            x + width <= sourceWidth &&
            y + height <= sourceHeight;

        for (int row = 0; row < height; row++)
        {
            int sourceRow = y + row;

            if (sourceRow < 0)
                sourceRow = 0;
            else if (sourceRow >= sourceHeight)
                sourceRow = sourceHeight - 1;

            int to = row * width * 4;
            int from = sourceRow * sourceWidth * 4;

            if (inside)
            {
                Buffer.BlockCopy(
                    source,
                    from + (x * 4),
                    result,
                    to,
                    width * 4);

                continue;
            }

            for (int column = 0; column < width; column++)
            {
                int sourceColumn = x + column;

                if (sourceColumn < 0)
                    sourceColumn = 0;
                else if (sourceColumn >= sourceWidth)
                    sourceColumn = sourceWidth - 1;

                Buffer.BlockCopy(
                    source,
                    from + (sourceColumn * 4),
                    result,
                    to + (column * 4),
                    4);
            }
        }

        return result;
    }


    /// <summary>
    /// Captures <paramref name="width"/> x <paramref name="height"/> pixels
    /// starting at (<paramref name="x"/>, <paramref name="y"/>), or null if the
    /// capture could not be set up.
    ///
    /// The result is tightly packed 32bpp BGRA with a stride of width * 4 and
    /// no row padding.
    /// </summary>
    public static byte[]? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);

            if (screenDc == IntPtr.Zero)
                return null;

            memDc = CreateCompatibleDC(screenDc);

            if (memDc == IntPtr.Zero)
                return null;

            // Negative biHeight requests a top-down DIB, so the rows arrive in
            // screen order and need no flip before upload.
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            IntPtr bits;

            dib = CreateDIBSection(
                memDc,
                ref info,
                DIB_RGB_COLORS,
                out bits,
                IntPtr.Zero,
                0);

            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                return null;

            IntPtr previous = SelectObject(memDc, dib);

            try
            {
                // CAPTUREBLT includes layered windows in the grab.
                if (!BitBlt(
                        memDc,
                        0,
                        0,
                        width,
                        height,
                        screenDc,
                        x,
                        y,
                        SRCCOPY | CAPTUREBLT))
                {
                    return null;
                }
            }
            finally
            {
                SelectObject(memDc, previous);
            }

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            return pixels;
        }
        catch (Exception)
        {
            // A failed grab should leave the previous glass in place rather
            // than blanking the widget.
            return null;
        }
        finally
        {
            if (dib != IntPtr.Zero)
                DeleteObject(dib);

            if (memDc != IntPtr.Zero)
                DeleteDC(memDc);

            if (screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}