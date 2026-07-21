namespace EzyImageViewer.Core.Documents.Layers;

public static class AnnotationNumbering
{
    public static bool TryGetNextMarkerNumber(
        IReadOnlyList<Annotation> annotations, out int number)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var maximum = 0;
        foreach (var marker in annotations.OfType<NumberMarkerAnnotation>())
            maximum = Math.Max(maximum, marker.Number);
        if (maximum == int.MaxValue)
        {
            number = 0;
            return false;
        }
        number = maximum + 1;
        return true;
    }
}
