using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Ontology;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Selectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddLambdaRagParsing()
    .AddLambdaRagProjection()
    .AddLambdaRagSelectors()
    .AddLambdaRagEvaluation();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { name = "lambda-rag", version = "0.1.0" }));

app.MapPost("/review", async (
    ReviewRequest req,
    ParserRegistry parsers,
    IDocumentProjector projector,
    EvaluationService evaluator,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    if (!File.Exists(req.DocumentPath)) return Results.BadRequest("documentPath not found");
    if (!File.Exists(req.RuleSetPath)) return Results.BadRequest("ruleSetPath not found");

    var parsed = await parsers.ParseAsync(req.DocumentPath, ct);
    var effectiveProjector = projector;
    if (req.OntologyMode is not null || req.OntologySnapshotPath is not null || req.TopicMap is not null)
    {
        var mode = req.OntologyMode?.ToLowerInvariant() switch
        {
            null or "static" => OntologyTopicMode.Static,
            "dynamic" => OntologyTopicMode.Dynamic,
            "hybrid" => OntologyTopicMode.Hybrid,
            _ => throw new ArgumentException("ontologyMode must be static, dynamic, or hybrid."),
        };
        if (req.OntologySnapshotPath is null && mode != OntologyTopicMode.Static)
            throw new ArgumentException("ontologySnapshotPath is required for dynamic and hybrid modes.");
        if (req.OntologySnapshotPath is not null && mode == OntologyTopicMode.Static)
            throw new ArgumentException("ontologySnapshotPath cannot be used with static mode.");

        if (mode == OntologyTopicMode.Static && req.OntologySnapshotPath is null && req.TopicMap is null)
        {
            effectiveProjector = projector;
        }
        else
        {
            var staticMap = TopicMapRegistry.Load(req.TopicMap ?? "contract.v1");
            var topicMap = req.OntologySnapshotPath is null
                ? staticMap
                : AcceptedOntologyTopicMapAdapter.Create(
                    OntologySnapshot.ParseStrict(await File.ReadAllTextAsync(req.OntologySnapshotPath, ct)),
                    mode,
                    mode == OntologyTopicMode.Dynamic ? null : staticMap);
            effectiveProjector = new LambdaRag.Projection.Projectors.DeterministicContractProjector(topicMap);
        }
    }

    var projected = await effectiveProjector.ProjectAsync(parsed, ct);
    var ruleset = LambdaRag.Cli.RuleSetIO.Load(req.RuleSetPath);
    var report = await evaluator.EvaluateAsync(ruleset, projected, ct);
    return Results.Ok(report);
});

app.Run();

public record ReviewRequest(
    string DocumentPath,
    string RuleSetPath,
    string? OntologySnapshotPath = null,
    string? OntologyMode = null,
    string? TopicMap = null);

