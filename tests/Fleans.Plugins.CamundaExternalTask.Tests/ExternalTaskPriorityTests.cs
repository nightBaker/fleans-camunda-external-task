using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

[TestClass]
public class ExternalTaskPriorityTests
{
    private static readonly Dictionary<string, object?> NoVariables = new();

    [TestMethod]
    public void Without_the_mapping_the_priority_is_zero()
    {
        var priority = ExternalTaskPriority.Evaluate([new InputMapping("=amount", "toCharge")], NoVariables);

        Assert.AreEqual(0L, priority);
    }

    [TestMethod]
    [DataRow("=10", 10L)]
    [DataRow("10", 10L)]
    [DataRow("=-5", -5L)]
    [DataRow("=3.0", 3L)]
    public void Literal_priority(string source, long expected)
    {
        var priority = ExternalTaskPriority.Evaluate([Mapping(source)], NoVariables);

        Assert.AreEqual(expected, priority);
    }

    [TestMethod]
    public void Expression_priority_reads_workflow_variables()
    {
        // Arrange — as the engine hands them over: nested objects are ExpandoObjects.
        var order = new ExpandoObject();
        ((IDictionary<string, object?>)order)["priority"] = 42L;
        var variables = new Dictionary<string, object?> { ["order"] = order, ["urgency"] = 7 };

        // Act + Assert
        Assert.AreEqual(42L, ExternalTaskPriority.Evaluate([Mapping("=order.priority")], variables));
        Assert.AreEqual(7L, ExternalTaskPriority.Evaluate([Mapping("=urgency")], variables));
    }

    [TestMethod]
    public void Whole_numbers_of_any_numeric_type_are_accepted()
    {
        foreach (var value in new object[] { 3, (short)3, (byte)3, (sbyte)3, (ushort)3, 3u, 3UL, 3.0f, 3.0, 3m })
            Assert.AreEqual(3L, ExternalTaskPriority.Evaluate([Mapping("=p")], new Dictionary<string, object?> { ["p"] = value }),
                value.GetType().Name);
    }

    [TestMethod]
    [DataRow("=missing")]
    [DataRow("=\"high\"")]
    [DataRow("=2.5")]
    [DataRow("=true")]
    public void Priority_that_is_not_a_whole_number_fails_with_code_400(string source)
    {
        var ex = Assert.ThrowsExactly<CustomTaskFailedActivityException>(() =>
            ExternalTaskPriority.Evaluate([Mapping(source)], NoVariables));

        Assert.AreEqual("400", ex.GetActivityErrorState().Code);
    }

    private static InputMapping Mapping(string source) => new(source, ExternalTaskPriority.InputTarget);
}
