namespace TicketingApi.Repositories;

public sealed record CosmosResult<T>(T Value, double RequestCharge, string QueryScope);

public static class CosmosQueryScopes
{
	public const string ExceptionDataKey = "CosmosQueryScope";
	public const string PointRead = "point-read";
	public const string SinglePartition = "single-partition";
	public const string CrossPartition = "cross-partition";
	public const string NotApplicable = "not-applicable";
}