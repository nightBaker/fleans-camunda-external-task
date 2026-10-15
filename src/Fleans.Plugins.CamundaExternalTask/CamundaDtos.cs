namespace Fleans.Plugins.CamundaExternalTask;

// Request/response shapes of the Camunda 7 REST API (/engine-rest/external-task). Property names
// serialize camelCase, matching Camunda. Request properties a Fleans deployment cannot honour
// (variable filters, …) are accepted and ignored by the JSON binder; businessKey and tenantIdIn
// are honoured as filters.
// Fields with no Fleans equivalent are returned as null (see issue #1).

/// <summary>Camunda typed value: <c>{ "value": …, "type": "String", "valueInfo": {} }</c>.</summary>
public sealed record VariableValueDto(object? Value, string? Type, Dictionary<string, object?>? ValueInfo);

public sealed record FetchExternalTasksDto(
    string? WorkerId,
    int MaxTasks,
    long? AsyncResponseTimeout,
    List<FetchExternalTaskTopicDto>? Topics,
    bool? UsePriority = null,
    List<SortingDto>? Sorting = null);

/// <summary><c>fetchAndLock</c> sorting (Camunda 7.21+). Only <c>createTime</c> exists.</summary>
public sealed record SortingDto(string? SortBy, string? SortOrder);

/// <summary>A missing <c>priority</c> is 0, as in Camunda's DTO.</summary>
public sealed record PriorityDto(long Priority);

public sealed record FetchExternalTaskTopicDto(
    string TopicName,
    long LockDuration,
    List<string>? Variables,
    bool LocalVariables,
    string? ProcessDefinitionId,
    List<string>? ProcessDefinitionIdIn,
    string? ProcessDefinitionKey,
    List<string>? ProcessDefinitionKeyIn,
    bool IncludeExtensionProperties = false,
    string? BusinessKey = null,
    List<string>? TenantIdIn = null,
    bool WithoutTenantId = false);

public sealed record CompleteExternalTaskDto(
    string? WorkerId,
    Dictionary<string, VariableValueDto>? Variables,
    Dictionary<string, VariableValueDto>? LocalVariables);

public sealed record ExternalTaskFailureDto(
    string? WorkerId,
    string? ErrorMessage,
    string? ErrorDetails,
    int? Retries,
    long RetryTimeout);

public sealed record ExternalTaskBpmnErrorDto(
    string? WorkerId,
    string? ErrorCode,
    string? ErrorMessage,
    Dictionary<string, VariableValueDto>? Variables);

public sealed record ExtendLockDto(string? WorkerId, long NewDuration);

public sealed record LockExternalTaskDto(string? WorkerId, long LockDuration);

public sealed record CamundaErrorDto(string Type, string Message);

/// <summary>Element of the <c>fetchAndLock</c> response.</summary>
public sealed record LockedExternalTaskDto(
    string Id,
    string TopicName,
    string WorkerId,
    string LockExpirationTime,
    string? CreateTime,
    string ActivityId,
    string ActivityInstanceId,
    string ExecutionId,
    string ProcessInstanceId,
    string? ProcessDefinitionId,
    string ProcessDefinitionKey,
    string? ProcessDefinitionVersionTag,
    int? Retries,
    string? ErrorMessage,
    string? ErrorDetails,
    string? TenantId,
    long Priority,
    string? BusinessKey,
    Dictionary<string, VariableValueDto> Variables,
    Dictionary<string, string> ExtensionProperties);

public sealed record RetriesDto(int? Retries);

public sealed record CountResultDto(long Count);

/// <summary>Query string of <c>GET /external-task</c> and <c>/count</c>; unknown parameters are ignored.</summary>
public sealed record ExternalTaskQueryDto(
    string? ExternalTaskId = null,
    string? TopicName = null,
    string? WorkerId = null,
    bool? Locked = null,
    bool? NotLocked = null,
    bool? WithRetriesLeft = null,
    bool? NoRetriesLeft = null,
    string? ActivityId = null,
    string? ProcessInstanceId = null,
    string? ProcessDefinitionId = null,
    string? TenantIdIn = null,
    int? FirstResult = null,
    int? MaxResults = null);

/// <summary>Element of <c>GET /external-task</c>, and the body of <c>GET /external-task/{id}</c>.</summary>
public sealed record ExternalTaskDto(
    string Id,
    string TopicName,
    string? WorkerId,
    string? LockExpirationTime,
    string? CreateTime,
    string ActivityId,
    string ActivityInstanceId,
    string ExecutionId,
    string ProcessInstanceId,
    string? ProcessDefinitionId,
    string ProcessDefinitionKey,
    string? ProcessDefinitionVersionTag,
    int? Retries,
    string? ErrorMessage,
    bool Suspended,
    string? TenantId,
    long Priority,
    string? BusinessKey);
