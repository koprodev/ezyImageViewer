namespace EzyImageViewer.Core.Documents;

public enum DocumentDiagnosticSeverity
{
    Information,
    Warning,
}

/// <summary>지원 진단을 위해 문서별로 기록한 렌더러 ID.</summary>
public sealed record DocumentRendererInfo(string Name, string Version)
{
    public static DocumentRendererInfo Unknown { get; } = new("Unknown", "Unknown");
}

/// <summary>안정된 코드와 표시 문구를 가진 기계 판독용 진단.</summary>
public sealed record DocumentDiagnostic(
    string Code,
    DocumentDiagnosticSeverity Severity,
    string Message);
