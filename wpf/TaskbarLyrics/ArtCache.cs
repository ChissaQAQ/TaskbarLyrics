// 听歌统计的配图缓存：专辑封面、歌手头像，按 StatsReport 给的键落盘。
//
// 图只在播放时由 ArtCollector 顺带存下（SMTC 给的封面字节，或歌词接口匹配到的封面/头像地址），
// 统计页、年报只从这里读，打开统计页本身不联网。
// 存的是缩到 300px 以内、重新编码过的 JPEG：原图有的上 MB，几百张就是几百 MB；
// 重新编码也顺带把「下回来的其实不是图」的情况挡在门外。
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics;

internal static class ArtCache
{
    private const int MaxPx = 300;
    private const int NoneTtlDays = 7;  // 查过没有的，过一阵再查：曲库可能后来补了图
    private const int MaxFiles = 1500;  // 只数图片，约 40~50 MB
    private const int EvictBatch = 100;

    /// <summary>自测把缓存指到临时目录，不碰真实数据。</summary>
    internal static string? DirOverride;

    /// <summary>跟歌词缓存同级，跟着配置文件走。</summary>
    private static string Dir => DirOverride ??
        Path.Combine(Path.GetDirectoryName(AppConfig.ConfigPath) ?? AppContext.BaseDirectory, "art-cache");

    private static string PathFor(string key, string ext)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(Dir, Convert.ToHexString(hash, 0, 16) + ext);
    }

    public static bool Has(string key) => File.Exists(PathFor(key, ".jpg"));

    /// <summary>查过、确实没有，且还没过重查期限。</summary>
    public static bool IsNone(string key)
    {
        try
        {
            var f = new FileInfo(PathFor(key, ".none"));
            return f.Exists && DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromDays(NoneTtlDays);
        }
        catch
        {
            return false;
        }
    }

    public static void MarkNone(string key)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(PathFor(key, ".none"), []);
        }
        catch
        {
            // 写不进去下次再查一遍，无伤大雅
        }
    }

    /// <summary>只认 JPEG、PNG 的文件头：不信服务器报的 Content-Type，出错页、JSON 一概不收。</summary>
    internal static bool LooksLikeImage(byte[] b)
        => b.Length > 8 && (b[0] == 0xFF && b[1] == 0xD8 || b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47);

    /// <summary>存一张图：解得开才存，缩到 300px 以内重新编码成 JPEG。返回是否存下了。</summary>
    public static bool Save(string key, byte[] bytes)
    {
        if (!LooksLikeImage(bytes)) return false;
        try
        {
            var src = Decode(bytes, 0);
            if (src == null) return false;
            BitmapSource img = src;
            var scale = Math.Min(1.0, (double)MaxPx / Math.Max(src.PixelWidth, src.PixelHeight));
            if (scale < 1) img = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            // 带透明通道的 PNG 直接编 JPEG 会把透明处变黑，先铺白底
            img = OnWhite(img);
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(img));

            Directory.CreateDirectory(Dir);
            var path = PathFor(key, ".jpg");
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) enc.Save(fs);
            File.Move(tmp, path, overwrite: true);
            Evict();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("art", ex);
            return false;
        }
    }

    /// <summary>读一张图，按 px 宽解码（界面上多大就解多大，省内存），冻结后可跨线程用。
    /// 读到了顺手把修改时间改成现在，超量时先删的是最久没人看的（见 Evict）。
    /// 坏文件删掉、返回 null，下次播放时会重新存。</summary>
    public static BitmapSource? Load(string key, int px)
    {
        var path = PathFor(key, ".jpg");
        try
        {
            if (!File.Exists(path)) return null;
            var img = Decode(File.ReadAllBytes(path), px);
            if (img != null)
            {
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* 改不了只是淘汰顺序不准 */ }
                return img;
            }
        }
        catch
        {
            // 落到下面删文件
        }
        try { File.Delete(path); } catch { /* 删不掉就算了 */ }
        return null;
    }

    /// <summary>逐像素按 alpha 混到白底上。不用 RenderTargetBitmap：收集器跑在后台的 MTA 线程上。</summary>
    private static BitmapSource OnWhite(BitmapSource img)
    {
        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var rgb = new byte[w * h * 3];
        for (int i = 0, j = 0; i < px.Length; i += 4, j += 3)
        {
            var a = px[i + 3];
            for (var c = 0; c < 3; c++) rgb[j + c] = (byte)((px[i + c] * a + 255 * (255 - a) + 127) / 255);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
        result.Freeze();
        return result;
    }

    private static BitmapImage? Decode(byte[] bytes, int px)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (px > 0) img.DecodePixelWidth = px;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img.PixelWidth > 0 && img.PixelHeight > 0 ? img : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>顺手清掉过期的「没有」记录和写了一半的临时文件；图片超量时删掉最久没用过的一批
    /// （存下、或统计页读到时会更新修改时间）。</summary>
    private static void Evict()
    {
        try
        {
            var now = DateTime.UtcNow;
            var jpgs = new List<FileInfo>();
            foreach (var f in new DirectoryInfo(Dir).GetFiles())
            {
                var age = now - f.LastWriteTimeUtc;
                if (f.Extension == ".none" && age >= TimeSpan.FromDays(NoneTtlDays) || f.Extension == ".tmp" && age >= TimeSpan.FromHours(1))
                    try { f.Delete(); } catch { /* 下次再说 */ }
                else if (f.Extension == ".jpg")
                    jpgs.Add(f);
            }
            if (jpgs.Count <= MaxFiles) return;
            foreach (var fi in jpgs.OrderBy(f => f.LastWriteTimeUtc).Take(jpgs.Count - MaxFiles + EvictBatch))
                fi.Delete();
        }
        catch
        {
            // 删不掉就下次再说
        }
    }

    /// <summary>清空全部配图（清空听歌记录时一起清）。</summary>
    public static void Clear()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in new DirectoryInfo(Dir).GetFiles())
            {
                try { f.Delete(); }
                catch { /* 单个文件占用中，跳过 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Error("art", ex);
        }
    }
}
