using System.Reflection;
using Microsoft.OpenApi.Models;

namespace BuildingOs.ApiServer;

public static partial class IServiceCollectionExtension
{
    public static IServiceCollection AddSwagger(this IServiceCollection self)
    {
        self.AddSwaggerGen(options =>
        {
            options.SupportNonNullableReferenceTypes();
            options.SwaggerDoc("building-os", new OpenApiInfo
            {
                Title = "Building OS API",
                Version = "v1",
                Description = "An ASP.NET Core API for Building OS",
            });
            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
        });
        return self;
    }
}