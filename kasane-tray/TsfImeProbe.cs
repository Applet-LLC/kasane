using System.Runtime.InteropServices;
using System.Text;

namespace Kasane.Tray;

/// <summary>
/// TSF（Text Services Framework）で、IME の ON/OFF・変換モードを読めるかの調査用（診断コマンド kasane-tray.exe --probe-tsf。2026-10-19）。
/// 本体の動きには使っていない。目的は、別プロセスの前面アプリの値が、TSF のコンパートメントから読めるか、IMM32（<see cref="Win32ImeSource"/>）と
/// どれだけ違うかを、アプリ別に測ること。
/// 読むもの（GUID と COM のメソッドの並びは、Windows SDK の msctf.h で確認した値）:
///  - グローバルコンパートメント（ITfThreadMgr::GetGlobalCompartment）の KEYBOARD_OPENCLOSE と KEYBOARD_INPUTMODE_CONVERSION。
///  - 自分のスレッドのコンパートメント（ITfThreadMgr を ITfCompartmentMgr として。対照。前面アプリの値は反映しないはず）。
/// COM は STA のスレッドで使う（Main が STAThread）。
/// </summary>
internal static class TsfImeProbe
{
    internal static readonly Guid ClsidThreadMgr = new("529A9E6B-6587-4F23-AB9E-9C7D683E3C50");
    internal static readonly Guid KeyboardOpenClose = new("58273AAD-01BB-4164-95C6-755BA0B5162D");
    private static readonly Guid KeyboardInputModeConversion = new("CCF05DD8-4A87-11D7-A6E2-00065B84435C");

    [ComImport, Guid("AA80E801-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITfThreadMgr
    {
        [PreserveSig] int Activate(out uint clientId);
        [PreserveSig] int Deactivate();
        [PreserveSig] int CreateDocumentMgr(out IntPtr ppdim);
        [PreserveSig] int EnumDocumentMgrs(out IntPtr ppEnum);
        [PreserveSig] int GetFocus(out IntPtr ppdimFocus);
        [PreserveSig] int SetFocus(IntPtr pdimFocus);
        [PreserveSig] int AssociateFocus(IntPtr hwnd, IntPtr pdimNew, out IntPtr ppdimPrev);
        [PreserveSig] int IsThreadFocus([MarshalAs(UnmanagedType.Bool)] out bool pfThreadFocus);
        [PreserveSig] int GetFunctionProvider(ref Guid clsid, out IntPtr ppFuncProv);
        [PreserveSig] int EnumFunctionProviders(out IntPtr ppEnum);
        [PreserveSig] int GetGlobalCompartment([MarshalAs(UnmanagedType.Interface)] out ITfCompartmentMgr ppCompMgr);
    }

    [ComImport, Guid("7DCF57AC-18AD-438B-824D-979BFFB74B7C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITfCompartmentMgr
    {
        [PreserveSig] int GetCompartment(ref Guid rguid, [MarshalAs(UnmanagedType.Interface)] out ITfCompartment ppcomp);
        [PreserveSig] int ClearCompartment(uint tid, ref Guid rguid);
        [PreserveSig] int EnumCompartments(out IntPtr ppEnum);
    }

    [ComImport, Guid("BB08F7A9-607A-4384-8623-056892B64371"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITfCompartment
    {
        [PreserveSig] int SetValue(uint tid, [MarshalAs(UnmanagedType.Struct)] ref object pvarValue);
        [PreserveSig] int GetValue([MarshalAs(UnmanagedType.Struct)] out object pvarValue);
    }

    /// <summary>
    /// 診断を <paramref name="count"/> 回、<paramref name="intervalSeconds"/> 秒ごとに、<paramref name="path"/> に追記する。最初に3秒待つ
    /// （その間に、調べたいウィンドウを前面にする。以降も、アプリや IME の状態を切り替えながら測る）。
    /// </summary>
    public static void Run(string path, int count, int intervalSeconds)
    {
        var imm = new Win32ImeSource();
        ITfThreadMgr? threadMgr = null;
        string initNote;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidThreadMgr) ?? throw new InvalidOperationException("TSF の ThreadMgr が見つかりません");
            threadMgr = (ITfThreadMgr)Activator.CreateInstance(type)!;
            int hr = threadMgr.Activate(out uint clientId);
            initNote = $"ThreadMgr 作成 OK、Activate hr=0x{hr:X8} clientId={clientId}";
        }
        catch (Exception ex)
        {
            initNote = "ThreadMgr の作成に失敗: " + ex.Message;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} --probe-tsf({count} 回、{intervalSeconds} 秒おき)。{initNote}");
        File.AppendAllText(path, sb.ToString());

        Thread.Sleep(3000);
        for (int i = 0; i < count; i++)
        {
            string line = SampleLine(imm, threadMgr);
            File.AppendAllText(path, line + Environment.NewLine);
            if (i + 1 < count) Thread.Sleep(Math.Max(1, intervalSeconds) * 1000);
        }

        try { threadMgr?.Deactivate(); }
        catch (Exception) { /* 調査用。終了時の失敗は無視 */ }
    }

    // 1回分: 時刻、IMM32 の結果（既存の診断の文）、TSF（グローバル・自分のスレッド）の値。
    private static string SampleLine(Win32ImeSource imm, ITfThreadMgr? threadMgr)
    {
        var immState = imm.ReadWithDetail(out string immDetail);
        string imm32 = immState.Known ? $"IMM32: Open={immState.Open} Mode=0x{immState.ConversionMode:X}" : "IMM32: 取れない";

        string global = "TSF global: (ThreadMgr なし)";
        string own = "TSF own: (ThreadMgr なし)";
        if (threadMgr != null)
        {
            global = "TSF global: " + ReadCompartments(() =>
            {
                int hr = threadMgr.GetGlobalCompartment(out var mgr);
                return hr == 0 ? mgr : null;
            });
            own = "TSF own: " + ReadCompartments(() => threadMgr as ITfCompartmentMgr);
        }

        return $"{DateTime.Now:HH:mm:ss}  {imm32}  |  {global}  |  {own}  |  {immDetail}";
    }

    private static string ReadCompartments(Func<ITfCompartmentMgr?> manager)
    {
        try
        {
            var mgr = manager();
            if (mgr == null) return "マネージャーを取れない";
            return $"Open={ReadValue(mgr, KeyboardOpenClose)} Mode={ReadValue(mgr, KeyboardInputModeConversion, hex: true)}";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return "例外: " + ex.Message;
        }
    }

    private static string ReadValue(ITfCompartmentMgr mgr, Guid guid, bool hex = false)
    {
        int hr = mgr.GetCompartment(ref guid, out var comp);
        if (hr != 0 || comp == null) return $"GetCompartment hr=0x{hr:X8}";
        hr = comp.GetValue(out object value);
        if (hr != 0) return $"GetValue hr=0x{hr:X8}"; // S_FALSE(1) は、値が未設定
        return value switch
        {
            int i => hex ? $"0x{i:X}" : i.ToString(),
            null => "(空)",
            _ => $"({value.GetType().Name}) {value}",
        };
    }
}
