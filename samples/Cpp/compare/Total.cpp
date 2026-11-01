#include <vector>

// The same algorithm as compare/Total.cs, line for line: only the ceremony around the body differs.
// This is the pair the metacode_compare demo runs.
int SumEven(const std::vector<int>& values)
{
    int total = 0;
    for (int value : values)
    {
        if (value % 2 != 0)
        {
            continue;
        }

        total += value;
    }

    return total;
}
