using System.IO;

namespace Semantic;

public interface IAlpha
{
    Stream GetStream();
}

public interface IBeta
{
    Stream GetStream();
}

public interface IGamma
{
    Stream GetStream();
}

public class BetaRunner : IBeta
{
    public Stream GetStream() => Stream.Null;
}

public sealed class UdpStream : BetaRunner, IAlpha
{
}

public sealed class SharedStream : BetaRunner, IAlpha
{
}

public sealed class GammaRunner : IGamma
{
    public Stream GetStream() => Stream.Null;
}
