namespace Seeder.Models;

// Shared price-tier catalog, duplicated from TicketingApi.Models so Seeder has no app dependency.
public static class PriceTiers
{
    public static readonly string[] All = ["Economy", "Standard", "Premium", "VIP"];

    private static readonly Dictionary<string, decimal> BasePrices = new()
    {
        ["Economy"] = 35m,
        ["Standard"] = 75m,
        ["Premium"] = 150m,
        ["VIP"] = 350m
    };

    public static decimal GetBasePrice(string tier) =>
        BasePrices.TryGetValue(tier, out var price) ? price : BasePrices["Standard"];
}
