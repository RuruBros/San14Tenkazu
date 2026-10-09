using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public class SeasonNativeBundleTests
{
    [Theory]
    [InlineData("SeasonBridge.dll")][InlineData("build.json")][InlineData("sources/SeasonTreeColorGpu.h")]
    public void PackagedBuildRejectsChangedDllManifestAndSourceWithoutOverwriting(string file)
    {
        string root=Path.Combine(Path.GetTempPath(),"san14-season-bundle-"+Guid.NewGuid().ToString("N"));
        try
        {
            string directory=SeasonNativeBundle.Extract(root);SeasonNativeBundle.VerifyDirectory(directory);
            Assert.Equal(directory,SeasonNativeBundle.Extract(root));
            string path=Path.Combine(directory,file);byte[] bytes=File.ReadAllBytes(path);bytes[0]^=1;File.WriteAllBytes(path,bytes);
            Assert.Throws<InvalidDataException>(()=>SeasonNativeBundle.VerifyDirectory(directory));
            Assert.Throws<InvalidDataException>(()=>SeasonNativeBundle.Extract(root));Assert.Equal(bytes,File.ReadAllBytes(path));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
