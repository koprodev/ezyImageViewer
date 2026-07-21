using System.ComponentModel;
using System.Runtime.InteropServices;
using static EzyImageViewer.Imaging.Codecs.Isolation.IsolationNativeMethods;

namespace EzyImageViewer.Imaging.Codecs.Isolation;

/// <summary>Owns the exact AppAuthority capability SID array used for profile and process creation.</summary>
internal sealed class AppContainerCapabilitySet : IDisposable
{
    private IntPtr _groupSids;
    private readonly uint _groupSidCount;
    private IntPtr _capabilitySids;
    private readonly uint _capabilitySidCount;
    private IntPtr _attributes;

    private AppContainerCapabilitySet(
        IntPtr groupSids,
        uint groupSidCount,
        IntPtr capabilitySids,
        uint capabilitySidCount,
        IntPtr attributes)
    {
        _groupSids = groupSids;
        _groupSidCount = groupSidCount;
        _capabilitySids = capabilitySids;
        _capabilitySidCount = capabilitySidCount;
        _attributes = attributes;
    }

    internal IntPtr Attributes => _attributes;
    internal uint Count => _capabilitySidCount;

    internal static AppContainerCapabilitySet Create(AppContainerCapabilities capabilities)
    {
        if (capabilities == AppContainerCapabilities.None)
        {
            return new AppContainerCapabilitySet(
                IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero);
        }
        if (capabilities != AppContainerCapabilities.CodeGeneration)
            throw new ArgumentOutOfRangeException(nameof(capabilities));

        IntPtr groupSids = IntPtr.Zero;
        IntPtr capabilitySids = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        uint groupSidCount = 0;
        uint capabilitySidCount = 0;
        try
        {
            if (!DeriveCapabilitySidsFromName(
                    "codeGeneration",
                    out groupSids,
                    out groupSidCount,
                    out capabilitySids,
                    out capabilitySidCount))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"DeriveCapabilitySidsFromName failed with Win32 error {error}.");
            }
            if (capabilitySidCount != 1 || capabilitySids == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The codeGeneration capability did not resolve to exactly one AppAuthority SID.");
            }

            attributes = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
            Marshal.StructureToPtr(
                new SidAndAttributes
                {
                    Sid = Marshal.ReadIntPtr(capabilitySids),
                    Attributes = SeGroupEnabled,
                },
                attributes,
                fDeleteOld: false);
            return new AppContainerCapabilitySet(
                groupSids,
                groupSidCount,
                capabilitySids,
                capabilitySidCount,
                attributes);
        }
        catch
        {
            if (attributes != IntPtr.Zero)
                Marshal.FreeHGlobal(attributes);
            FreeSidArray(groupSids, groupSidCount);
            FreeSidArray(capabilitySids, capabilitySidCount);
            throw;
        }
    }

    public void Dispose()
    {
        if (_attributes != IntPtr.Zero)
            Marshal.FreeHGlobal(_attributes);
        FreeSidArray(_groupSids, _groupSidCount);
        FreeSidArray(_capabilitySids, _capabilitySidCount);
        _attributes = IntPtr.Zero;
        _groupSids = IntPtr.Zero;
        _capabilitySids = IntPtr.Zero;
    }

    private static void FreeSidArray(IntPtr array, uint count)
    {
        if (array == IntPtr.Zero)
            return;
        for (uint index = 0; index < count; index++)
        {
            var sid = Marshal.ReadIntPtr(array, checked((int)index * IntPtr.Size));
            if (sid != IntPtr.Zero)
                _ = LocalFree(sid);
        }
        _ = LocalFree(array);
    }
}
