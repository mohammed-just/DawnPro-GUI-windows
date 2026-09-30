using Moondrop.PhysicalWatchdog;

namespace Moondrop.PhysicalTests;

[TestClass]
public sealed class OfflineTopologyProbeTests
{
    [TestMethod]
    [TestCategory("OfflineTopologyProbe")]
    public Task PublishedRunnerCapturesAuthenticatedParentTopology() =>
        PhysicalOfflineTopologyProbe.RunMtpTestAsync();
}
