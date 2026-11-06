using Semantic;
namespace SemanticClient;

public static class Calls
{
    /// <summary>See <see cref="Algorithms.Transform(int)"/> and <see cref="Algorithms.Identity{T}(T)"/>.</summary>
    public static void Run()
    {
        _ = Algorithms.Transform(1);
        _ = Algorithms.Transform("one");
        Func<int, int> transform = Algorithms.Transform;
        _ = Algorithms.Identity<int>(2);
        _ = Algorithms.Identity("two");
        _ = Algorithms.Partial(3);
        IWorker abstraction = new Worker();
        _ = abstraction.Work(4);
        _ = new Worker().Work(5);
        _ = new DerivedWorker().Work(6);
        IWorker explicitWorker = new ExplicitWorker();
        _ = explicitWorker.Work(7);
    }
}
