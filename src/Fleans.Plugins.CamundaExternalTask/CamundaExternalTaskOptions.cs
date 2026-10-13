namespace Fleans.Plugins.CamundaExternalTask;

public sealed class CamundaExternalTaskOptions
{
    /// <summary>
    /// Camunda 7 incidents. When true, a <c>failure</c> with <c>retries = 0</c> keeps the task as an
    /// incident: the activity keeps waiting, nobody can fetch the task, and an operator resolves it with
    /// <c>PUT /external-task/{id}/retries</c>. When false (the default), the activity fails with error
    /// code <c>500</c>, which an error boundary event can catch.
    /// </summary>
    public bool Incidents { get; set; }
}
