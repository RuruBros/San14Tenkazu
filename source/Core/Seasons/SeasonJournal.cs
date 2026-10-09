using System.Threading.Channels;

namespace San14Tenkazu.Seasons;

internal sealed class SeasonJournal : IAsyncDisposable
{
    private readonly Channel<string> lines=Channel.CreateUnbounded<string>(new UnboundedChannelOptions{SingleReader=true});
    private readonly Task writerTask;
    internal string Path { get; }
    internal SeasonJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        Path=System.IO.Path.Combine(directory,$"season-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        var writer=new StreamWriter(new FileStream(Path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
        writerTask=Task.Run(async()=>
        {
            using(writer)await foreach(string line in lines.Reader.ReadAllAsync())await writer.WriteLineAsync(line);
        });
    }
    internal void Record(string message)
    {
        if(writerTask.IsFaulted)throw new IOException("Season journal write failed",writerTask.Exception);
        if(!lines.Writer.TryWrite($"{DateTime.UtcNow:O} {message}"))throw new IOException("Season journal is closed");
    }
    public async ValueTask DisposeAsync(){lines.Writer.TryComplete();await writerTask.ConfigureAwait(false);}
}
