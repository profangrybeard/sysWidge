using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Args: <input.png> <appIco> <trayIco> [cl ct cr cb]   (crop fractions of full image)
string input = args[0];
string appIco = args[1];
string trayIco = args[2];
float cl = args.Length > 3 ? float.Parse(args[3]) : 0.30f;
float ct = args.Length > 4 ? float.Parse(args[4]) : 0.11f;
float cr = args.Length > 5 ? float.Parse(args[5]) : 0.74f;
float cb = args.Length > 6 ? float.Parse(args[6]) : 0.37f;
bool recolor = args.Any(a => a.Equals("recolor", StringComparison.OrdinalIgnoreCase));

string outDir = Path.GetDirectoryName(Path.GetFullPath(appIco))!;
Directory.CreateDirectory(outDir);

using var src = new Bitmap(input);
var work = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(work)) g.DrawImageUnscaled(src, 0, 0);

RemoveBackground(work);
if (recolor) RecolorBoxers(work);
work.Save(Path.Combine(outDir, "_full_preview.png"), ImageFormat.Png);

// App icon: trim to content, emit multi-res.
var full = Crop(work, ContentBounds(work));
SaveIco(appIco, full, new[] { 256, 128, 64, 48, 32, 16 });

// Tray icon: crop the helmet+plume region, trim, square, emit small multi-res.
int hx = (int)(cl * work.Width), hy = (int)(ct * work.Height);
var helmetBox = new Rectangle(hx, hy, (int)(cr * work.Width) - hx, (int)(cb * work.Height) - hy);
using var helmet = Crop(work, helmetBox);
using var helmetTrim = Crop(helmet, ContentBounds(helmet));
using var traySquare = FitSquare(helmetTrim, 256, fill: true); // stretch to fill -> bigger/stubbier
traySquare.Save(Path.Combine(outDir, "_tray_preview.png"), ImageFormat.Png);
SaveIco(trayIco, traySquare, new[] { 48, 32, 24, 16 });

Console.WriteLine($"app: {full.Width}x{full.Height} -> {appIco}");
Console.WriteLine($"tray: helmet {helmetTrim.Width}x{helmetTrim.Height} -> {trayIco}");

// ---------------------------------------------------------------- helpers

static void RemoveBackground(Bitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    int stride = data.Stride, bytes = stride * h;
    byte[] buf = new byte[bytes];
    Marshal.Copy(data.Scan0, buf, 0, bytes);

    var visited = new bool[w * h];
    var stack = new Stack<int>();

    bool IsWhite(int x, int y)
    {
        int o = y * stride + x * 4;
        return buf[o] >= 240 && buf[o + 1] >= 240 && buf[o + 2] >= 240; // BGRA
    }
    void Seed(int x, int y)
    {
        int p = y * w + x;
        if (!visited[p] && IsWhite(x, y)) { visited[p] = true; stack.Push(p); }
    }

    for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
    for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

    while (stack.Count > 0)
    {
        int p = stack.Pop();
        int x = p % w, y = p / w;
        buf[y * stride + x * 4 + 3] = 0; // alpha -> transparent
        if (x > 0) Seed(x - 1, y);
        if (x < w - 1) Seed(x + 1, y);
        if (y > 0) Seed(x, y - 1);
        if (y < h - 1) Seed(x, y + 1);
    }

    Marshal.Copy(buf, 0, data.Scan0, bytes);
    bmp.UnlockBits(data);
}

// Boxers blue -> white, and the strawberry leaves green -> red (so the berries become
// solid red heart-ish blobs). Color-targeted so it only touches those regions.
static void RecolorBoxers(Bitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    byte[] buf = new byte[data.Stride * h];
    Marshal.Copy(data.Scan0, buf, 0, buf.Length);

    for (int i = 0; i < buf.Length; i += 4)
    {
        byte b = buf[i], g = buf[i + 1], r = buf[i + 2], a = buf[i + 3];
        if (a < 8) continue;

        if (b > r + 25 && b > g + 12 && b > 100)        // blue boxer -> white
        {
            buf[i] = 236; buf[i + 1] = 238; buf[i + 2] = 240;
        }
        else if (g > r + 20 && g > b + 20 && g > 80)    // green leaf -> berry red
        {
            buf[i] = 44; buf[i + 1] = 44; buf[i + 2] = 200;
        }
    }

    Marshal.Copy(buf, 0, data.Scan0, buf.Length);
    bmp.UnlockBits(data);
}

static Rectangle ContentBounds(Bitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int stride = data.Stride;
    byte[] buf = new byte[stride * h];
    Marshal.Copy(data.Scan0, buf, 0, buf.Length);
    bmp.UnlockBits(data);

    int minX = w, minY = h, maxX = -1, maxY = -1;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (buf[y * stride + x * 4 + 3] > 8)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

    if (maxX < 0) return new Rectangle(0, 0, w, h);
    return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
}

static Bitmap Crop(Bitmap src, Rectangle r)
{
    r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
    var dst = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dst);
    g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
    return dst;
}

static Bitmap FitSquare(Bitmap src, int size, bool fill = false)
{
    var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dst);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    if (fill)
    {
        // Stretch to fill the whole square (non-uniform) — fattens a tall figure.
        g.DrawImage(src, 0, 0, size, size);
    }
    else
    {
        float scale = Math.Min((float)size / src.Width, (float)size / src.Height);
        int dw = (int)(src.Width * scale), dh = (int)(src.Height * scale);
        g.DrawImage(src, (size - dw) / 2, (size - dh) / 2, dw, dh);
    }
    return dst;
}

static void SaveIco(string path, Bitmap src, int[] sizes)
{
    var pngs = sizes.Select(s =>
    {
        using var sq = FitSquare(src, s);
        using var ms = new MemoryStream();
        sq.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }).ToArray();

    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    bw.Write((short)0);            // reserved
    bw.Write((short)1);            // type = icon
    bw.Write((short)sizes.Length); // count

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int s = sizes[i];
        bw.Write((byte)(s >= 256 ? 0 : s)); // width
        bw.Write((byte)(s >= 256 ? 0 : s)); // height
        bw.Write((byte)0);                  // palette
        bw.Write((byte)0);                  // reserved
        bw.Write((short)1);                 // planes
        bw.Write((short)32);                // bpp
        bw.Write(pngs[i].Length);           // bytes in resource
        bw.Write(offset);                   // offset
        offset += pngs[i].Length;
    }
    foreach (var png in pngs) bw.Write(png);
}
