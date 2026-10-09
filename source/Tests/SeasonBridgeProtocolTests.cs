using System.Buffers.Binary;
using System.Runtime.InteropServices;
using San14Tenkazu.Seasons;
namespace San14Tenkazu.Tests;

public class SeasonBridgeProtocolTests
{
    [Fact]
    public void BootstrapMatchesNativeAbiAndPreservesWidePipeAndRecovery()
    {
        Assert.Equal(56,Marshal.SizeOf<SeasonBridgeProtocol.Recovery>());
        Assert.Equal(448,Marshal.SizeOf<SeasonBridgeProtocol.Bootstrap>());
        var input=new SeasonBridgeProtocol.Bootstrap{Magic=SeasonBridgeProtocol.Magic,Version=SeasonBridgeProtocol.Version,Size=448,Generation=9,
            Module=0x7ff76e7f0000,Context=0x1f700a11b88,Pipe=@"\\.\pipe\san14-seasons-2192-abc",
            Sources=0x12345678000,SourceBytes=(uint)SeasonTreeSources.ByteCount,SceneData=0x98765432000,SceneBytes=49675144,
            Recovery=new(){Magic=SeasonBridgeProtocol.Magic,Version=SeasonBridgeProtocol.Version,Size=56,Generation=9,CallbacksDrained=1}};
        byte[] data=SeasonBridgeProtocol.Bytes(input);
        Assert.Equal(9ul,BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)));
        var output=SeasonBridgeProtocol.Read<SeasonBridgeProtocol.Bootstrap>(data);
        Assert.Equal(input.Pipe,output.Pipe);Assert.Equal(input.Context,output.Context);
        Assert.True(output.Recovery.Valid(9));Assert.False(output.Recovery.Receipt.Confirmed);
        Assert.Equal(input.Sources,BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(416)));
        Assert.Equal(input.SourceBytes,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(424)));
        Assert.Equal(input.SceneData,BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(432)));
        Assert.Equal(input.SceneBytes,BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(440)));
    }
    [Fact]
    public void ReplyRejectsStaleGenerationSequenceAndInvalidCalendar()
    {
        byte[] data=SeasonBridgeProtocol.Encode(SeasonBridgeProtocol.Command.Hello,5,7);
        Array.Resize(ref data,192);BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8),192);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12),0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48),195);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52),4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56),21);
        var reply=SeasonBridgeProtocol.Decode(data,5,7);
        Assert.False(reply.Rendered);Assert.Equal(0u,reply.Capabilities);
        Assert.Throws<InvalidDataException>(()=>SeasonBridgeProtocol.Decode(data,6,7));
        Assert.Throws<InvalidDataException>(()=>SeasonBridgeProtocol.Decode(data,5,8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56),31);
        Assert.Throws<InvalidDataException>(()=>SeasonBridgeProtocol.Decode(data,5,7));
    }
}
