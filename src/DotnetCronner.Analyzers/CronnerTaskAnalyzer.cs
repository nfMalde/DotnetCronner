using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotnetCronner.Analyzers;

/// <summary>
/// Build-time diagnostics for <c>[CronnerTask]</c> usage: catches invalid cron expressions and duplicate
/// explicit task ids before the app runs.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CronnerTaskAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeMetadataName = "DotnetCronner.CronnerTaskAttribute";

    /// <summary>DC0001 — the cron expression on a <c>[CronnerTask]</c> is invalid.</summary>
    public static readonly DiagnosticDescriptor InvalidCron = new(
        id: "DC0001",
        title: "Invalid cron expression",
        messageFormat: "Invalid cron expression: {0}",
        category: "DotnetCronner",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The cron expression passed to [CronnerTask] cannot be parsed.");

    /// <summary>DC0002 — two <c>[CronnerTask]</c> attributes declare the same explicit id.</summary>
    public static readonly DiagnosticDescriptor DuplicateId = new(
        id: "DC0002",
        title: "Duplicate DotnetCronner task id",
        messageFormat: "Task id '{0}' is used by more than one [CronnerTask]; ids must be unique",
        category: "DotnetCronner",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Registering two tasks with the same id throws at startup.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(InvalidCron, DuplicateId);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationStart =>
        {
            var attributeSymbol = compilationStart.Compilation.GetTypeByMetadataName(AttributeMetadataName);
            if (attributeSymbol is null)
                return;

            var idLocations = new ConcurrentBag<(string Id, Location Location)>();

            compilationStart.RegisterSyntaxNodeAction(
                ctx => AnalyzeAttribute(ctx, attributeSymbol, idLocations), SyntaxKind.Attribute);

            compilationStart.RegisterCompilationEndAction(ctx =>
            {
                foreach (var group in idLocations.GroupBy(x => x.Id, StringComparer.Ordinal))
                {
                    if (group.Count() <= 1)
                        continue;

                    foreach (var (id, location) in group)
                        ctx.ReportDiagnostic(Diagnostic.Create(DuplicateId, location, id));
                }
            });
        });
    }

    private static void AnalyzeAttribute(
        SyntaxNodeAnalysisContext context, INamedTypeSymbol attributeSymbol,
        ConcurrentBag<(string, Location)> idLocations)
    {
        var attribute = (AttributeSyntax)context.Node;
        var type = context.SemanticModel.GetTypeInfo(attribute, context.CancellationToken).Type;
        if (!SymbolEqualityComparer.Default.Equals(type, attributeSymbol))
            return;

        var (idExpression, cronExpression) = FindArguments(attribute);

        if (cronExpression is not null &&
            context.SemanticModel.GetConstantValue(cronExpression, context.CancellationToken) is { HasValue: true, Value: string cron } &&
            CronSyntax.Validate(cron) is { } reason)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidCron, cronExpression.GetLocation(), reason));
        }

        if (idExpression is not null &&
            context.SemanticModel.GetConstantValue(idExpression, context.CancellationToken) is { HasValue: true, Value: string id } &&
            !string.IsNullOrWhiteSpace(id))
        {
            idLocations.Add((id, idExpression.GetLocation()));
        }
    }

    private static (ExpressionSyntax? Id, ExpressionSyntax? Cron) FindArguments(AttributeSyntax attribute)
    {
        ExpressionSyntax? id = null;
        ExpressionSyntax? cron = null;
        var positional = 0;

        foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
        {
            if (argument.NameEquals is not null)
                continue; // named property (Priority, Concurrency, ...) — not id/cronstring

            if (argument.NameColon is { } nameColon)
            {
                switch (nameColon.Name.Identifier.ValueText)
                {
                    case "id":
                        id = argument.Expression;
                        break;
                    case "cronstring":
                        cron = argument.Expression;
                        break;
                }

                continue;
            }

            switch (positional++)
            {
                case 0:
                    id = argument.Expression;
                    break;
                case 1:
                    cron = argument.Expression;
                    break;
            }
        }

        return (id, cron);
    }
}
