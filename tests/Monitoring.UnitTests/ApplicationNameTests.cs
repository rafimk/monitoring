using Microsoft.Extensions.Options;
using Monitoring.Worker;

namespace Monitoring.UnitTests;

public class ApplicationNameTests
{
    private static bool Valid(string? name) => new WorkerOptionsValidator().Validate(null, new WorkerOptions { ApplicationName = name! }).Succeeded;

    [Fact]
    public void Default_IsALocalDevelopmentPlaceholder_AndValid() =>
        Assert.Equal("monitoring-local", new WorkerOptions().ApplicationName);

    [Theory]
    [InlineData("Billing", true)]
    [InlineData("billing.api-v2_prod", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    public void ApplicationName_IsRequiredAndSimple(string? name, bool valid) => Assert.Equal(valid, Valid(name));

    [Fact]
    public void ApplicationName_IsLimitedTo80Characters()
    {
        Assert.True(Valid(new string('a', 80)));
        Assert.False(Valid(new string('a', 81)));
    }

    [Fact]
    public void ThreeConcepts_AreDistinct_ApplicationStableNameAndInstanceId()
    {
        var a = new WorkerIdentity(Options.Create(new WorkerOptions { ApplicationName = "Billing", WorkerId = "billing-node" }));
        var b = new WorkerIdentity(Options.Create(new WorkerOptions { ApplicationName = "Billing", WorkerId = "billing-node" }));
        Assert.Equal("Billing", a.ApplicationName);
        Assert.Equal("billing-node", a.Name);
        Assert.StartsWith("billing-node-", a.Id);
        Assert.NotEqual(a.Id, b.Id);            // a restart is a new instance
        Assert.Equal(a.Name, b.Name);           // with the same stable name
        Assert.Equal(a.ApplicationName, b.ApplicationName);
    }

    [Fact]
    public void StableName_DefaultsToTheMachineName_NotToAProcessId() =>
        Assert.Equal(Environment.MachineName, new WorkerIdentity(Options.Create(new WorkerOptions())).Name);

    [Fact]
    public void ApplicationName_IsTrimmed() =>
        Assert.Equal("Billing", new WorkerIdentity(Options.Create(new WorkerOptions { ApplicationName = " Billing " })).ApplicationName);
}
