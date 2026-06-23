using BuildingOS.Shared.Infrastructure.ControlRouting;

namespace BuildingOS.Shared.Test.Infrastructure.ControlRouting;

public class EgressSubjectsTest
{
    [Fact]
    public void PerGatewayRequest_BuildsSubjectUnderControlRequestSpace()
    {
        Assert.Equal("building-os.control.request.gw.gw-sim-1", EgressSubjects.PerGatewayRequest("gw-sim-1"));
    }

    [Fact]
    public void Result_BuildsResultSubject_ConsumedByWaitForResult()
    {
        Assert.Equal("building-os.control.result.abc-123", EgressSubjects.Result("abc-123"));
    }

    [Fact]
    public void GenericRequest_IsTheSharedHandlerSubject()
    {
        Assert.Equal("building-os.control.request", EgressSubjects.GenericRequest);
    }
}
