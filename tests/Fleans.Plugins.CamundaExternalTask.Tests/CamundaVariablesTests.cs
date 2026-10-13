using System.Dynamic;
using System.Text.Json;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

[TestClass]
public class CamundaVariablesTests
{
    [TestMethod]
    [DataRow(42L, "Integer")]
    [DataRow(5_000_000_000L, "Long")]
    [DataRow(1.5, "Double")]
    [DataRow("x", "String")]
    [DataRow(true, "Boolean")]
    [DataRow(null, "Null")]
    public void ToCamunda_picks_the_camunda_type(object? value, string expectedType) =>
        Assert.AreEqual(expectedType, CamundaVariables.ToCamunda(value).Type);

    [TestMethod]
    public void ToCamunda_serializes_objects_and_lists_as_json()
    {
        // Arrange
        var order = new ExpandoObject();
        ((IDictionary<string, object?>)order)["lines"] = new List<object?> { 1L, "two" };

        // Act
        var typed = CamundaVariables.ToCamunda(order);

        // Assert
        Assert.AreEqual("Json", typed.Type);
        Assert.AreEqual("{\"lines\":[1,\"two\"]}", typed.Value);
    }

    [TestMethod]
    public void ToFleans_infers_untyped_values_from_json()
    {
        // Arrange
        var element = JsonSerializer.SerializeToElement(new { a = 1, b = new[] { true } });

        // Act
        var value = (IDictionary<string, object?>)CamundaVariables.ToFleans("v", new VariableValueDto(element, null, null))!;

        // Assert
        Assert.AreEqual(1L, value["a"]);
        CollectionAssert.AreEqual(new List<object?> { true }, (List<object?>)value["b"]!);
    }

    [TestMethod]
    public void ToFleans_rejects_java_serialized_objects()
    {
        var typed = new VariableValueDto(JsonSerializer.SerializeToElement("rO0AB"), "Object",
            new Dictionary<string, object?> { ["serializationDataFormat"] = "application/x-java-serialized-object" });

        Assert.ThrowsExactly<CamundaVariables.UnsupportedVariableException>(() => CamundaVariables.ToFleans("v", typed));
    }

    [TestMethod]
    public void Bytes_round_trip_as_base64()
    {
        var stored = CamundaVariables.ToFleans("v", new VariableValueDto(JsonSerializer.SerializeToElement("AQID"), "Bytes", null));

        var typed = CamundaVariables.ToCamunda(stored);

        Assert.AreEqual("Bytes", typed.Type);
        Assert.AreEqual("AQID", typed.Value);
    }

    [TestMethod]
    public void File_keeps_its_metadata_and_is_returned_without_inline_content()
    {
        // Arrange — what camunda-external-task-client sends for a File.
        var stored = CamundaVariables.ToFleans("v", new VariableValueDto(JsonSerializer.SerializeToElement("aGVsbG8="), "File",
            new Dictionary<string, object?>
            {
                ["filename"] = JsonSerializer.SerializeToElement("hello.txt"),
                ["mimetype"] = JsonSerializer.SerializeToElement("text/plain"),
                ["encoding"] = JsonSerializer.SerializeToElement("utf-8"),
            }));

        // Act
        var typed = CamundaVariables.ToCamunda(stored);

        // Assert
        Assert.IsTrue(CamundaVariables.TryGetBinary(stored, out var binary));
        Assert.AreEqual("aGVsbG8=", binary.Base64);
        Assert.AreEqual("File", typed.Type);
        Assert.IsNull(typed.Value, "As in Camunda, clients download file content separately.");
        Assert.AreEqual("hello.txt", typed.ValueInfo!["filename"]);
        Assert.AreEqual("text/plain", typed.ValueInfo["mimetype"]);
        Assert.AreEqual("utf-8", typed.ValueInfo["encoding"]);
    }

    [TestMethod]
    public void Bytes_that_are_not_base64_are_rejected() =>
        Assert.ThrowsExactly<FormatException>(() =>
            CamundaVariables.ToFleans("v", new VariableValueDto(JsonSerializer.SerializeToElement("not base64!"), "Bytes", null)));

    [TestMethod]
    public void FormatDate_converts_local_times_to_utc()
    {
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, 89, DateTimeKind.Utc);

        Assert.AreEqual(CamundaVariables.FormatDate(utc), CamundaVariables.FormatDate(utc.ToLocalTime()));
    }

    [TestMethod]
    public void FormatDate_uses_the_camunda_rest_format() =>
        Assert.AreEqual("2026-03-04T05:06:07.089+0000",
            CamundaVariables.FormatDate(new DateTime(2026, 3, 4, 5, 6, 7, 89, DateTimeKind.Utc)));
}
