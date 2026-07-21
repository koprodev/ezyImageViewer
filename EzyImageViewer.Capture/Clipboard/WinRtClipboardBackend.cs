using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace EzyImageViewer.Capture.Clipboard;

/// <summary>
/// WinRT clipboard reader. Call from the UI thread (clipboard access is view-bound);
/// the returned payload is a bounded, owned copy taken before this method completes.
/// Format priority per FR-APP-004: PNG > DIBv5 > DIB > Bitmap.
/// </summary>
public sealed class WinRtClipboardBackend : IClipboardBackend, IClipboardImageWriter
{
    private const string PngFormat = "PNG";
    private const string DibV5Format = "DeviceIndependentBitmapV5";
    private const string DibFormat = "DeviceIndependentBitmap";
    /// <summary>FR-CAP-005: rides every internal copy so the capture watcher can tell our own
    /// clipboard writes from a real capture.</summary>
    private const string InternalMarkerFormat = "EzyImageViewer.InternalCopy";

    public async Task<ClipboardImagePayload?> TryGetImageAsync(long maxBytes, CancellationToken cancellationToken)
    {
        var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();

        if (view.Contains(PngFormat))
        {
            var bytes = await CopyDataAsync(view, PngFormat, maxBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
                return new ClipboardImagePayload(bytes, ClipboardImagePayload.Png);
        }

        foreach (var dibFormat in (string[])[DibV5Format, DibFormat])
        {
            if (!view.Contains(dibFormat))
                continue;
            var dib = await CopyDataAsync(view, dibFormat, maxBytes, cancellationToken).ConfigureAwait(false);
            if (dib is not null)
                return new ClipboardImagePayload(DibConverter.DibToBmp(dib), ClipboardImagePayload.Bmp);
        }

        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync().AsTask(cancellationToken).ConfigureAwait(false);
            using var stream = await reference.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var bytes = await CopyBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
            // OpenReadAsync yields an encoded image stream; sniffing decides the actual codec.
            if (bytes is not null)
                return new ClipboardImagePayload(bytes, ClipboardImagePayload.Png);
        }

        return null;
    }

    public async Task SetImagePngAsync(byte[] pngBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
            throw new ArgumentException("Empty PNG payload.", nameof(pngBytes));

        // Two independent streams: DataPackage consumers read each format separately.
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        var pngStream = await ToStreamAsync(pngBytes, cancellationToken).ConfigureAwait(true);
        var bitmapStream = await ToStreamAsync(pngBytes, cancellationToken).ConfigureAwait(true);
        var contentSet = false;
        try
        {
            package.SetData(PngFormat, pngStream);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(bitmapStream));
            package.SetData(InternalMarkerFormat, "1");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            contentSet = true;
            // Hands the data to the OS so the copy survives app exit (FR-OUT-001). A busy
            // clipboard can refuse the flush; the content then stays delegated to our streams,
            // which must outlive this call — they are only released after a successful flush.
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (System.Runtime.InteropServices.COMException) when (contentSet)
        {
            return;
        }
        catch
        {
            pngStream.Dispose();
            bitmapStream.Dispose();
            throw;
        }
        pngStream.Dispose();
        bitmapStream.Dispose();
    }

    public bool CurrentContentHasInternalMarker()
    {
        try
        {
            return Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                .Contains(InternalMarkerFormat);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false; // a busy clipboard reads as "not ours"; the hash backup still applies
        }
    }

    private static async Task<InMemoryRandomAccessStream> ToStreamAsync(
        byte[] bytes, CancellationToken cancellationToken)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(true);
        stream.Seek(0);
        return stream;
    }

    private static async Task<byte[]?> CopyDataAsync(
        DataPackageView view, string formatId, long maxBytes, CancellationToken cancellationToken)
    {
        var data = await view.GetDataAsync(formatId).AsTask(cancellationToken).ConfigureAwait(false);
        return data switch
        {
            IRandomAccessStream ras => await CopyBoundedAsync(ras, maxBytes, cancellationToken).ConfigureAwait(false),
            IRandomAccessStreamReference reference => await CopyReferenceAsync(reference, maxBytes, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private static async Task<byte[]?> CopyReferenceAsync(
        IRandomAccessStreamReference reference, long maxBytes, CancellationToken cancellationToken)
    {
        using var stream = await reference.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return await CopyBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> CopyBoundedAsync(
        IRandomAccessStream stream, long maxBytes, CancellationToken cancellationToken)
    {
        if ((long)stream.Size > maxBytes)
            throw new InvalidDataException($"Clipboard image ({stream.Size:N0} bytes) exceeds the {maxBytes:N0} byte limit.");
        if (stream.Size == 0)
            return null;

        using var managed = stream.AsStreamForRead();
        using var buffer = new MemoryStream((int)stream.Size);
        await managed.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
