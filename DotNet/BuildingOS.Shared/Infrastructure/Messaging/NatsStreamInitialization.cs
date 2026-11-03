using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BuildingOS.Shared.Infrastructure.Messaging;

/// <summary>Ensures a canonical stream without replacing another service's limits or topology.</summary>
public static class NatsStreamInitialization
{
    public static async Task<INatsJSStream> EnsureAsync(
        INatsJSContext js, StreamConfig requested, CancellationToken ct)
    {
        var name = requested.Name ?? throw new ArgumentException("Stream name is required.", nameof(requested));
        INatsJSStream stream;
        try
        {
            stream = await js.GetStreamAsync(name, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404 && ex.Error.ErrCode == 10059)
        {
            try
            {
                stream = await js.CreateStreamAsync(requested, ct).ConfigureAwait(false);
            }
            // JSStreamNameExistErr: another initializer won. One fresh lookup, never an unbounded
            // retry and never a blanket catch for permissions, quotas or invalid configuration.
            catch (NatsJSApiException conflict) when (conflict.Error.ErrCode == 10058)
            {
                stream = await js.GetStreamAsync(name, cancellationToken: ct).ConfigureAwait(false);
            }
        }

        var actual = stream.Info.Config;
        if (actual.Name != requested.Name ||
            !(actual.Subjects ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(requested.Subjects ?? []) ||
            actual.Storage != requested.Storage || actual.Retention != requested.Retention ||
            actual.Discard != requested.Discard || actual.NoAck || actual.Sealed ||
            actual.Mirror is not null || actual.Sources?.Count > 0 || actual.SubjectTransform is not null)
            throw new InvalidOperationException($"Stream '{requested.Name}' has incompatible topology or persistence settings.");

        return stream;
    }
}
