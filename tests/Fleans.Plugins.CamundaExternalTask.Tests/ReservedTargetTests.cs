using System.Text.RegularExpressions;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

[TestClass]
public class ReservedTargetTests
{
    /// <summary>The engine's rule for input targets (CustomTaskRoutingResolver), checked at BPMN deploy time.</summary>
    private static readonly Regex EngineTarget = new("^[a-zA-Z_][a-zA-Z0-9_]*$");

    [TestMethod]
    public void Reserved_targets_pass_the_engine_deploy_time_validation()
    {
        Assert.MatchesRegex(EngineTarget, ExternalTaskPriority.InputTarget);
        Assert.MatchesRegex(EngineTarget, ReservedInputs.BusinessKey);
        Assert.MatchesRegex(EngineTarget, ReservedInputs.TenantId);
        Assert.MatchesRegex(EngineTarget, ExternalTaskService.ExtensionPropertyPrefix + "region");
    }
}
