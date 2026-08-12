using TicketingApi.Pagination;

namespace TicketingApi.Tests;

public sealed class ContinuationTokenCodecTests
{
    [Fact]
    public void RoundTripsOpaqueTokenAsUrlSafeText()
    {
        const string original = "[{\"token\":\"a+b/c==\"}]";

        var encoded = ContinuationTokenCodec.Encode(original);

        Assert.NotNull(encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(original, ContinuationTokenCodec.Decode(encoded));
    }

    [Fact]
    public void RejectsMalformedToken()
    {
        Assert.Throws<InvalidContinuationTokenException>(() => ContinuationTokenCodec.Decode("%%%"));
    }
}