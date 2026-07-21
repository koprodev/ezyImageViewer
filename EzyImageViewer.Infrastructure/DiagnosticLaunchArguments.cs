namespace EzyImageViewer.Infrastructure;

public enum DiagnosticLaunchMode
{
    None,
    ZoomPanBenchmark,
    StartupBenchmark,
    Open24MegapixelBenchmark,
    OpenSmoke,
    HoldSmoke,
    RecoverySeed,
    RecoveryVerify,
}

public readonly record struct DiagnosticLaunchPlan(DiagnosticLaunchMode Mode)
{
    public bool IsDiagnostic => Mode != DiagnosticLaunchMode.None;
    public bool IsStandalone => IsDiagnostic && Mode != DiagnosticLaunchMode.StartupBenchmark;
}

/// <summary>Fail-closed parser for internal benchmark and smoke command-line surfaces.</summary>
public static class DiagnosticLaunchArguments
{
    [Flags]
    private enum Companion
    {
        None = 0,
        BenchmarkBackend = 1 << 0,
        SmokeOutput = 1 << 1,
        SmokeCodec = 1 << 2,
        SmokeProject = 1 << 3,
        SmokeCapture = 1 << 4,
        RecoveryOutput = 1 << 5,
        RecoveryRoot = 1 << 6,
    }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out DiagnosticLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var mode = DiagnosticLaunchMode.None;
        var companions = Companion.None;

        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                plan = default;
                return false;
            }
            if (!HasDiagnosticPrefix(argument))
                continue;

            if (TryMatchValue(argument, "--bench-zoompan=", out var matched))
            {
                if (!matched || !TrySetMode(ref mode, DiagnosticLaunchMode.ZoomPanBenchmark))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--spike-zoompan=", out matched))
            {
                if (!matched || !TrySetMode(ref mode, DiagnosticLaunchMode.ZoomPanBenchmark))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--bench-startup=", out matched))
            {
                if (!matched || !TrySetMode(ref mode, DiagnosticLaunchMode.StartupBenchmark))
                    return Fail(out plan);
                continue;
            }
            if (argument.Equals("--bench-open24mp", StringComparison.Ordinal)
                || TryMatchValue(argument, "--bench-open24mp=", out matched) && matched)
            {
                if (!TrySetMode(ref mode, DiagnosticLaunchMode.Open24MegapixelBenchmark))
                    return Fail(out plan);
                continue;
            }
            if (argument.StartsWith("--bench-open24mp=", StringComparison.Ordinal))
                return Fail(out plan);
            if (TryMatchValue(argument, "--smoke-open=", out matched))
            {
                if (!matched || !TrySetMode(ref mode, DiagnosticLaunchMode.OpenSmoke))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--smoke-hold=", out matched))
            {
                if (!matched || !TrySetMode(ref mode, DiagnosticLaunchMode.HoldSmoke))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--diagnostic-recovery-seed=", out matched))
            {
                if (!matched || !TrySetMode(ref mode, DiagnosticLaunchMode.RecoverySeed))
                    return Fail(out plan);
                continue;
            }
            if (argument.Equals("--diagnostic-recovery-verify", StringComparison.Ordinal))
            {
                if (!TrySetMode(ref mode, DiagnosticLaunchMode.RecoveryVerify))
                    return Fail(out plan);
                continue;
            }

            if (TryMatchValue(argument, "--bench-backend=", out matched))
            {
                if (!matched || !TryAddCompanion(ref companions, Companion.BenchmarkBackend))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--smoke-out=", out matched))
            {
                if (!matched || !TryAddCompanion(ref companions, Companion.SmokeOutput))
                    return Fail(out plan);
                continue;
            }
            if (argument.Equals("--smoke-codec", StringComparison.Ordinal))
            {
                if (!TryAddCompanion(ref companions, Companion.SmokeCodec))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--smoke-project=", out matched))
            {
                if (!matched || !TryAddCompanion(ref companions, Companion.SmokeProject))
                    return Fail(out plan);
                continue;
            }
            if (argument.Equals("--smoke-capture", StringComparison.Ordinal))
            {
                if (!TryAddCompanion(ref companions, Companion.SmokeCapture))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--diagnostic-recovery-out=", out matched))
            {
                if (!matched || !TryAddCompanion(ref companions, Companion.RecoveryOutput))
                    return Fail(out plan);
                continue;
            }
            if (TryMatchValue(argument, "--diagnostic-recovery-root=", out matched))
            {
                if (!matched || !TryAddCompanion(ref companions, Companion.RecoveryRoot))
                    return Fail(out plan);
                continue;
            }

            return Fail(out plan);
        }

        var valid = mode switch
        {
            DiagnosticLaunchMode.None => companions == Companion.None,
            DiagnosticLaunchMode.ZoomPanBenchmark =>
                (companions & ~Companion.BenchmarkBackend) == Companion.None,
            DiagnosticLaunchMode.StartupBenchmark or
                DiagnosticLaunchMode.Open24MegapixelBenchmark or
                DiagnosticLaunchMode.HoldSmoke => companions == Companion.None,
            DiagnosticLaunchMode.OpenSmoke =>
                (companions & ~(Companion.SmokeOutput
                    | Companion.SmokeCodec
                    | Companion.SmokeProject
                    | Companion.SmokeCapture)) == Companion.None,
            DiagnosticLaunchMode.RecoverySeed or DiagnosticLaunchMode.RecoveryVerify =>
                companions == (Companion.RecoveryOutput | Companion.RecoveryRoot),
            _ => false,
        };
        if (!valid)
            return Fail(out plan);

        plan = new DiagnosticLaunchPlan(mode);
        return true;
    }

    private static bool HasDiagnosticPrefix(string argument) =>
        argument.StartsWith("--bench-", StringComparison.Ordinal)
        || argument.StartsWith("--spike-", StringComparison.Ordinal)
        || argument.StartsWith("--smoke-", StringComparison.Ordinal)
        || argument.StartsWith("--diagnostic-", StringComparison.Ordinal);

    private static bool TryMatchValue(string argument, string prefix, out bool hasValue)
    {
        if (!argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            hasValue = false;
            return false;
        }
        hasValue = !string.IsNullOrWhiteSpace(argument[prefix.Length..]);
        return true;
    }

    private static bool TrySetMode(ref DiagnosticLaunchMode current, DiagnosticLaunchMode value)
    {
        if (current != DiagnosticLaunchMode.None)
            return false;
        current = value;
        return true;
    }

    private static bool TryAddCompanion(ref Companion current, Companion value)
    {
        if ((current & value) != 0)
            return false;
        current |= value;
        return true;
    }

    private static bool Fail(out DiagnosticLaunchPlan plan)
    {
        plan = default;
        return false;
    }
}
