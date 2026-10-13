using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.Grains;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

/// <summary>
/// The engine side of the plugin in a real Orleans cluster: the engine publishes
/// <see cref="ExecuteCustomTaskEvent"/> on <c>events.ExecuteCustomTaskEvent.camunda:&lt;topic&gt;</c>,
/// the regex-subscribed <see cref="CamundaExternalTaskHandler"/> queues it, and worker reports
/// reach <see cref="IWorkflowInstanceCallback"/>. The workflow instance is a fake grain.
/// </summary>
[TestClass]
[DoNotParallelize]
public class StreamHandlerTests
{
    private const string ConnectionString = "DataSource=file:stream-handler-tests?mode=memory&cache=shared";
    private static SqliteConnection _keepAlive = null!;
    private static TestCluster _cluster = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        builder.AddClientBuilderConfigurator<ClientConfigurator>();
        _cluster = builder.Build();
        _cluster.Deploy();
    }

    [ClassCleanup]
    public static void Stop()
    {
        _cluster.StopAllSilos();
        _keepAlive.Dispose();
    }

    private static ExternalTaskService Service =>
        ((InProcessSiloHandle)_cluster.Primary).SiloHost.Services.GetRequiredService<ExternalTaskService>();

    [TestMethod]
    public async Task Published_task_is_queued_fetched_and_completed_back_into_the_workflow()
    {
        // Arrange — an active activity whose scope holds amount=42.
        var (instanceId, evt) = NewTask("charge-card", inputs: [new InputMapping("=amount", "toCharge")]);
        FakeWorkflowInstance.Activate(evt.ActivityInstanceId, ("amount", 42L));

        // Act
        await Publish(evt);
        var task = (await Service.FetchAndLock(Fetch("charge-card"), default)).Single();
        await Service.Complete(task.Id, new CompleteExternalTaskDto("w1",
            new Dictionary<string, VariableValueDto> { ["charged"] = new(true, "Boolean", null) }, null));

        // Assert
        Assert.AreEqual(instanceId.ToString("D"), task.ProcessInstanceId);
        Assert.AreEqual("charge-card", task.TopicName);
        Assert.AreEqual(42, task.Variables["toCharge"].Value);
        Assert.Contains($"complete:{evt.ActivityId}:charged=True", FakeWorkflowInstance.Calls);
        Assert.AreEqual(0, await RowCount(instanceId));
    }

    [TestMethod]
    public async Task BpmnError_fails_the_activity_with_its_error_code()
    {
        // Arrange
        var (_, evt) = NewTask("check-fraud");
        FakeWorkflowInstance.Activate(evt.ActivityInstanceId);
        await Publish(evt);
        var task = (await Service.FetchAndLock(Fetch("check-fraud"), default)).Single();

        // Act
        await Service.HandleBpmnError(task.Id, new ExternalTaskBpmnErrorDto("w1", "FRAUD", "fishy", null));

        // Assert
        Assert.Contains($"fail:{evt.ActivityId}:FRAUD", FakeWorkflowInstance.Calls);
    }

    [TestMethod]
    public async Task Event_for_an_activity_that_already_finished_is_not_queued()
    {
        // Arrange — redelivery after the activity completed (the fake reports it inactive).
        var (instanceId, evt) = NewTask("charge-card");

        // Act
        await Publish(evt);
        await Task.Delay(1000); // nothing observable to wait for — give the stream time to deliver

        // Assert
        Assert.AreEqual(0, await RowCount(instanceId));
        Assert.IsTrue(FakeWorkflowInstance.Checked.Contains(evt.ActivityInstanceId), "The event reached the handler.");
    }

    [TestMethod]
    public async Task Duplicate_delivery_of_a_waiting_task_keeps_a_single_row()
    {
        // Arrange
        var (instanceId, evt) = NewTask("charge-card");
        FakeWorkflowInstance.Activate(evt.ActivityInstanceId);

        // Act
        await Publish(evt);
        await Publish(evt);

        // Assert
        await WaitUntil(async () => await RowCount(instanceId) == 1 && FakeWorkflowInstance.CheckCount(evt.ActivityInstanceId) == 2);
        Assert.AreEqual(1, await RowCount(instanceId));
    }

    [TestMethod]
    public async Task Priority_mapping_is_evaluated_when_the_task_is_queued()
    {
        // Arrange — one literal, one expression over the activity's variables.
        var (literalInstance, literal) = NewTask("ship-order", inputs: [new InputMapping("=10", ExternalTaskPriority.InputTarget)]);
        var (expressionInstance, expression) = NewTask("ship-order",
            inputs: [new InputMapping("=order.priority", ExternalTaskPriority.InputTarget)]);
        FakeWorkflowInstance.Activate(literal.ActivityInstanceId);
        FakeWorkflowInstance.Activate(expression.ActivityInstanceId,
            ("order", new Dictionary<string, object?> { ["priority"] = 25L }));

        // Act
        await Publish(literal);
        await Publish(expression);
        await WaitUntil(async () => await RowCount(literalInstance) == 1 && await RowCount(expressionInstance) == 1);
        var tasks = await Service.FetchAndLock(
            Fetch("ship-order") with { MaxTasks = 2, UsePriority = true }, default);

        // Assert
        CollectionAssert.AreEqual(new long[] { 25, 10 }, tasks.Select(t => t.Priority).ToArray());
        CollectionAssert.AreEqual(new[] { expressionInstance.ToString("D"), literalInstance.ToString("D") },
            tasks.Select(t => t.ProcessInstanceId).ToArray());
    }

    [TestMethod]
    public async Task Business_key_and_tenant_id_are_evaluated_when_the_task_is_queued()
    {
        // Arrange
        var (instanceId, evt) = NewTask("bill-order", inputs:
        [
            new InputMapping("=order.id", ReservedInputs.BusinessKey),
            new InputMapping("acme", ReservedInputs.TenantId),
        ]);
        FakeWorkflowInstance.Activate(evt.ActivityInstanceId, ("order", new Dictionary<string, object?> { ["id"] = 1001L }));

        // Act
        await Publish(evt);
        await WaitUntil(async () => await RowCount(instanceId) == 1);
        var task = (await Service.FetchAndLock(Fetch("bill-order"), default)).Single();

        // Assert
        Assert.AreEqual("1001", task.BusinessKey);
        Assert.AreEqual("acme", task.TenantId);
    }

    [TestMethod]
    public async Task Priority_that_is_not_a_number_fails_the_activity()
    {
        // Arrange
        var (instanceId, evt) = NewTask("ship-order", inputs: [new InputMapping("=label", ExternalTaskPriority.InputTarget)]);
        FakeWorkflowInstance.Activate(evt.ActivityInstanceId, ("label", "urgent"));

        // Act
        await Publish(evt);
        await WaitUntil(() => Task.FromResult(FakeWorkflowInstance.Calls.Contains($"fail:{evt.ActivityId}:400")));

        // Assert
        Assert.Contains($"fail:{evt.ActivityId}:400", FakeWorkflowInstance.Calls);
        Assert.AreEqual(0, await RowCount(instanceId));
    }

    private static (Guid InstanceId, ExecuteCustomTaskEvent Event) NewTask(string topic, List<InputMapping>? inputs = null)
    {
        var instanceId = Guid.NewGuid();
        return (instanceId, new ExecuteCustomTaskEvent(
            WorkflowInstanceId: instanceId,
            WorkflowId: "order-process",
            ProcessDefinitionId: "order-process:1",
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: "task-" + topic,
            TaskType: CamundaExternalTaskConventions.TaskTypeFor(topic),
            InputMappings: inputs ?? new List<InputMapping>(),
            OutputMappings: new List<OutputMapping>(),
            VariablesId: Guid.NewGuid()));
    }

    /// <summary>Publishes exactly as the engine's WorkflowEventsPublisher does.</summary>
    private static async Task Publish(ExecuteCustomTaskEvent evt)
    {
        var stream = _cluster.Client.GetStreamProvider(WorkflowEventStreams.StreamProvider)
            .GetStream<ExecuteCustomTaskEvent>(StreamId.Create(
                WorkflowEventStreams.GetExecuteCustomTaskNamespace(evt.TaskType),
                evt.WorkflowInstanceId.ToString("D")));
        await stream.OnNextAsync(evt);
    }

    private static async Task<int> RowCount(Guid instanceId)
    {
        await using var db = await ((InProcessSiloHandle)_cluster.Primary).SiloHost.Services
            .GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>().CreateDbContextAsync();
        return await db.ExternalTasks.CountAsync(t => t.WorkflowInstanceId == instanceId);
    }

    private static FetchExternalTasksDto Fetch(string topic) =>
        new("w1", MaxTasks: 1, AsyncResponseTimeout: 10_000,
            [new FetchExternalTaskTopicDto(topic, 60_000, null, false, null, null, null, null)]);

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder silo) => silo
            .AddMemoryStreams(WorkflowEventStreams.StreamProvider)
            .AddMemoryGrainStorage("PubSubStore")
            .ConfigureServices(services =>
                services.AddCamundaExternalTasks(o => o.UseSqlite(ConnectionString)));
    }

    private sealed class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration, IClientBuilder client) =>
            client.AddMemoryStreams(WorkflowEventStreams.StreamProvider);
    }
}

/// <summary>Stands in for the engine's workflow instance grain.</summary>
public sealed class FakeWorkflowInstance : Grain, IWorkflowInstanceCallback
{
    private static readonly ConcurrentDictionary<Guid, ExpandoObject> ActiveActivities = new();
    private static readonly ConcurrentDictionary<Guid, int> Checks = new();
    public static readonly ConcurrentBag<string> Calls = [];

    public static IReadOnlyCollection<Guid> Checked => Checks.Keys.ToList();

    public static int CheckCount(Guid activityInstanceId) => Checks.GetValueOrDefault(activityInstanceId);

    public static void Activate(Guid activityInstanceId, params (string Name, object? Value)[] variables)
    {
        var scope = new ExpandoObject();
        foreach (var (name, value) in variables)
            ((IDictionary<string, object?>)scope)[name] = value;
        ActiveActivities[activityInstanceId] = scope;
    }

    public ValueTask<ExpandoObject> GetVariables(Guid variablesStateId) => ValueTask.FromResult(new ExpandoObject());

    public ValueTask<ExpandoObject?> GetVariablesIfActive(Guid activityInstanceId, Guid variablesStateId)
    {
        Checks.AddOrUpdate(activityInstanceId, 1, (_, n) => n + 1);
        return ValueTask.FromResult(ActiveActivities.GetValueOrDefault(activityInstanceId));
    }

    public Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables)
    {
        ActiveActivities.TryRemove(activityInstanceId, out _);
        var values = string.Join(",", ((IDictionary<string, object?>)variables).Select(kv => $"{kv.Key}={kv.Value}"));
        Calls.Add($"complete:{activityId}:{values}");
        return Task.CompletedTask;
    }

    public Task FailActivity(string activityId, Guid activityInstanceId, Exception exception)
    {
        ActiveActivities.TryRemove(activityInstanceId, out _);
        var code = exception is ActivityException a ? a.GetActivityErrorState().Code : "?";
        Calls.Add($"fail:{activityId}:{code}");
        return Task.CompletedTask;
    }
}
