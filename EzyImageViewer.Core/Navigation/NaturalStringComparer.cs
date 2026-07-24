namespace EzyImageViewer.Core.Navigation;

/// <summary>
/// 탐색기식 자연 정렬. 숫자 덩어리는 값으로 비교("image2" &lt; "image10"), 나머지는 대소문자 무시.
/// 숫자가 같으면 앞쪽 0이 적은 짧은 덩어리를 먼저 두어 전체 순서를 안정화.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var (xValueEnd, xStart) = (ScanDigits(x, i), i);
                var (yValueEnd, yStart) = (ScanDigits(y, j), j);

                var xRun = TrimLeadingZeros(x, xStart, xValueEnd);
                var yRun = TrimLeadingZeros(y, yStart, yValueEnd);
                var byLength = (xValueEnd - xRun).CompareTo(yValueEnd - yRun);
                if (byLength != 0)
                    return byLength;
                for (int a = xRun, b = yRun; a < xValueEnd; a++, b++)
                {
                    if (x[a] != y[b])
                        return x[a].CompareTo(y[b]);
                }
        // 숫자가 같으면(예: "007" 대 "7") 앞쪽 0이 적은 쪽을 먼저 둠.
                var byZeros = (xValueEnd - xStart).CompareTo(yValueEnd - yStart);
                if (byZeros != 0)
                    return byZeros;
                (i, j) = (xValueEnd, yValueEnd);
            }
            else
            {
                var cx = char.ToUpperInvariant(x[i]);
                var cy = char.ToUpperInvariant(y[j]);
                if (cx != cy)
                    return cx.CompareTo(cy);
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }

    private static int ScanDigits(string s, int start)
    {
        while (start < s.Length && char.IsAsciiDigit(s[start]))
            start++;
        return start;
    }

    private static int TrimLeadingZeros(string s, int start, int end)
    {
        while (start < end - 1 && s[start] == '0')
            start++;
        return start;
    }
}
