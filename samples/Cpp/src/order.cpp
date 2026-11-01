#include "catalog/order.hpp"

#include <stdexcept>
#include <utility>

namespace catalog
{
    Order::Order(std::string customer)
        : customer_(std::move(customer))
    {
    }

    Order::~Order() = default;

    void Order::add(Line line)
    {
        if (line.quantity <= 0)                       // IfStmt
        {
            throw std::invalid_argument("quantity must be positive");   // CXXThrowExpr
        }

        lines_.push_back(std::move(line));            // CXXMemberCallExpr
    }

    Money Order::total() const
    {
        Money sum = 0;
        for (const Line& line : lines_)               // CXXForRangeStmt -> FOREACH
        {
            sum += line.unitPrice * line.quantity;    // CompoundAssignOperator
        }

        return sum;
    }

    int Order::count() const
    {
        return static_cast<int>(lines_.size());       // CXXStaticCastExpr -> a cast
    }

    bool Order::operator==(const Order& other) const
    {
        return customer_ == other.customer_ && total() == other.total();
    }

    const Line& Order::operator[](std::size_t index) const
    {
        return lines_[index];                         // ArraySubscriptExpr -> an index
    }

    std::string Order::describe() const
    {
        switch (status_)                              // SwitchStmt with a default
        {
        case Status::Draft:
            return "draft";
        case Status::Open:
            return "open";
        case Status::Paid:
            return "paid";
        default:
            return "cancelled";
        }
    }

    PriorityOrder::PriorityOrder(std::string customer, Money fee)
        : Order(std::move(customer)), fee_(fee)
    {
    }

    std::string PriorityOrder::describe() const
    {
        try                                           // CXXTryStmt
        {
            if (fee_ < 0)
            {
                throw std::invalid_argument("negative fee");
            }

            return Order::describe() + " (priority)";
        }
        catch (const std::invalid_argument& error)    // CXXCatchStmt, by reference
        {
            return std::string("invalid: ") + error.what();
        }
    }

    Money applyDiscount(Money amount, int percent)
    {
        if (percent < 0 || percent > 100)
        {
            throw std::out_of_range("percent out of range");
        }

        return amount - amount * percent / 100;       // ConditionalOperator is not needed here
    }
}
