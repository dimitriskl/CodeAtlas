// Public synthetic cases authored after implementation, before their first detector run.
namespace DuplicatePublicFixture;
public static class Corpus
{
    public static int SquaresA(int[] values)
    {
        int sum = 7;
        foreach (int value in values) { if (value > 0) sum += value * value; }
        return sum - values.Length;
    }
    public static int SquaresB(int[] values)
    {
        int sum = 7;
        foreach (int value in values) { if (value > 0) sum += value * value; }
        return sum - values.Length;
    }
    public static string JoinA(string[] parts)
    {
        string result = "begin";
        foreach (string part in parts) { if (part.Length > 2) result = string.Concat(result, ":", part.Trim()); }
        return result.ToLowerInvariant();
    }
    public static string JoinB(string[] sections)
    {
        string joined = "begin";
        foreach (string section in sections) { if (section.Length > 2) joined = string.Concat(joined, ":", section.Trim()); }
        return joined.ToLowerInvariant();
    }
    public static string PathA(string name)
    {
        string cleaned = name.Trim();
        if (cleaned.Length == 0) return "missing";
        return string.Concat(@"c:\cache\", cleaned.ToLowerInvariant(), ".data");
    }
    public static string PathB(string name)
    {
        string cleaned = name.Trim();
        if (cleaned.Length == 0) return "missing";
        return string.Concat("c:cache", cleaned.ToLowerInvariant(), ".data");
    }
    public static void WriteA(int count)
    {
        int remaining = count + 3;
        while (remaining > 0) { System.Console.Write(remaining); remaining -= 1; }
        System.Console.Write("finished");
    }
    public static void WriteB(int count)
    {
        int remaining = count + 3;
        while (remaining > 0) { System.Console.WriteLine(remaining); remaining -= 1; }
        System.Console.WriteLine("finished");
    }
    public static decimal FeeA(decimal amount)
    {
        decimal fee = amount * 0.03m;
        if (amount > 500m) fee += 8m;
        return System.Math.Round(fee, 2);
    }
    public static decimal FeeB(decimal amount)
    {
        decimal fee = amount * 0.04m;
        if (amount > 700m) fee += 8m;
        return System.Math.Round(fee, 2);
    }
    public static int CheckedA(int value)
    {
        int result = value + 11;
        checked { result *= value; result += 100; }
        return result / 2;
    }
    public static int CheckedB(int value)
    {
        int result = value + 11;
        unchecked { result *= value; result += 100; }
        return result / 2;
    }
    /// <summary>Return whether an order may be released.</summary>
    public static bool ReleaseA(bool approved, int outstanding)
    {
        if (!approved) return false;
        int remaining = outstanding - 2;
        return remaining <= 0 && outstanding >= 0;
    }
    /// <summary>Return whether an order may be released.</summary>
    public static bool ReleaseB(bool approved, int outstanding)
    {
        if (approved) return true;
        int remaining = outstanding + 2;
        return remaining > 0 || outstanding < 0;
    }
    public static int ForwardA(int value) => value;
    public static int ForwardB(int value) => value;
}
