using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text;
using Jellyfin.Plugin.BulkMetadata.Core;
using Jellyfin.Plugin.BulkMetadata.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BulkMetadata.Api;

/// <summary>
/// REST API used by the Bulk Metadata dashboard page. Administrators only.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("BulkMetadata")]
[Produces(MediaTypeNames.Application.Json)]
public class BulkMetadataController : ControllerBase
{
    private readonly BulkEditService _service;
    private readonly JobManager _jobs;
    private readonly HistoryStore _history;

    /// <summary>Initializes a new instance of the <see cref="BulkMetadataController"/> class.</summary>
    /// <param name="service">Service.</param>
    /// <param name="jobs">Jobs.</param>
    /// <param name="history">History.</param>
    public BulkMetadataController(BulkEditService service, JobManager jobs, HistoryStore history)
    {
        _service = service;
        _jobs = jobs;
        _history = history;
    }

    /// <summary>Field catalog, item types and other lookups for the UI.</summary>
    /// <returns>Catalog.</returns>
    [HttpGet("Catalog")]
    public ActionResult GetCatalog()
    {
        return Ok(new
        {
            Fields = FieldCatalog.Fields.Select(f => new { f.Key, f.Label, Kind = f.Kind.ToString(), f.LockName }),
            Providers = FieldCatalog.CommonProviders,
            LockNames = FieldCatalog.LockNames,
            ItemTypes = BulkEditService.DefaultKinds.Select(k => k.ToString()),
            DefaultColumns = BulkEditService.DefaultColumns,
            Libraries = _service.GetLibraries(),
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            DefaultLockEditedFields = Plugin.Instance?.Configuration.DefaultLockEditedFields ?? true,
        });
    }

    /// <summary>Searches items.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Results.</returns>
    [HttpPost("Search")]
    public ActionResult<SearchResponse> Search([FromBody] SearchRequest request)
        => Run(() => _service.SearchPage(request ?? new SearchRequest()));

    /// <summary>Distinct values of a field with counts.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Values.</returns>
    [HttpPost("Values")]
    public ActionResult<List<ValueCount>> Values([FromBody] ValuesRequest request)
        => Run(() => _service.GetValues(request));

    /// <summary>Previews an edit without saving anything.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Preview.</returns>
    [HttpPost("Preview")]
    public ActionResult<PreviewResponse> Preview([FromBody] EditRequest request)
        => Run(() => BulkEditService.Preview(_service.PlanEdit(request)));

    /// <summary>Applies an edit in the background.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Job.</returns>
    [HttpPost("Apply")]
    public ActionResult<JobStatus> Apply([FromBody] EditRequest request)
        => Run(() =>
        {
            var plans = _service.PlanEdit(request);
            var desc = string.IsNullOrWhiteSpace(request.Description) ? Describe(request.Operations) : request.Description!.Trim();
            return _service.StartApply(plans, desc, UserName());
        });

    /// <summary>Previews a CSV import.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Preview.</returns>
    [HttpPost("Import/Preview")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public ActionResult<PreviewResponse> ImportPreview([FromBody] ImportRequest request)
        => Run(() =>
        {
            var warnings = new List<string>();
            var plans = _service.PlanImport(request, warnings);
            return BulkEditService.Preview(plans, warnings);
        });

    /// <summary>Applies a CSV import in the background.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Job.</returns>
    [HttpPost("Import/Apply")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public ActionResult<JobStatus> ImportApply([FromBody] ImportRequest request)
        => Run(() =>
        {
            var warnings = new List<string>();
            var plans = _service.PlanImport(request, warnings);
            return _service.StartApply(plans, "Spreadsheet (CSV) import", UserName());
        });

    /// <summary>Exports items to CSV.</summary>
    /// <param name="request">Request.</param>
    /// <returns>CSV file.</returns>
    [HttpPost("Export")]
    [Produces("text/csv")]
    public ActionResult Export([FromBody] ExportRequest request)
    {
        try
        {
            var csv = _service.Export(request);
            return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"jellyfin-metadata-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>Queues a metadata refresh from online providers.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Count queued.</returns>
    [HttpPost("Refresh")]
    public ActionResult Refresh([FromBody] RefreshRequest request)
        => Run(() => (object)new { Queued = _service.Refresh(request) });

    /// <summary>Gets a job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>Status.</returns>
    [HttpGet("Jobs/{id}")]
    public ActionResult<JobStatus> GetJob([FromRoute] string id)
    {
        var job = _jobs.Get(id);
        return job is null ? NotFound(new { Error = "Job not found." }) : Ok(job);
    }

    /// <summary>Gets the running job, if any.</summary>
    /// <returns>Status or 204.</returns>
    [HttpGet("Jobs/Current")]
    public ActionResult<JobStatus> GetCurrentJob()
    {
        var job = _jobs.Current;
        return job is null ? NoContent() : Ok(job);
    }

    /// <summary>Cancels a job (items already saved stay saved and can be undone).</summary>
    /// <param name="id">Job id.</param>
    /// <returns>OK.</returns>
    [HttpPost("Jobs/{id}/Cancel")]
    public ActionResult CancelJob([FromRoute] string id)
        => _jobs.Cancel(id) ? Ok(new { Cancelled = true }) : NotFound(new { Error = "Job not found." });

    /// <summary>Lists past batches.</summary>
    /// <returns>Summaries.</returns>
    [HttpGet("History")]
    public ActionResult<List<HistorySummary>> GetHistory() => Ok(_history.List());

    /// <summary>Gets one batch with every change (first 500 items).</summary>
    /// <param name="id">Id.</param>
    /// <returns>Entry.</returns>
    [HttpGet("History/{id}")]
    public ActionResult GetHistoryEntry([FromRoute] string id)
    {
        var entry = _history.Get(id);
        if (entry is null)
        {
            return NotFound(new { Error = "Batch not found." });
        }

        return Ok(new
        {
            entry.Id,
            entry.TimestampUtc,
            entry.Description,
            entry.ItemCount,
            entry.Fields,
            entry.User,
            entry.UndoOf,
            entry.UndoneBy,
            Items = entry.Items.Take(500).Select(i => new
            {
                i.ItemId,
                i.Name,
                i.Type,
                Changes = i.Before.Keys().Union(i.After.Keys(), StringComparer.OrdinalIgnoreCase).Select(k =>
                {
                    var def = FieldCatalog.Find(k);
                    return new FieldChange
                    {
                        Field = k,
                        Label = def?.Label ?? k,
                        Before = def is null ? null : i.Before.Display(def),
                        After = def is null ? null : i.After.Display(def),
                    };
                }),
            }),
        });
    }

    /// <summary>Previews undoing a batch.</summary>
    /// <param name="id">Id.</param>
    /// <returns>Preview.</returns>
    [HttpPost("History/{id}/Undo/Preview")]
    public ActionResult<PreviewResponse> UndoPreview([FromRoute] string id)
    {
        var entry = _history.Get(id);
        if (entry is null)
        {
            return NotFound(new { Error = "Batch not found." });
        }

        return Run(() =>
        {
            var warnings = new List<string>();
            return BulkEditService.Preview(_service.PlanUndo(entry, warnings), warnings);
        });
    }

    /// <summary>Undoes a batch in the background.</summary>
    /// <param name="id">Id.</param>
    /// <returns>Job.</returns>
    [HttpPost("History/{id}/Undo")]
    public ActionResult<JobStatus> Undo([FromRoute] string id)
    {
        var entry = _history.Get(id);
        if (entry is null)
        {
            return NotFound(new { Error = "Batch not found." });
        }

        return Run(() =>
        {
            var warnings = new List<string>();
            var plans = _service.PlanUndo(entry, warnings);
            return _service.StartApply(plans, "Undo: " + entry.Description, UserName(), entry.Id);
        });
    }

    /// <summary>Deletes a batch from history (metadata is not touched).</summary>
    /// <param name="id">Id.</param>
    /// <returns>OK.</returns>
    [HttpDelete("History/{id}")]
    public ActionResult DeleteHistory([FromRoute] string id)
        => _history.Delete(id) ? Ok(new { Deleted = true }) : NotFound(new { Error = "Batch not found." });

    private ActionResult Run<T>(Func<T> action)
    {
        try
        {
            return Ok(action());
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    private string? UserName() => User?.Identity?.Name;

    private static string Describe(List<EditOperation> ops)
    {
        var parts = ops.Take(3).Select(op =>
        {
            var label = FieldCatalog.Find(op.Field)?.Label ?? op.Field;
            return (op.Action ?? string.Empty).ToLowerInvariant() switch
            {
                "set" => $"Set {label} = \"{Trunc(op.Value)}\"",
                "clear" => $"Clear {label}",
                "add" => $"Add {label}: {Trunc(op.Value)}",
                "remove" => $"Remove {label}: {Trunc(op.Value)}",
                "replace" => $"Replace \"{Trunc(op.Find)}\" → \"{Trunc(op.Replace)}\" in {label}",
                "append" => $"Append to {label}",
                "prepend" => $"Prepend to {label}",
                "case" => $"Change case of {label}",
                "trim" => $"Clean up {label}",
                "sort" => $"Sort {label}",
                "copy" => $"Copy {FieldCatalog.Find(op.SourceField)?.Label ?? op.SourceField} → {label}",
                "lockfields" => $"Lock {Trunc(op.Value)}",
                "unlockfields" => $"Unlock {Trunc(op.Value)}",
                _ => op.Action ?? string.Empty,
            };
        }).ToList();
        if (ops.Count > 3)
        {
            parts.Add($"+{ops.Count - 3} more");
        }

        return string.Join("; ", parts);
    }

    private static string Trunc(string? s) => s is null ? string.Empty : s.Length > 40 ? s.Substring(0, 37) + "…" : s;
}
