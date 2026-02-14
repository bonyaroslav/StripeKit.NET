using System.Collections.Generic;

namespace StripeKit.Tests;

public class StripeMetadataMapperTests
{
    [Fact]
    public void CreateForUser_ValidUserId_SetsUserIdMetadata()
    {
        IReadOnlyDictionary<string, string> metadata = StripeMetadataMapper.CreateForUser("user_123");

        Assert.True(metadata.ContainsKey("user_id"));
        Assert.Equal("user_123", metadata["user_id"]);
    }

    [Fact]
    public void TryGetUserId_MissingMetadata_ReturnsFalse()
    {
        bool found = StripeMetadataMapper.TryGetUserId(null, out string userId);

        Assert.False(found);
        Assert.Equal(string.Empty, userId);
    }

    [Fact]
    public void TryGetUserId_Present_ReturnsTrue()
    {
        Dictionary<string, string> metadata = new Dictionary<string, string>
        {
            ["user_id"] = "user_456"
        };

        bool found = StripeMetadataMapper.TryGetUserId(metadata, out string userId);

        Assert.True(found);
        Assert.Equal("user_456", userId);
    }

    [Fact]
    public void CreateForUser_EmptyUserId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => StripeMetadataMapper.CreateForUser(""));
    }
}
