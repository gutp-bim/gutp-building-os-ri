using Grpc.Core;
using BuildingOs.ApiServer.Protos;

namespace BuildingOs.ApiServer.Services;

public class GreeterService : Greeter.GreeterBase
{
    private readonly ILogger<GreeterService> _logger;

    public GreeterService(ILogger<GreeterService> logger)
    {
        _logger = logger;
    }

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        _logger.LogInformation("gRPC SayHello called with name: {Name}", request.Name);

        return Task.FromResult(new HelloReply
        {
            Message = $"Hello, {request.Name}!"
        });
    }

    public override async Task Subscribe(
        SubscribeRequest request,
        IServerStreamWriter<NotificationEvent> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("gRPC Subscribe started for client: {ClientName}", request.ClientName);

        var sequence = 0;

        while (!context.CancellationToken.IsCancellationRequested)
        {
            sequence++;

            await responseStream.WriteAsync(new NotificationEvent
            {
                Sequence = sequence,
                Message = $"Notification #{sequence} for {request.ClientName}",
                Timestamp = DateTime.UtcNow.ToString("o")
            });

            _logger.LogInformation("Sent notification #{Sequence} to {ClientName}", sequence, request.ClientName);

            await Task.Delay(2000, context.CancellationToken);
        }
    }
}
