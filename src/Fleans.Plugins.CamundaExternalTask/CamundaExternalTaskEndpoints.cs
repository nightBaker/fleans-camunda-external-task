using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Maps the Camunda 7 <c>/external-task</c> REST resource so existing workers (camunda-external-task-client
/// for Java / JS, or hand-written HTTP clients) only need their base URL changed.
/// </summary>
public static partial class CamundaExternalTaskEndpoints
{
    /// <summary>
    /// Maps <c>{basePath}/external-task/*</c>. Returns the route group so the host can add
    /// authorization (<c>.RequireAuthorization()</c>), rate limiting, etc.
    /// </summary>
    public static RouteGroupBuilder MapCamundaExternalTaskApi(
        this IEndpointRouteBuilder endpoints, string basePath = "/engine-rest")
    {
        var group = endpoints.MapGroup($"{basePath.TrimEnd('/')}/external-task")
            .WithTags("Camunda External Task")
            .AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (ExternalTaskApiException ex)
                {
                    return Json(new CamundaErrorDto(ex.Type, ex.Message), ex.StatusCode);
                }
                catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
                {
                    // Engine or database failure: Camunda clients expect its error body on a 500 too.
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(CamundaExternalTaskEndpoints));
                    LogUnhandled(logger, ex, context.HttpContext.Request.Path);
                    return Json(new CamundaErrorDto("ProcessEngineException", ex.Message), StatusCodes.Status500InternalServerError);
                }
            });

        group.MapPost("/fetchAndLock", async (FetchExternalTasksDto body, ExternalTaskService service, HttpContext http) =>
            Json(await service.FetchAndLock(body, http.RequestAborted)));

        group.MapPost("/{id}/complete", async (string id, CompleteExternalTaskDto body, ExternalTaskService service) =>
        {
            await service.Complete(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/failure", async (string id, ExternalTaskFailureDto body, ExternalTaskService service) =>
        {
            await service.HandleFailure(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/bpmnError", async (string id, ExternalTaskBpmnErrorDto body, ExternalTaskService service) =>
        {
            await service.HandleBpmnError(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/extendLock", async (string id, ExtendLockDto body, ExternalTaskService service) =>
        {
            await service.ExtendLock(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/lock", async (string id, LockExternalTaskDto body, ExternalTaskService service) =>
        {
            await service.Lock(id, body);
            return Results.NoContent();
        });

        group.MapPost("/{id}/unlock", async (string id, ExternalTaskService service) =>
        {
            await service.Unlock(id);
            return Results.NoContent();
        });

        return group;
    }

    /// <summary>
    /// Camunda answers with a bare <c>application/json</c> content type, and the official JS client
    /// only parses bodies whose content type matches it exactly — ASP.NET's default
    /// <c>application/json; charset=utf-8</c> makes it treat the body as raw bytes.
    /// </summary>
    private static IResult Json(object value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, contentType: "application/json", statusCode: statusCode);

    [LoggerMessage(EventId = 9355, Level = LogLevel.Error,
        Message = "Camunda external-task request {Path} failed")]
    private static partial void LogUnhandled(ILogger logger, Exception ex, string path);
}
