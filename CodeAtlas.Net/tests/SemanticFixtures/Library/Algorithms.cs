namespace Semantic;

public interface IWorker { int Work(int value); }
public class Worker : IWorker { public virtual int Work(int value) => value; }
public sealed class DerivedWorker : Worker { public override int Work(int value) => value + 1; }
public sealed class ExplicitWorker : IWorker { int IWorker.Work(int value) => value + 2; }

public static partial class Algorithms
{
    public static T Identity<T>(T value) => value;
    public static int Transform(int value) => value + 1;
    public static string Transform(string value) => value.Trim();
    public static partial int Partial(int value);
}
