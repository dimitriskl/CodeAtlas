namespace ExplanationQuality;

public static class Calculator
{
    public static int Process(int value)
    {
        int adjusted = value + 7;
        return adjusted;
    }

    /// <summary>Trims surrounding whitespace and converts the text to invariant uppercase.</summary>
    public static string Process(string value)
    {
        return value.Trim().ToUpperInvariant();
    }
}

public static class Ledger
{
    /// <summary>Durably commits a payment inside an atomic database transaction.</summary>
    public static int Commit(int amount)
    {
        int marker = amount + 1;
        return marker;
    }
}

public static class Dispatcher
{
    public static int Send(string payload)
    {
        return Transport.Deliver(payload);
    }
}

public static class NoOp
{
    public static void Ping()
    {
    }
}

public static class Pipeline
{
    public static System.Action GetCallback()
    {
        return NoOp.Ping;
    }
}
