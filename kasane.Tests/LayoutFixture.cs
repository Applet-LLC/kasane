using Kasane.Layout;

namespace Kasane.Tests;

/// <summary>Shared 17-key (10key) layout, loaded once from the bundled kle-10key.json fixture
/// (a copy of kasane/Layouts/kle-10key.json).</summary>
public static class LayoutFixture
{
    public static readonly KeyboardLayout TenKey = KeyboardLayout.Load(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "kle-10key.json"));
}
