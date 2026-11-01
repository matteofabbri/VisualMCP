namespace Compare;

/// <summary>The same algorithm as compare/Total.cpp — the other side of the metacode_compare demo.</summary>
public static class Total
{
    public static int SumEven(int[] values)
    {
        var total = 0;
        foreach (var value in values)
        {
            if (value % 2 != 0)
            {
                continue;
            }

            total += value;
        }

        return total;
    }
}
