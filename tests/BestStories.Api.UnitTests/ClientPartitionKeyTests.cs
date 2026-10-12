using System.Net;
using BestStories.Api.Configuration;

namespace BestStories.Api.UnitTests;

public sealed class ClientPartitionKeyTests
{
    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void GivenAClientAddress_WhenItIsPartitioned_ThenTheExpectedKeyIsUsed(string address, string expected)
    {
        // When
        var key = ApiServiceCollectionExtensions.GetClientPartitionKey(IPAddress.Parse(address));

        // Then
        Assert.Equal(expected, key);
    }

    [Fact]
    public void GivenIPv4ClientsOnADualStackSocket_WhenTheyArePartitioned_ThenEachGetsItsOwnPartition()
    {
        // When
        var first = ApiServiceCollectionExtensions.GetClientPartitionKey(IPAddress.Parse("::ffff:203.0.113.5"));
        var second = ApiServiceCollectionExtensions.GetClientPartitionKey(IPAddress.Parse("::ffff:198.51.100.7"));

        // Then
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GivenNoClientAddress_WhenItIsPartitioned_ThenTheSharedUnknownPartitionIsUsed()
    {
        // When
        var key = ApiServiceCollectionExtensions.GetClientPartitionKey(null);

        // Then
        Assert.Equal("unknown", key);
    }
}
