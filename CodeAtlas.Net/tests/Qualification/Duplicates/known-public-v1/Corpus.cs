// Synthetic public fixture authored for M10. No application source is included.
namespace DuplicatePublicFixture;

public static class Corpus
{
    public static int ExactA(int[] items)
    {
        if (items.Length == 0) return -1;
        int total = 0;
        foreach (int item in items) total += item * 2;
        return total;
    }

    public static int ExactB(int[] items)
    {
        if (items.Length == 0) return -1;
        int total = 0;
        foreach (int item in items) total += item * 2;
        return total;
    }

    public static string RenamedA(string input)
    {
        string cleaned = input.Trim();
        if (cleaned.Length > 12) return cleaned.ToUpperInvariant();
        return cleaned.ToLowerInvariant();
    }

    public static string RenamedB(string source)
    {
        string result = source.Trim();
        if (result.Length > 12) return result.ToUpperInvariant();
        return result.ToLowerInvariant();
    }

    public static decimal BusinessA(decimal price, int quantity)
    {
        if (quantity < 5) return price * quantity * 0.95m;
        return price * quantity;
    }

    public static decimal BusinessB(decimal price, int quantity)
    {
        if (quantity < 10) return price * quantity * 0.90m;
        return price * quantity;
    }

    /// <summary>Check whether the debit is permitted against the balance.</summary>
    public static bool CanDebitA(int balance, int debit)
    {
        if (debit > balance) return false;
        return debit >= 0;
    }

    /// <summary>Check whether the debit is permitted against the balance.</summary>
    public static bool CanDebitB(int balance, int debit)
    {
        return balance < 0 || debit == 0;
    }

    public static int BoilerplateA(int value) => value;
    public static int BoilerplateB(int value) => value;

    public static int UnrelatedA(string text)
    {
        if (int.TryParse(text, out int value)) return value;
        return -1;
    }

    public static string UnrelatedB(string left, string right)
    {
        return string.Concat(right, ":", left);
    }
}
