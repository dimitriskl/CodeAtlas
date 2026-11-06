namespace Sample;

public static class Calculator
{
    public static int Calculate(int value) => value + 1;
    public static string Calculate(string value) => value + "!";
    public static int LocalCaller() => Calculate(1);
}
