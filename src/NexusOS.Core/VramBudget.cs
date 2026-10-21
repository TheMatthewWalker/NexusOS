public sealed record InferenceBudget(long TotalVramBytes, long ReserveBytes, long AiBudgetBytes);

public static class InferenceBudgetPlanner
{
    public static InferenceBudget Calculate(long totalVramBytes, int reservePercent)
    {
        if (totalVramBytes < 0 || reservePercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(reservePercent));
        var reserve = totalVramBytes / 100 * reservePercent;
        return new(totalVramBytes, reserve, totalVramBytes - reserve);
    }
}

