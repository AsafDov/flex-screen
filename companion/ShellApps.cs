using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FlexScreen.Companion;

/// <summary>
/// The Start menu's app list (shell:AppsFolder), desktop and Store apps alike, by the name
/// the Start menu shows, with the id that starts each one: an AppUserModelID
/// ("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Microsoft.VisualStudioCode") or an exe
/// under a known folder ("{6D809377-…}\Prusa3D\PrusaSlicer\prusa-slicer.exe"). Also the
/// shell's icon for an app, a file or a folder. Shell calls belong on an STA thread:
/// <see cref="OnSta{T}"/> runs one there.
/// </summary>
static class ShellApps
{
    public record App(string Name, string Id);

    /// <summary>Every app in the Start menu's list; empty if the shell won't say.</summary>
    public static List<App> List()
    {
        var apps = new List<App>();
        if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, typeof(IShellItem).GUID, out var folderObject) != 0 || folderObject is not IShellItem folder) return apps;
        try
        {
            if (folder.BindToHandler(IntPtr.Zero, BhidEnumItems, typeof(IEnumShellItems).GUID, out var enumPointer) != 0 || enumPointer == IntPtr.Zero) return apps;
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPointer);
            Marshal.Release(enumPointer);
            try
            {
                while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1 && item != null)
                {
                    try
                    {
                        var name = DisplayName(item, SigdnNormalDisplay);
                        var id = DisplayName(item, SigdnParentRelativeParsing);
                        if (name.Length > 0 && id.Length > 0) apps.Add(new App(name, id));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
        return apps;
    }

    /// <summary>The app called <paramref name="name"/>: the same name, else one starting with it, else one containing it.</summary>
    public static App? Find(IReadOnlyList<App> apps, string name)
    {
        if (name.Length == 0) return null;
        return apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? apps.Where(a => a.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).MinBy(a => a.Name.Length)
            ?? apps.Where(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).MinBy(a => a.Name.Length);
    }

    /// <summary>
    /// The shell's picture of a Start menu app ("shell:AppsFolder\" + its id), a file or a folder,
    /// <paramref name="size"/> pixels square; with <paramref name="iconOnly"/> false, an image
    /// file gives its own picture. Null if there's none.
    /// </summary>
    public static Bitmap? Image(string parsingName, int size, bool iconOnly = true)
    {
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var o) != 0 || o is not IShellItemImageFactory factory) return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, iconOnly ? SiigbfIconOnly : SiigbfResizeToFit, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try
            {
                return FromHbitmap(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Runs <paramref name="work"/> on a new STA thread and waits for it.</summary>
    public static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                failure = e;
            }
        }) { IsBackground = true, Name = "shell" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
        return result;
    }

    /// <summary>A shell bitmap (32-bit, usually premultiplied alpha) as an ordinary ARGB bitmap.</summary>
    static Bitmap? FromHbitmap(IntPtr hbitmap)
    {
        var info = new NativeBitmap();
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), ref info) == 0 || info.Width <= 0 || info.Height == 0) return null;
        int w = info.Width, h = Math.Abs(info.Height);
        var header = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };  // top-down
        var bits = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)h, bits, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        bool anyAlpha = false, premultiplied = true;
        for (var i = 0; i < w * h; i++)
        {
            var a = bits[i * 4 + 3];
            if (a != 0) anyAlpha = true;
            if (bits[i * 4] > a || bits[i * 4 + 1] > a || bits[i * 4 + 2] > a) premultiplied = false;
        }
        for (var i = 0; i < w * h; i++)
        {
            if (!anyAlpha)
            {
                bits[i * 4 + 3] = 255;  // an icon with no alpha channel at all is opaque
            }
            else if (premultiplied && bits[i * 4 + 3] is > 0 and < 255 and var a)
            {
                for (var c = 0; c < 3; c++) bits[i * 4 + c] = (byte)Math.Min(255, bits[i * 4 + c] * 255 / a);
            }
        }
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++) Marshal.Copy(bits, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    static string DisplayName(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out var text) != 0 || text == IntPtr.Zero) return "";
        try
        {
            return Marshal.PtrToStringUni(text) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    const uint SigdnNormalDisplay = 0;
    const uint SigdnParentRelativeParsing = 0x80018001;
    const uint SiigbfResizeToFit = 0x0;
    const uint SiigbfIconOnly = 0x4;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid handler, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems copy);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize
    {
        public int Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    // BITMAPINFOHEADER, with room for the three color masks GetDIBits may write after it.
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Mask0, Mask1, Mask2;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr handle, int size, ref NativeBitmap bitmap);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
