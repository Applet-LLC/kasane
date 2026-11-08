using System.Runtime.InteropServices;

namespace Kasane.Interop;

/// <summary>
/// クリップボードに文字を書く（Win32 の OpenClipboard / SetClipboardData。2026-11-08）。
/// WPF の Clipboard.SetText は OLE を通り、書いたあとの確定（OleFlushClipboard）で、ほかのプロセス（クリップボードの履歴など）が
/// クリップボードを開いていると CLIPBRD_E_CANT_OPEN を投げる。確定には試し直しが無く、中身は入っているのに失敗と出た
/// （割り当ての一覧の実機確認 T7。この PC では毎回）。ここでは、開けるまで少し待って試し直し、CF_UNICODETEXT を直接置く。
/// トレイの <c>ActionRunner.SetClipboardText</c> と同じやり方。
/// </summary>
public static class ClipboardText
{
    /// <summary>書けたら null、書けなければ理由。</summary>
    public static string? Set(string text, IntPtr owner = default)
    {
        bool opened = false;
        for (int i = 0; i < 20 && !opened; i++) // 最大でおよそ 1 秒
        {
            opened = OpenClipboard(owner);
            if (!opened) Thread.Sleep(50);
        }
        if (!opened) return Model.Loc.T("ほかのアプリがクリップボードを使っています。少し待ってから、もう一度押してください。",
                                        "Another app is using the clipboard. Wait a moment and try again.");
        try
        {
            if (!EmptyClipboard()) return Error("EmptyClipboard");
            int bytes = (text.Length + 1) * 2;
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return Error("GlobalAlloc");
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return Error("GlobalLock"); }
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return Error("SetClipboardData"); }
            return null; // 渡した領域は、クリップボードのものになる
        }
        finally { CloseClipboard(); }
    }

    private static string Error(string api) => $"{api} (Win32 {Marshal.GetLastWin32Error()})";

    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr mem);
}
