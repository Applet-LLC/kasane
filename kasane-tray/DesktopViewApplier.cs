using System.Runtime.InteropServices;

namespace Kasane.Tray;

/// <summary>
/// トレイの起動時に、デスクトップのアイコンの表示方法を tray.json の DesktopViewMode に合わせる（のどかの DesktopListView の代わり）。
/// やり方は Raymond Chen の「Programmatically setting the view mode of the desktop」と同じ:
/// IShellWindows::FindWindowSW でデスクトップを探し、IShellBrowser → IShellView → IFolderView::SetCurrentViewMode を呼ぶ。
/// ログオン直後はエクスプローラーのデスクトップがまだ無いことがあるので、できるまで1秒ごとに最大60秒ほど試す。
/// 設定が無い（null）ときは何もしない。キーのリマップとは無関係。
/// </summary>
internal sealed class DesktopViewApplier : IDisposable
{
    private const int IntervalMs = 1000;
    private const int MaxAttempts = 60;

    private readonly int _folderViewMode;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = IntervalMs };
    private int _attempts;

    private DesktopViewApplier(int folderViewMode)
    {
        _folderViewMode = folderViewMode;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>設定が有効なときだけ作って開始する。無効（null・範囲外）なら null。</summary>
    public static DesktopViewApplier? StartIfConfigured(int? mode)
    {
        if (DesktopViewMode.ToFolderViewMode(mode) is not { } fvm) return null;
        var applier = new DesktopViewApplier(fvm);
        applier._timer.Start();
        return applier;
    }

    private void Tick()
    {
        _attempts++;
        bool done = false;
        try
        {
            done = TryApply(_folderViewMode);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            if (_attempts >= MaxAttempts) TrayLog.Write("desktop-view: " + ex.Message);
        }
        if (done || _attempts >= MaxAttempts) _timer.Stop();
    }

    private static bool TryApply(int folderViewMode)
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellWindows)!)!;
        object loc = CSIDL_DESKTOP; // VT_I4
        object? empty = null;       // VT_EMPTY
        object? disp = shellWindows.FindWindowSW(ref loc, ref empty, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
        if (disp is not IShellServiceProvider sp) return false;

        Guid sid = SidSTopLevelBrowser;
        Guid iid = typeof(IShellBrowser).GUID;
        sp.QueryService(ref sid, ref iid, out IntPtr pBrowser);
        if (pBrowser == IntPtr.Zero) return false;
        try
        {
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
            var view = browser.QueryActiveShellView();
            if (view is not IFolderView folderView) return false;
            folderView.SetCurrentViewMode(folderViewMode);
            return true;
        }
        finally { Marshal.Release(pBrowser); }
    }

    public void Dispose() => _timer.Dispose();

    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In, MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.Interface)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int pCookie);
        void RegisterPending(int threadId, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarloc, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot, int swClass, out int pCookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc, [In, MarshalAs(UnmanagedType.Struct)] ref object? pvarLocRoot, int swClass, out int pHWND, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    // IShellBrowser の QueryActiveShellView までの並び。呼ぶのは QueryActiveShellView だけで、途中のものは並びを合わせるための穴埋め。
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [return: MarshalAs(UnmanagedType.Interface)] object QueryActiveShellView();
    }

    // IFolderView は、使う SetCurrentViewMode までだけ宣言する。
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(out int pViewMode);
        void SetCurrentViewMode(int viewMode);
    }
}
