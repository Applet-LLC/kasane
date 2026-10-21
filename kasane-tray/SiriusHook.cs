using System.Runtime.InteropServices;

namespace Kasane.Tray;

/// <summary>
/// 天狼（sirius）のフック用 DLL（sirius_hook_for_kasane_x64.dll。ソースは sirius\ フォルダ）が、共有メモリに書く構造体
/// （CcommonValues）のフィールドの位置（バイト）。C# 側で構造体を再定義すると、ネストした Win32 構造体（LOGFONT など）の
/// 大きさでずれるので、位置は DLL の <c>SiriusGetLayout</c>（offsetof）から得る。
/// </summary>
internal readonly record struct SiriusLayout(
    int Size, int HWndFocus, int ImeStatus, int Conversion, int SupportTsfOpenClose, int SupportTsfConversion, int IsHooked)
{
    /// <summary>この C# が分かる SiriusLayout の版（DLL の SIRIUS_LAYOUT_VERSION と合わせる）。</summary>
    public const uint SupportedVersion = 1;

    /// <summary>DLL が返す、フィールドの位置の表（C++ の SiriusLayout と同じ並び）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public uint CbSize, Version, HWndFocus, HWndFront, Conversion, ImeStatus, SupportTsf, SupportTsfOpenClose, SupportTsfConversion, ProcessId, IsHooked;
    }

    /// <summary>版が違う・位置が構造体の外にはみ出すときは null（読まない。TSF は使えないものとして、IMM32 だけで動く）。</summary>
    public static SiriusLayout? From(in Native n)
    {
        if (n.Version != SupportedVersion || n.CbSize == 0 || n.CbSize > int.MaxValue) return null;
        long size = n.CbSize;
        // 読む場所が、構造体の中に収まっているか（HWND は IntPtr.Size、DWORD/BOOL は 4、bool は 1 バイト）。
        if (!Fits(n.HWndFocus, IntPtr.Size, size) || !Fits(n.ImeStatus, 4, size) || !Fits(n.Conversion, 4, size)
            || !Fits(n.SupportTsfOpenClose, 4, size) || !Fits(n.SupportTsfConversion, 4, size) || !Fits(n.IsHooked, 1, size))
            return null;
        return new SiriusLayout((int)n.CbSize, (int)n.HWndFocus, (int)n.ImeStatus, (int)n.Conversion,
            (int)n.SupportTsfOpenClose, (int)n.SupportTsfConversion, (int)n.IsHooked);
    }

    private static bool Fits(uint offset, int length, long size) => offset + (long)length <= size;
}

/// <summary>共有メモリの、ある瞬間の値。</summary>
internal readonly record struct SiriusSnapshot(
    bool Hooked, long HWndFocus, bool SupportOpenClose, bool SupportConversion, uint ImeStatus, uint Conversion);

/// <summary>TSF（共有メモリ）から IME の状態を採れなかった理由（ログ用）。</summary>
public enum TsfReason
{
    Ok = 0,
    NotAttached,          // フックを入れていない
    NoTarget,             // 前面が、見ないウィンドウ（トレイ自身・シェル）
    NotHooked,            // DLL がフックの設置済みと言っていない
    OpenCloseUnsupported, // 共有メモリの値が、TSF の OpenClose を読めたものではない（その窓に DLL が入っていない・TSF を使わない）
    FocusNotForeground,   // 共有メモリの値は、前面ではない別のウィンドウのもの
    ConversionUnsupported // ON だが、変換モードが TSF で読めていない
}

internal static class SiriusDecoder
{
    /// <summary>
    /// 共有メモリの値から IME の状態を決める（純粋な判断。テストする）。
    ///  - フックが有効で、OpenClose が TSF で読めていて、その値が<b>前面のアプリのもの</b>（<paramref name="focusInForeground"/>）のときだけ採る。
    ///    共有メモリはシステム全体で1つで、最後にフォーカスを受けたウィンドウの値なので、前面のウィンドウのものかを確かめる。
    ///  - OFF なら、変換モードは聞かない（IMM32 と同じ）。ON で変換モードが TSF で読めないなら、かな入力か分からないので不明。
    /// </summary>
    public static ImeState Decide(in SiriusSnapshot s, bool focusInForeground) => Decide(s, focusInForeground, out _);

    /// <summary>上と同じ判断。採れなかったとき、その理由も返す。</summary>
    public static ImeState Decide(in SiriusSnapshot s, bool focusInForeground, out TsfReason reason)
    {
        reason = TsfReason.Ok;
        if (!s.Hooked) { reason = TsfReason.NotHooked; return ImeState.Unknown; }
        if (!s.SupportOpenClose) { reason = TsfReason.OpenCloseUnsupported; return ImeState.Unknown; }
        if (!focusInForeground) { reason = TsfReason.FocusNotForeground; return ImeState.Unknown; }
        if (s.ImeStatus == 0) return new ImeState(true, false, 0);
        if (!s.SupportConversion) { reason = TsfReason.ConversionUnsupported; return ImeState.Unknown; }
        return new ImeState(true, true, s.Conversion);
    }
}

/// <summary>
/// 天狼のフック用 DLL を読み込み、フックを入れて、共有メモリ（IME の状態）を得る。フックは、同じデスクトップの全 GUI プロセスに
/// DLL を読み込ませ、そのスレッドで TSF のコンパートメント（ON/OFF・変換モード）を監視して、共有メモリに書く。
/// DLL が無い・読めない・版が違うときは入れず（<see cref="TryInstall"/> が null）、IMM32 だけで動く。
/// </summary>
internal sealed class SiriusHook : IDisposable
{
    public const string DllName = "sirius_hook_for_kasane_x64.dll";

    // 登録ウィンドウメッセージ。nodoka の WM_SIRIUS_CONTROL とは別の名前にして、のどかと同じPCでも取り合わない。
    private const string ControlMessageName = "KASANE_SIRIUS_CONTROL";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SetupHookFn(uint messageId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseHookFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetLayoutFn(out SiriusLayout.Native layout);

    private readonly IntPtr _module;
    private readonly ReleaseHookFn _release;
    private bool _disposed;

    private SiriusHook(IntPtr module, ReleaseHookFn release, IntPtr memory, SiriusLayout layout)
    {
        _module = module;
        _release = release;
        Memory = memory;
        Layout = layout;
    }

    /// <summary>共有メモリの先頭（CcommonValues）。<see cref="Dispose"/> のあとは使えない（読む側が止めてから Dispose する）。</summary>
    public IntPtr Memory { get; }

    public SiriusLayout Layout { get; }

    /// <summary>トレイの exe と同じフォルダの DLL。uiAccess のトレイは、署名して Program Files に置いたものだけを読む。</summary>
    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, DllName);

    /// <summary>フックを入れる。入れられなければ null（理由は <paramref name="error"/>）。</summary>
    public static SiriusHook? TryInstall(out string error, string? dllPath = null)
    {
        dllPath ??= DefaultPath;
        error = "";
        if (!File.Exists(dllPath)) { error = $"{DllName} がありません"; return null; }

        IntPtr module = IntPtr.Zero;
        try
        {
            module = NativeLibrary.Load(dllPath);
            var setup = Marshal.GetDelegateForFunctionPointer<SetupHookFn>(NativeLibrary.GetExport(module, "SiriusSetupHook"));
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseHookFn>(NativeLibrary.GetExport(module, "SiriusReleaseHook"));
            var getLayout = Marshal.GetDelegateForFunctionPointer<GetLayoutFn>(NativeLibrary.GetExport(module, "SiriusGetLayout"));

            getLayout(out var native);
            if (SiriusLayout.From(native) is not { } layout)
            {
                error = $"{DllName} の共有メモリの形が分かりません(版 {native.Version}、大きさ {native.CbSize})";
                NativeLibrary.Free(module);
                return null;
            }

            uint messageId = RegisterWindowMessage(ControlMessageName);
            IntPtr memory = setup(messageId);
            if (memory == IntPtr.Zero)
            {
                error = "SiriusSetupHook が失敗しました(フックを入れられません)";
                NativeLibrary.Free(module);
                return null;
            }
            return new SiriusHook(module, release, memory, layout);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                                       or ArgumentException or MarshalDirectiveException or InvalidOperationException)
        {
            error = $"{DllName} を読み込めません: {ex.Message}";
            if (module != IntPtr.Zero) NativeLibrary.Free(module);
            return null;
        }
    }

    /// <summary>その瞬間の共有メモリの値を読む。</summary>
    public SiriusSnapshot Read()
    {
        var l = Layout;
        return new SiriusSnapshot(
            Hooked: Marshal.ReadByte(Memory, l.IsHooked) != 0,
            HWndFocus: Marshal.ReadIntPtr(Memory, l.HWndFocus).ToInt64(),
            SupportOpenClose: Marshal.ReadInt32(Memory, l.SupportTsfOpenClose) != 0,
            SupportConversion: Marshal.ReadInt32(Memory, l.SupportTsfConversion) != 0,
            ImeStatus: (uint)Marshal.ReadInt32(Memory, l.ImeStatus),
            Conversion: (uint)Marshal.ReadInt32(Memory, l.Conversion));
    }

    /// <summary>フックを外す（各プロセスの DLL は、WM_NULL の同報で外れる）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _release(); }
        catch (Exception ex) when (ex is SEHException or InvalidOperationException) { /* 終了時の失敗は無視（DLL は各プロセスから、いずれ外れる） */ }
        NativeLibrary.Free(_module);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
