#include "catalog/order.hpp"

#include <iostream>

namespace
{
    bool check(bool condition, const char* what)
    {
        std::cout << (condition ? "ok   " : "FAIL ") << what << '\n';
        return condition;
    }
}

int main()
{
    catalog::Order order("Ada");
    order.add({ "keyboard", 9900, 2 });
    order.add({ "mouse", 2500, 1 });

    bool ok = true;
    ok &= check(order.count() == 2, "two lines");
    ok &= check(order.total() == 22300, "total is the sum of unitPrice * quantity");

    catalog::PriorityOrder urgent("Grace", 500);
    urgent.add({ "monitor", 24900, 1 });
    ok &= check(urgent.describe() == "draft (priority)", "the base describe is reused");

    // A lambda: MetacodeLambda in the model.
    auto cheaperThan = [](const catalog::Line& line, catalog::Money limit)
    {
        return line.unitPrice < limit;
    };
    ok &= check(cheaperThan({ "cable", 1200, 3 }, 2000), "a lambda is callable");

    catalog::Box<int> boxed(41);
    boxed.set(catalog::clamp(boxed.get() + 1, 0, 100));
    ok &= check(boxed.get() == 42, "the class template holds the value");

    std::cout << (ok ? "all checks passed" : "some checks failed") << '\n';
    return ok ? 0 : 1;
}
