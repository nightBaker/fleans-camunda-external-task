using System.Globalization;
using Fleans.Application.CustomTasks;
using Fleans.Domain.Activities;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Input-mapping targets that carry Camunda task metadata instead of a worker variable. Besides
/// these, <see cref="ExternalTaskPriority.InputTarget"/> and
/// <see cref="ExternalTaskService.ExtensionPropertyPrefix"/> are reserved.
/// </summary>
public static class ReservedInputs
{
    /// <summary>Camunda's process-instance business key, returned on the task and usable as a fetch filter.</summary>
    public const string BusinessKey = "businessKey";

    /// <summary>Camunda's tenant id, returned on the task and usable as a fetch filter.</summary>
    public const string TenantId = "tenantId";

    public static bool IsReserved(string target) =>
        target is BusinessKey or TenantId or ExternalTaskPriority.InputTarget
        || target.StartsWith(ExternalTaskService.ExtensionPropertyPrefix, StringComparison.Ordinal);

    /// <summary>The mapping for <paramref name="target"/> as a string; null when absent or null.</summary>
    public static string? EvaluateString(
        IEnumerable<InputMapping> inputMappings, string target, IDictionary<string, object?> variables)
    {
        var mapping = inputMappings.LastOrDefault(m => m.Target == target);
        return mapping is null
            ? null
            : Convert.ToString(MappingResolver.Resolve(mapping.Source, variables), CultureInfo.InvariantCulture);
    }
}
