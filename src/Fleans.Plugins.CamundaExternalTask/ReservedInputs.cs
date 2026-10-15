using System.Globalization;
using Fleans.Application.CustomTasks;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Input-mapping targets that carry Camunda task metadata instead of a worker variable. Besides
/// these, <see cref="ExternalTaskPriority.InputTarget"/> and
/// <see cref="ExternalTaskService.ExtensionPropertyPrefix"/> are reserved.
/// </summary>
public static class ReservedInputs
{
    /// <summary>
    /// Camunda's process-instance business key, returned on the task and usable as a fetch filter.
    /// Overrides the instance's business id, which is the business key when this input is absent.
    /// </summary>
    public const string BusinessKey = "businessKey";

    /// <summary>Camunda's tenant id, returned on the task and usable as a fetch filter.</summary>
    public const string TenantId = "tenantId";

    /// <summary>Column size of <see cref="ExternalTaskEntity.BusinessKey"/> and <see cref="ExternalTaskEntity.TenantId"/>.</summary>
    public const int MaxLength = 256;

    public static bool IsReserved(string target) =>
        target is BusinessKey or TenantId or ExternalTaskPriority.InputTarget
        || target.StartsWith(ExternalTaskService.ExtensionPropertyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The mapping for <paramref name="target"/> as a string; null when absent or null. Throws
    /// <see cref="CustomTaskFailedActivityException"/> (code <c>400</c>) when it is longer than
    /// <see cref="MaxLength"/>, since a retry cannot make it fit the column.
    /// </summary>
    public static string? EvaluateString(
        IEnumerable<InputMapping> inputMappings, string target, IDictionary<string, object?> variables)
    {
        var mapping = inputMappings.LastOrDefault(m => m.Target == target);
        if (mapping is null) return null;
        var text = ToText(MappingResolver.Resolve(mapping.Source, variables));
        if (text is { Length: > MaxLength })
            throw new CustomTaskFailedActivityException("400",
                $"{target} '{mapping.Source}' is {text.Length} characters long; at most {MaxLength} are allowed.");
        return text;
    }

    /// <summary>
    /// A variable value as the text a Camunda worker expects: booleans as <c>true</c>/<c>false</c>,
    /// numbers in invariant culture, objects and lists as JSON.
    /// </summary>
    public static string? ToText(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        IDictionary<string, object?> or System.Collections.IEnumerable => CamundaVariables.ToCamunda(value).Value as string,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
