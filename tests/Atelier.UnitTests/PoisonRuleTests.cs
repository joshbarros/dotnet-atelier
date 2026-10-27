using Atelier.Shared;
using Xunit;

namespace Atelier.UnitTests;

public class PoisonRuleTests
{
    [Theory]
    [InlineData("POISON")]
    [InlineData("ATT-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void Substring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(PoisonRule.IsPoison(reference));

    [Theory]
    [InlineData("ATT-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    [InlineData("")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(PoisonRule.IsPoison(reference));
}
