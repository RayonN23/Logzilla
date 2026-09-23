using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Logzilla.Setup;

/// <summary>
/// Tells Explorer that file associations changed, so the new icon appears without
/// waiting for a sign-out.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellNotifier
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    public static void RefreshFileAssociations() =>
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    // DllImport rather than LibraryImport: the source generator would require
    // AllowUnsafeBlocks for what is a blittable, four-argument void call.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
