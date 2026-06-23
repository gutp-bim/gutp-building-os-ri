namespace BuildingOs.ApiServer.Authorization;

public abstract record TwinGetResult<T>
{
    public sealed record Ok(T Resource) : TwinGetResult<T>;
    public sealed record Forbidden : TwinGetResult<T>;
    public sealed record NotFound : TwinGetResult<T>;
}
