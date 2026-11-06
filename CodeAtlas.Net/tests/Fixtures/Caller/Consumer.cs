using Sample;

namespace Client;

public static class Consumer
{
    public static int CallInt() => Calculator.Calculate(2);
    public static string CallString() => Calculator.Calculate("hello");
    public static Func<int, int> Capture() => Calculator.Calculate;
}
