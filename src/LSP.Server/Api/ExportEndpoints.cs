using LSP.Server.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LSP.Server.Api;

public sealed record StartExportRequest(
    string TargetRoot,
    IReadOnlyList<int> MediaFileIds,
    bool Move = false,
    bool IncludePosters = true);
public sealed record ImportPackageRequest(string PackageRoot);

public sealed record ExportStatusDto(
    string State,
    bool? Started,
    string? TargetRoot,
    bool Move,
    bool IncludePosters,
    bool Extended,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    ExportProgress? Progress,
    ExportReport? Report,
    string? Error);

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/export", StartExport);
        app.MapGet("/api/export/status", GetStatus);
        app.MapPost("/api/export/cancel", CancelExport);
        app.MapPost("/api/import", ImportPackage);
    }

    private static IResult StartExport(
        StartExportRequest request,
        ExportJobService jobs,
        EnrichmentJobService enrichment)
    {
        if (string.IsNullOrWhiteSpace(request.TargetRoot))
            return Results.BadRequest("Cilova slozka je prazdna.");
        if (request.MediaFileIds.Count == 0)
            return Results.BadRequest("Vyberte alespon jeden film nebo serial.");
        if (enrichment.IsRunning)
            return Results.Conflict("Nejprve dokoncete obohaceni knihovny.");

        var started = jobs.TryStart(
            new ExportRequest(request.TargetRoot, request.MediaFileIds, request.Move, request.IncludePosters),
            out var status);
        return started
            ? Results.Ok(ToDto(status, true))
            : Results.Conflict(ToDto(status, false));
    }

    private static IResult GetStatus(ExportJobService jobs) => Results.Ok(ToDto(jobs.GetStatus(), null));

    private static IResult CancelExport(ExportJobService jobs)
    {
        jobs.Cancel();
        return Results.Ok(ToDto(jobs.GetStatus(), null));
    }

    private static async Task<IResult> ImportPackage(
        ImportPackageRequest request,
        ExportService service,
        ExportJobService export,
        EnrichmentJobService enrichment,
        LibraryOperationCoordinator operations,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PackageRoot))
            return Results.BadRequest("Slozka baliku je prazdna.");
        if (enrichment.IsRunning || export.IsRunning)
            return Results.Conflict("Bezi obohaceni nebo export knihovny.");
        if (!operations.TryBeginImport(out var lease))
            return Results.Conflict("Bezi sken, export nebo jiny import knihovny.");

        using (lease)
        {
            try
            {
                return Results.Ok(await service.ImportAsync(request.PackageRoot, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }
    }

    private static ExportStatusDto ToDto(ExportJobStatus status, bool? started) => new(
        status.State.ToString().ToLowerInvariant(),
        started,
        status.Request?.TargetRoot,
        status.Request?.Move ?? false,
        status.Request?.IncludePosters ?? true,
        status.Extended,
        status.StartedAt,
        status.FinishedAt,
        status.Progress,
        status.Report,
        status.Error);
}
