namespace San14Tenkazu.Tests;

public class KoreanTests
{
    [Fact]
    public void ANameTakesTheParticleOfItsLastSyllable()
    {
        Assert.Equal("조조는", Korean.Topic("조조"));
        Assert.Equal("하후연은", Korean.Topic("하후연"));
        Assert.Equal("손권과", Korean.And("손권"));
        Assert.Equal("유비와", Korean.And("유비"));
        Assert.Equal("조조 부대가", Korean.Subject("조조 부대"));
        Assert.Equal("금을", Korean.Object("금"));
    }

    [Fact]
    public void ANumberIsReadAsKoreanReadsIt()
    {
        Assert.Equal("3은", Korean.Topic("3"));
        Assert.Equal("2는", Korean.Topic("2"));
        Assert.Equal("10은", Korean.Topic("10"));
        Assert.Equal("1,000은", Korean.Topic("1,000"));
        Assert.Equal("150,000은", Korean.Topic("150,000"));
        Assert.Equal("150,002는", Korean.Topic("150,002"));
        Assert.Equal("장수 #123을", Korean.Object("장수 #123"));
        Assert.Equal("1,000,000,000,000는", Korean.Topic("1,000,000,000,000"));   // 조
    }

    [Fact]
    public void AnythingElseCountsAsEndingInAVowel()
    {
        Assert.Equal("PID는", Korean.Topic("PID"));
        Assert.Equal("는", Korean.Topic(""));
    }
}
