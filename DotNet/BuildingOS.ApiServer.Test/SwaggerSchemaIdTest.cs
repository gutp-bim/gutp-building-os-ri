using BuildingOs.ApiServer;
using BuildingOs.ApiServer.Controllers;

namespace BuildingOS.ApiServer.Test;

public class SwaggerSchemaIdTest
{
    [Fact]
    public void For_NestedTypesWithSameNameInDifferentControllers_ProduceDistinctIds()
    {
        var usersId = SwaggerSchemaId.For(typeof(UsersController.SetEnabledRequest));
        var oidcId = SwaggerSchemaId.For(typeof(OidcClientsController.SetEnabledRequest));

        Assert.NotEqual(usersId, oidcId);
        Assert.Equal("UsersControllerSetEnabledRequest", usersId);
        Assert.Equal("OidcClientsControllerSetEnabledRequest", oidcId);
    }

    [Fact]
    public void For_TopLevelType_UsesPlainName()
    {
        Assert.Equal("String", SwaggerSchemaId.For(typeof(string)));
    }
}
