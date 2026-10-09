using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonTreeSourcesTests
{
    [Fact]
    public void MissingTruncatedAndUnregisteredOriginalsAreRejectedBeforeUpload()
    {
        Assert.Throws<InvalidDataException>(()=>SeasonTreeSources.Validate([]));
        Assert.Throws<InvalidDataException>(()=>SeasonTreeSources.Validate(new byte[SeasonTreeSources.ByteCount-1]));
        Assert.Throws<InvalidDataException>(()=>SeasonTreeSources.Validate(new byte[SeasonTreeSources.ByteCount]));
        Assert.Throws<InvalidDataException>(()=>SeasonTreeSources.Pack(a=>new byte[a.Bytes]));
    }
}
