using Oadm.Core.LiveView;

namespace Oadm.Core.Tests.LiveView;

/// <summary>The snapshot is read with a hard cap: a camera that sends more than the limit is not read to its end.</summary>
public sealed class SnapshotReadTests
{
    /// <summary>A stream that never ends and counts what was read from it.</summary>
    private sealed class Endless : Stream
    {
        public long Read1 { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)7, offset, count);
            Read1 += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AnAnswerWithoutAnEndStopsTheReadOneByteOverTheLimit()
    {
        var stream = new Endless();
        using var content = new StreamContent(stream);

        var bytes = await AxisLiveVideoSourceFactory.ReadCappedAsync(content, 100_000, CancellationToken.None);

        Assert.Equal(100_001, bytes.Length); // over the limit: the caller refuses it
        Assert.True(stream.Read1 <= 100_001 + 16 * 1024, $"read {stream.Read1} bytes");
    }

    [Fact]
    public async Task AnAnnouncedTooLargeAnswerIsNotReadAtAll()
    {
        var stream = new Endless();
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = 50_000_000;

        var bytes = await AxisLiveVideoSourceFactory.ReadCappedAsync(content, 100_000, CancellationToken.None);

        Assert.True(bytes.Length > 100_000);
        Assert.Equal(0, stream.Read1);
    }

    [Fact]
    public async Task AnAnswerWithinTheLimitIsReadInFull()
    {
        var picture = new byte[40_000];
        new Random(1).NextBytes(picture);
        using var content = new ByteArrayContent(picture);

        Assert.Equal(picture, await AxisLiveVideoSourceFactory.ReadCappedAsync(content, 100_000, CancellationToken.None));
        using var exact = new ByteArrayContent(picture);
        Assert.Equal(picture, await AxisLiveVideoSourceFactory.ReadCappedAsync(exact, picture.Length, CancellationToken.None));
    }
}
