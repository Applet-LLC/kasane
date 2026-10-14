using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>デスクトップのアイコンの表示方法（のどかの DesktopListView の番号 → FOLDERVIEWMODE）。</summary>
public class DesktopViewModeTests
{
    [Theory]
    [InlineData(0, 1)] // 通常 → FVM_ICON
    [InlineData(1, 4)] // 詳細 → FVM_DETAILS
    [InlineData(2, 2)] // 小さなアイコン → FVM_SMALLICON
    [InlineData(3, 3)] // リスト → FVM_LIST
    [InlineData(4, 6)] // タイル → FVM_TILE
    public void MapsToFolderViewMode(int mode, int expected) =>
        Assert.Equal(expected, DesktopViewMode.ToFolderViewMode(mode));

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(5)]
    public void OutOfRangeDoesNothing(int? mode) =>
        Assert.Null(DesktopViewMode.ToFolderViewMode(mode));
}
