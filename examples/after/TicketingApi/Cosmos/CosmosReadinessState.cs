namespace TicketingApi.Cosmos;

public sealed class CosmosReadinessState
{
    private volatile bool _schemaReady;
    private volatile bool _changeFeedReady;
    private volatile string? _failure;

    public bool SchemaReady
    {
        get => _schemaReady;
        set => _schemaReady = value;
    }

    public bool ChangeFeedReady
    {
        get => _changeFeedReady;
        set => _changeFeedReady = value;
    }

    public string? Failure
    {
        get => _failure;
        set => _failure = value;
    }

    public bool IsReady => SchemaReady && ChangeFeedReady && Failure is null;
}