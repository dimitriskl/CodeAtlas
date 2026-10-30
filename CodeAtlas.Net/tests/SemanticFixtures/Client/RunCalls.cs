using Semantic;

namespace SemanticClient;

public static class RunCalls
{
    public static void Use(IAlpha alpha, IBeta beta, IGamma gamma)
    {
        alpha.GetStream();
        beta.GetStream();
        new BetaRunner().GetStream();
        new UdpStream().GetStream();
        new SharedStream().GetStream();
        gamma.GetStream();
        new GammaRunner().GetStream();
    }
}
