# CA2254 Reviewer-Runnable Proof (AC-4)

This file documents how to verify that **CA2254 — Template should be a static expression** fires on a
string-interpolated `ILogger.Log*` call in this repository, as required by Story 1.5 AC-4.

The rule ships with `Microsoft.CodeAnalysis.NetAnalyzers` (already active via `AnalysisMode=AllEnabledByDefault`
in `Directory.Build.props`). With `TreatWarningsAsErrors=true`, a violation becomes a build error.

## Reproducer

Temporarily add the following lines to the bottom of `src/FormForge.Api/Program.cs` (above
`public partial class Program;`):

```csharp
internal static class Ca2254Repro
{
    public static void Repro(ILogger logger, int id) =>
        logger.LogInformation($"Saved {id}");  // <-- CA2254 here
}
```

Then run:

```pwsh
dotnet build src/FormForge.Api/FormForge.Api.csproj
```

Expected output (build fails):

```
error CA2254: The logging message template should not vary between calls to 'LogInformation(...)'
```

Revert the change. The repository must not commit broken-build code; this doc is the proof.

## Reference

- Rule docs: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2254
- Architecture spec: `_bmad-output/planning-artifacts/architecture.md` § "Logging Conventions" (anti-patterns)
- Story spec: Story 1.5 AC-4
