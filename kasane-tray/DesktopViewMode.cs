namespace Kasane.Tray;

/// <summary>
/// デスクトップのアイコンの表示方法（のどかの def option DesktopListView と同じ番号。リストビューの表示方法の並びと同じ）。
/// 0 = 通常表示（アイコン）、1 = 詳細表示、2 = 小さなアイコン表示、3 = リスト表示、4 = タイル表示。
/// </summary>
public static class DesktopViewMode
{
    public const int Min = 0;
    public const int Max = 4;

    // IFolderView の FOLDERVIEWMODE（shobjidl.h）。
    private const int FVM_ICON = 1;
    private const int FVM_SMALLICON = 2;
    private const int FVM_LIST = 3;
    private const int FVM_DETAILS = 4;
    private const int FVM_TILE = 6;

    /// <summary>設定の番号を FOLDERVIEWMODE に直す。範囲外は null（何もしない）。</summary>
    public static int? ToFolderViewMode(int? mode) => mode switch
    {
        0 => FVM_ICON,
        1 => FVM_DETAILS,
        2 => FVM_SMALLICON,
        3 => FVM_LIST,
        4 => FVM_TILE,
        _ => null,
    };
}
