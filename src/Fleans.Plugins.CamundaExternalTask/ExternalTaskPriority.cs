using System.Globalization;
using Fleans.Application.CustomTasks;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// Task priority, Camunda's <c>camunda:taskPriority</c>: the reserved input mapping
/// <c>&lt;zeebe:input source="=10" target="taskPriority"/&gt;</c>. The source is a literal or a
/// path over workflow variables, evaluated once when the task is queued. Without the mapping the
/// priority is 0, as in Camunda.
/// </summary>
public static class ExternalTaskPriority
{
    /// <summary>Input-mapping target that sets the priority instead of a worker variable.</summary>
    public const string InputTarget = "taskPriority";

    /// <summary>
    /// Evaluates the priority mapping against the activity's variables. Throws
    /// <see cref="CustomTaskFailedActivityException"/> (code <c>400</c>) when it does not yield a
    /// whole number — no retry can fix that, so the caller fails the activity.
    /// </summary>
    public static long Evaluate(IEnumerable<InputMapping> inputMappings, IDictionary<string, object?> variables)
    {
        var mapping = inputMappings.LastOrDefault(m => m.Target == InputTarget);
        if (mapping is null) return 0;

        return MappingResolver.Resolve(mapping.Source, variables) switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            double d when IsWhole(d) => (long)d,
            decimal m when m == decimal.Truncate(m) && m is >= long.MinValue and <= long.MaxValue => (long)m,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            var other => throw new CustomTaskFailedActivityException("400",
                $"Task priority '{mapping.Source}' evaluated to '{other ?? "null"}', which is not a whole number."),
        };
    }

    private static bool IsWhole(double d) =>
        double.IsFinite(d) && d == Math.Truncate(d) && d >= long.MinValue && d < long.MaxValue;
}
