using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace EzyImageViewer.Capture.Clipboard;

/// <summary>
/// UI 스레드용 WinRT 클립보드 읽기. 반환 데이터는 상한 안의 소유 복사본.
/// 형식 우선순위: PNG > DIBv5 > DIB > Bitmap.
/// </summary>
public sealed class WinRtClipboardBackend
{
    private const string PngFormat = "PNG";
    private const string DibV5Format = "DeviceIndependentBitmapV5";
    private const string DibFormat = "DeviceIndependentBitmap";
    /// <summary>내부 복사마다 실어 캡처 감시가 우리 쓰기와 실제 캡처를 구분.</summary>
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
            // 인코딩 이미지 스트림을 받아 실제 코덱은 시그니처로 판별.
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

        // 소비자가 형식별로 따로 읽으므로 독립 스트림 둘 사용.
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
            // 앱 종료 뒤에도 복사가 남도록 OS에 전달. 사용 중이면 실패할 수 있어 성공 뒤에만 스트림 해제.
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
            return false; // 사용 중이면 우리 데이터 아님으로 처리, 해시 보조 판정은 유지.
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
