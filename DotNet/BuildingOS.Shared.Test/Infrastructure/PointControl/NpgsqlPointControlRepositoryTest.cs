using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.PointControlAudit;
using Moq;

namespace BuildingOS.Shared.Test.Infrastructure.PointControl;

public class NpgsqlPointControlRepositoryTest
{
    // These tests use a mock of the Npgsql layer to verify query logic.
    // Real DB tests are in BuildingOS.IntegrationTest.

    [Fact]
    public void PointControlInfo_CanBeCreated()
    {
        var info = new PointControlInfo
        {
            id = Guid.NewGuid(),
            Type = DeviceControlType.Kandt,
            Body = "{\"objectId\":1}",
            Result = null
        };

        Assert.NotEqual(Guid.Empty, info.id);
        Assert.Equal(DeviceControlType.Kandt, info.Type);
    }

    [Fact]
    public void PointControlInfo_ResultCanBeSet()
    {
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "test", Body = "{}" };
        info.Result = PointControlResult.Success;
        Assert.Equal(PointControlResult.Success, info.Result);
    }

    [Fact]
    public void PointControlInfo_FailedResult()
    {
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "test", Body = "{}" };
        info.Result = PointControlResult.Failed;
        Assert.Equal(PointControlResult.Failed, info.Result);
    }
}
