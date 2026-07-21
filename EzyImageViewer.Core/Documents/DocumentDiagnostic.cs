namespace EzyImageViewer.Core.Documents;

public enum DocumentDiagnosticSeverity
{
    Information,
    Warning,
}

/// <summary>Renderer identity captured per opened document for support diagnostics.</summary>
public sealed record DocumentRendererInfo(string Name, string Version)
{
    public static DocumentRendererInfo Unknown { get; } = new("Unknown", "Unknown");
}

/// <summary>Machine-readable diagnostic with a stable code and display text.</summary>
public sealed record DocumentDiagnostic(
    string Code,
    DocumentDiagnosticSeverity Severity,
    string Message);
