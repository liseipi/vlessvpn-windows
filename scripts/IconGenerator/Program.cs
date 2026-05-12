using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

if (args.Length == 0)
{
    Console.WriteLine("用法: IconGenerator <logo.png> [output.ico]");
    return;
}

string inputPath = args[0];
string outputPath = args.Length > 1 ? args[1]
    : Path.Combine(Path.GetDirectoryName(inputPath)!, "app.ico");

Console.WriteLine($"源文件: {inputPath}");

using var source = new Bitmap(inputPath);
int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

var entries = new List<(int size, byte[] bmp)>();

foreach (int s in sizes)
{
    using var bmp = new Bitmap(source, s, s);
    var bmpData = bmp.LockBits(
        new Rectangle(0, 0, s, s),
        ImageLockMode.ReadOnly,
        PixelFormat.Format32bppArgb);

    int stride = Math.Abs(bmpData.Stride);
    byte[] rowData = new byte[stride * s];
    Marshal.Copy(bmpData.Scan0, rowData, 0, rowData.Length);
    bmp.UnlockBits(bmpData);

    // BGRA bottom-up pixels
    byte[] pixels = new byte[s * s * 4];
    for (int y = 0; y < s; y++)
    {
        int srcRow = y * stride;
        int dstRow = (s - 1 - y) * s * 4;
        for (int x = 0; x < s; x++)
        {
            int si = srcRow + x * 4;
            int di = dstRow + x * 4;
            pixels[di + 0] = rowData[si + 0];  // B
            pixels[di + 1] = rowData[si + 1];  // G
            pixels[di + 2] = rowData[si + 2];  // R
            pixels[di + 3] = 255;              // A (force opaque)
        }
    }

    // AND mask: all 0 (opaque), row-aligned to 4 bytes
    int andStride = ((s + 31) / 32) * 4;
    byte[] andMask = new byte[andStride * s];

    // Combine into single BMP blob
    int total = 40 + pixels.Length + andMask.Length;
    byte[] blob = new byte[total];
    int off = 0;

    // BITMAPINFOHEADER (40 bytes)
    PutI32(blob, ref off, 40);            // biSize
    PutI32(blob, ref off, s);             // biWidth
    PutI32(blob, ref off, s * 2);         // biHeight (icon convention: doubled)
    PutI16(blob, ref off, 1);             // biPlanes
    PutI16(blob, ref off, 32);            // biBitCount
    PutI32(blob, ref off, 0);             // biCompression (BI_RGB)
    PutI32(blob, ref off, pixels.Length); // biSizeImage
    PutI32(blob, ref off, 0);             // biXPelsPerMeter
    PutI32(blob, ref off, 0);             // biYPelsPerMeter
    PutI32(blob, ref off, 0);             // biClrUsed
    PutI32(blob, ref off, 0);             // biClrImportant

    Buffer.BlockCopy(pixels, 0, blob, off, pixels.Length);
    off += pixels.Length;
    Buffer.BlockCopy(andMask, 0, blob, off, andMask.Length);

    entries.Add((s, blob));
    Console.WriteLine($"  {s}x{s}  ({blob.Length} bytes)");
}

// Write .ico
using var fs = File.Create(outputPath);

// ICO header: reserved(2) + type(2) + count(2)
fs.WriteByte(0); fs.WriteByte(0);
fs.WriteByte(1); fs.WriteByte(0);
WriteInt16LE(fs, (short)entries.Count);

// Directory entries
int dataOffset = 6 + 16 * entries.Count;
foreach (var (size, blob) in entries)
{
    byte s = (byte)(size >= 256 ? 0 : size);
    fs.WriteByte(s);
    fs.WriteByte(s);
    fs.WriteByte(0);
    fs.WriteByte(0);
    WriteInt16LE(fs, 1);           // planes
    WriteInt16LE(fs, 32);          // bit count
    WriteInt32LE(fs, blob.Length); // size
    WriteInt32LE(fs, dataOffset);  // offset
    dataOffset += blob.Length;
}

// Image data
foreach (var (_, blob) in entries)
    fs.Write(blob, 0, blob.Length);

Console.WriteLine($"\n已生成: {Path.GetFullPath(outputPath)}");

static void WriteInt16LE(Stream s, short v)
{
    s.WriteByte((byte)v);
    s.WriteByte((byte)(v >> 8));
}

static void WriteInt32LE(Stream s, int v)
{
    s.WriteByte((byte)v);
    s.WriteByte((byte)(v >> 8));
    s.WriteByte((byte)(v >> 16));
    s.WriteByte((byte)(v >> 24));
}

static void PutI32(byte[] buf, ref int off, int v)
{
    buf[off] = (byte)v; off++;
    buf[off] = (byte)(v >> 8); off++;
    buf[off] = (byte)(v >> 16); off++;
    buf[off] = (byte)(v >> 24); off++;
}

static void PutI16(byte[] buf, ref int off, short v)
{
    buf[off] = (byte)v; off++;
    buf[off] = (byte)(v >> 8); off++;
}
