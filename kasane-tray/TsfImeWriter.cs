using System.Runtime.InteropServices;

namespace Kasane.Tray;

/// <summary>
/// TSF のグローバルコンパートメント（<c>GUID_COMPARTMENT_KEYBOARD_OPENCLOSE</c>）に、IME の ON/OFF を書く（実験的）。
/// 別プロセスのトレイからは、この値が空で読めない（<c>docs/ARCHITECTURE.md</c>「TSF で IME の状態を読めるかの調査」）。書いた値が
/// 前面のアプリの IME に効くかは、IME とアプリによる。呼び出し側は、書いたあとに IMM32 で読み直して確かめること。
/// COM は STA のスレッドで使うので、毎回 STA の短いスレッドで書く（ThreadMgr の作成 → Activate → SetValue → Deactivate）。
/// COM のインターフェースの定義は <see cref="TsfImeProbe"/> のものを使う。
/// </summary>
internal static class TsfImeWriter
{
    private const int TimeoutMs = 1500;

    /// <summary>書けたら true。書けなかったときは、理由を <paramref name="error"/> に入れる。</summary>
    public static bool TrySetOpen(bool open, out string error)
    {
        string? failure = null;
        var thread = new Thread(() => failure = Write(open)) { IsBackground = true, Name = "kasane-tsf-write" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeoutMs)) { error = Kasane.Model.Loc.T("TSF の書き込みが時間内に終わりませんでした", "The TSF write did not finish in time"); return false; }
        error = failure ?? "";
        return failure == null;
    }

    private static string? Write(bool open)
    {
        TsfImeProbe.ITfThreadMgr? threadMgr = null;
        bool activated = false;
        try
        {
            var type = Type.GetTypeFromCLSID(TsfImeProbe.ClsidThreadMgr);
            if (type == null) return Kasane.Model.Loc.T("TSF の ThreadMgr が見つかりません", "The TSF ThreadMgr was not found");
            threadMgr = (TsfImeProbe.ITfThreadMgr)Activator.CreateInstance(type)!;

            int hr = threadMgr.Activate(out uint clientId);
            if (hr != 0) return $"ITfThreadMgr.Activate hr=0x{hr:X8}";
            activated = true;

            hr = threadMgr.GetGlobalCompartment(out var manager);
            if (hr != 0 || manager == null) return $"GetGlobalCompartment hr=0x{hr:X8}";

            var guid = TsfImeProbe.KeyboardOpenClose;
            hr = manager.GetCompartment(ref guid, out var compartment);
            if (hr != 0 || compartment == null) return $"GetCompartment hr=0x{hr:X8}";

            object value = open ? 1 : 0;
            hr = compartment.SetValue(clientId, ref value);
            return hr == 0 ? null : $"ITfCompartment.SetValue hr=0x{hr:X8}";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return ex.Message;
        }
        finally
        {
            if (activated)
            {
                try { threadMgr?.Deactivate(); }
                catch (COMException) { /* 書き込みは済んでいる。終了時の失敗は無視 */ }
            }
        }
    }
}
