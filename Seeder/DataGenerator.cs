using Seeder.Models;

namespace Seeder;

// All fictional/generated content below - no real brand, team, or show names are used.
public static class DataGenerator
{
    public const int EventCount = 5_000;
    public const int OrderCount = 250_000;
    public const int CustomerCount = 25_000;
    public const int TopEventCount = 50;
    public const double TopEventShare = 0.60;
    public const int ChampionshipOrderCount = 42_000;
    public const string ChampionshipEventId = "event-00001";

    public static readonly string[] Cities =
    [
        "New York", "Los Angeles", "Chicago", "Houston", "Phoenix", "Philadelphia", "San Antonio",
        "San Diego", "Dallas", "Austin", "Jacksonville", "Fort Worth", "Columbus", "Charlotte",
        "San Francisco", "Indianapolis", "Seattle", "Denver", "Washington", "Boston", "Nashville",
        "Oklahoma City", "El Paso", "Portland", "Las Vegas", "Detroit", "Memphis", "Louisville",
        "Baltimore", "Milwaukee", "Albuquerque", "Tucson", "Fresno", "Sacramento", "Kansas City",
        "Atlanta", "Miami", "Raleigh", "Omaha", "Minneapolis"
    ];

    private static readonly string[] Adjectives =
    [
        "Midnight", "Electric", "Golden", "Neon", "Crimson", "Velvet", "Wild", "Silver", "Rebel",
        "Solar", "Scarlet", "Iron", "Amber", "Northern", "Emerald"
    ];

    private static readonly string[] Nouns =
    [
        "Echoes", "Horizon", "Nights", "Dreams", "Fire", "Wolves", "Storm", "Voyage", "Legends",
        "Pulse", "Reverie", "Skyline", "Tides", "Embers", "Wavelength"
    ];

    private static readonly string[] Mascots =
    [
        "Hawks", "Wolves", "Titans", "Comets", "Rangers", "Vipers", "Bears", "Falcons", "Sharks",
        "Knights", "Panthers", "Miners", "Storm", "Raptors", "Foxes"
    ];

    private static readonly string[] ConferenceTopics =
    [
        "AI", "Cloud", "DevOps", "Data", "Security", "Product", "Marketing", "FinTech",
        "HealthTech", "Design"
    ];

    private static readonly string[] OrderStatuses = ["confirmed", "cancelled", "refunded"];
    private static readonly double[] OrderStatusWeights = [0.85, 0.10, 0.05];

    public static List<TicketEvent> GenerateEvents(Random random)
    {
        var now = DateTime.UtcNow;
        var events = new List<TicketEvent>(EventCount);

        for (var i = 0; i < EventCount; i++)
        {
            var city = Cities[random.Next(Cities.Length)];
            var (name, venue) = GenerateNameAndVenue(city, random);
            var totalSeats = RandomSeatCount(random);

            events.Add(new TicketEvent
            {
                Id = $"event-{i + 1:D5}",
                Name = name,
                Venue = venue,
                City = city,
                EventDate = RandomEventDate(now, random),
                TotalSeats = totalSeats,
                AvailableSeats = totalSeats,
                PriceTier = PriceTiers.All[random.Next(PriceTiers.All.Length)]
            });
        }

        // Force the designated hot-partition event so it's easy to find and query against.
        var championship = events[0];
        championship.Name = "Championship Final";
        championship.Venue = $"{championship.City} Stadium";
        championship.TotalSeats = 60_000;
        championship.AvailableSeats = 60_000;
        championship.PriceTier = "VIP";

        return events;
    }

    // Assigns an order count to every event (sum == OrderCount) with the requested skew:
    // event[0] (the championship final) gets ChampionshionOrderCount alone, the next
    // TopEventCount-1 events share the rest of the top-event bucket via a Zipf-like weighting,
    // and the long tail of remaining events splits what's left with mild random variance.
    public static int[] ComputeOrderDistribution(Random random)
    {
        var counts = new int[EventCount];
        counts[0] = ChampionshipOrderCount;

        var topBucketTotal = (int)(OrderCount * TopEventShare) - ChampionshipOrderCount;
        var topRanks = TopEventCount - 1;
        var topWeights = new double[topRanks];
        double topWeightSum = 0;
        for (var r = 0; r < topRanks; r++)
        {
            topWeights[r] = 1.0 / (r + 2);
            topWeightSum += topWeights[r];
        }

        var topAssigned = 0;
        for (var r = 0; r < topRanks; r++)
        {
            var share = (int)Math.Round(topBucketTotal * (topWeights[r] / topWeightSum));
            counts[r + 1] = share;
            topAssigned += share;
        }
        counts[topRanks] += topBucketTotal - topAssigned; // absorb rounding drift

        var tailTotal = OrderCount - ChampionshipOrderCount - topBucketTotal;
        var tailCount = EventCount - TopEventCount;
        var tailWeights = new double[tailCount];
        double tailWeightSum = 0;
        for (var i = 0; i < tailCount; i++)
        {
            tailWeights[i] = 0.5 + random.NextDouble();
            tailWeightSum += tailWeights[i];
        }

        var tailAssigned = 0;
        for (var i = 0; i < tailCount; i++)
        {
            var share = (int)Math.Round(tailTotal * (tailWeights[i] / tailWeightSum));
            counts[TopEventCount + i] = share;
            tailAssigned += share;
        }
        counts[EventCount - 1] += tailTotal - tailAssigned; // absorb rounding drift

        return counts;
    }

    public static IEnumerable<Order> GenerateOrders(IReadOnlyList<TicketEvent> events, int[] orderCounts, Random random)
    {
        var orderNumber = 0L;
        var now = DateTime.UtcNow;

        for (var e = 0; e < events.Count; e++)
        {
            var ticketEvent = events[e];
            var count = orderCounts[e];

            for (var i = 0; i < count; i++)
            {
                orderNumber++;
                var quantity = random.Next(1, 9); // 1-8 tickets
                var basePrice = PriceTiers.GetBasePrice(ticketEvent.PriceTier);
                var priceVariance = 0.9 + (random.NextDouble() * 0.2); // +/-10% realistic variance

                yield return new Order
                {
                    Id = $"order-{orderNumber:D6}",
                    EventId = ticketEvent.Id,
                    CustomerId = $"customer-{random.Next(1, CustomerCount + 1):D5}",
                    Quantity = quantity,
                    PriceTier = ticketEvent.PriceTier,
                    TotalPrice = Math.Round(quantity * basePrice * (decimal)priceVariance, 2),
                    Status = RandomStatus(random),
                    OrderDate = RandomPurchaseTimestamp(now, random)
                };
            }
        }
    }

    private static (string Name, string Venue) GenerateNameAndVenue(string city, Random random)
    {
        var category = random.NextDouble();

        if (category < 0.40)
        {
            var artist = $"{Adjectives[random.Next(Adjectives.Length)]} {Nouns[random.Next(Nouns.Length)]}";
            var format = random.Next(3);
            var name = format switch
            {
                0 => $"{artist} World Tour",
                1 => $"{artist} Live",
                _ => $"{artist}: {city} Night"
            };
            return (name, $"{city} Arena");
        }

        if (category < 0.65)
        {
            var homeMascot = Mascots[random.Next(Mascots.Length)];
            var awayCity = Cities[random.Next(Cities.Length)];
            var awayMascot = Mascots[random.Next(Mascots.Length)];
            return ($"{city} {homeMascot} vs {awayCity} {awayMascot}", $"{city} Stadium");
        }

        if (category < 0.85)
        {
            var adjective = Adjectives[random.Next(Adjectives.Length)];
            var noun = Nouns[random.Next(Nouns.Length)];
            var format = random.Next(2);
            var name = format == 0 ? $"The {adjective} {noun}: A New Musical" : $"{adjective} {noun} - The Musical";
            return (name, $"{city} Theater");
        }

        var topic = ConferenceTopics[random.Next(ConferenceTopics.Length)];
        var year = DateTime.UtcNow.Year + random.Next(0, 2);
        return ($"{topic} Summit {year}", $"{city} Convention Center");
    }

    private static int RandomSeatCount(Random random)
    {
        var roll = random.NextDouble();
        return roll switch
        {
            < 0.7 => random.Next(200, 5_000),
            < 0.9 => random.Next(5_000, 20_000),
            _ => random.Next(20_000, 60_001)
        };
    }

    private static DateTime RandomEventDate(DateTime now, Random random)
    {
        var start = now.AddDays(1);
        var end = now.AddMonths(18);
        var minutes = random.NextDouble() * (end - start).TotalMinutes;
        var date = start.AddMinutes(minutes);
        var hour = 12 + random.Next(0, 10); // 12:00 - 21:00 local show times
        return new DateTime(date.Year, date.Month, date.Day, hour, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime RandomPurchaseTimestamp(DateTime now, Random random)
    {
        var earliest = now.AddDays(-180);
        var minutes = random.NextDouble() * (now - earliest).TotalMinutes;
        return earliest.AddMinutes(minutes);
    }

    private static string RandomStatus(Random random)
    {
        var roll = random.NextDouble();
        double cumulative = 0;
        for (var i = 0; i < OrderStatuses.Length; i++)
        {
            cumulative += OrderStatusWeights[i];
            if (roll < cumulative)
            {
                return OrderStatuses[i];
            }
        }

        return OrderStatuses[^1];
    }
}
