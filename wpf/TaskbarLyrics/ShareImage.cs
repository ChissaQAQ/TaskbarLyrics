// 把程序自己的一块界面渲染成 PNG：统计页的「分享」、年度报告的「保存为图片」、自测截图共用。
//
// 只画给定元素自己的视觉树（RenderTargetBitmap），绝不截屏：屏幕上压在窗口前面的东西
// （聊天窗、通知气泡）不会被带进图里。要分享的那块界面也由调用方挑好，
// 不含统计页页脚那行数据目录（路径里有 Windows 用户名）。
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics;

internal static class ShareImage
{
    // 最长边的像素上限：「全部」范围的榜单展开后很长，再按 1.25 倍渲染能有上万像素，
    // 有的聊天软件发不出去，RenderTargetBitmap 也要一次吃下几百 MB
    private const double MaxPixels = 8000;
    // 总像素上限：只限最长边的话，又宽又长的图仍能到上百 MB，渲染、编码、进剪贴板都要卡上一秒
    private const double MaxArea = 24e6;

    /// <summary>root 外围留 pad 的边；给了标题、副标题就在顶上画出来。scale 是像素倍率。
    /// 开了 Mica 时窗口背景是透明的，这里先铺一层主题底色。</summary>
    public static BitmapSource Render(FrameworkElement root, double scale = 1.25, double pad = 24,
        string? title = null, string? subtitle = null)
    {
        root.UpdateLayout();
        double w = root.ActualWidth, h = root.ActualHeight;
        var bg = root.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
        var fg = root.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.Black;
        var fg2 = root.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;

        var titleText = title == null ? null : Text(root, title, 22, FontWeights.SemiBold, fg, w, scale);
        var subText = subtitle == null ? null : Text(root, subtitle, 12, FontWeights.Normal, fg2, w, scale);
        var head = (titleText?.Height ?? 0) + (titleText != null && subText != null ? 4 : 0) + (subText?.Height ?? 0);
        if (head > 0) head += 16;

        double fullW = w + pad * 2, fullH = h + head + pad * 2;
        scale = Math.Min(scale, Math.Min(MaxPixels / Math.Max(fullW, fullH), Math.Sqrt(MaxArea / (fullW * fullH))));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, fullW, fullH));
            var y = pad;
            if (titleText != null)
            {
                dc.DrawText(titleText, new Point(pad, y));
                y += titleText.Height + (subText != null ? 4 : 0);
            }
            if (subText != null) dc.DrawText(subText, new Point(pad, y));
            // VisualBrush 的坐标系连带 root 自身的偏移（它的 Margin），Viewbox 要从那个偏移处取，
            // 否则整张图往右下错开一个 Margin，右边和底部被裁掉
            var off = VisualTreeHelper.GetOffset(root);
            dc.DrawRectangle(new VisualBrush(root)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(off.X, off.Y, w, h),
            }, null, new Rect(pad, pad + head, w, h));
        }
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(fullW * scale), (int)Math.Ceiling(fullH * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>标题用 root 所在窗口的字体，超宽时折行。</summary>
    private static FormattedText Text(FrameworkElement root, string s, double size, FontWeight weight, Brush brush,
        double maxWidth, double pixelsPerDip)
        => new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(root), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
        };

    public static byte[] EncodePng(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>先写临时文件再改名：写一半出错不会留下一张打不开的图把原来那张盖掉。</summary>
    public static void SaveTo(string path, BitmapSource img)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, EncodePng(img));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>弹「另存为」，默认放在「图片」文件夹。返回存到的路径，取消了返回 null；写盘失败照常抛出。</summary>
    public static string? SaveWithDialog(Window owner, BitmapSource img, string fileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = fileName,
            DefaultExt = ".png",
            Filter = "PNG 图片|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(owner) != true) return null;
        SaveTo(dlg.FileName, img);
        return dlg.FileName;
    }

    /// <summary>放进剪贴板，可以直接粘到聊天窗里。剪贴板正被别的程序占着时返回 false。</summary>
    public static bool CopyToClipboard(BitmapSource img)
    {
        try
        {
            Clipboard.SetImage(img);
            return true;
        }
        catch (ExternalException ex)
        {
            Log.Error("share-clipboard", ex);
            return false;
        }
    }
}
