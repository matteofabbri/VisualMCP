#pragma once

#include <string>
#include <vector>

// A deliberately ordinary C++ library: the point is not the code, it is that every construct in it
// is one the metacode reader models — class and struct, access specifiers, inheritance, a virtual
// method, an overloaded operator, a type alias, a function template, a class template, a range-for
// and a destructor.

namespace catalog
{
    using Money = long long;                 // MetacodeTypeAlias
    using Prices = std::vector<Money>;

    enum class Status                  // MetacodeTypeDeclaration (Enum) with typed members
    {
        Draft,
        Open,
        Paid,
        Cancelled,
    };

    struct Line                        // MetacodeTypeKind.Struct, members public by default
    {
        std::string sku;
        Money unitPrice = 0;
        int quantity = 1;
    };

    class Order                        // MetacodeTypeKind.Class, members private by default
    {
    public:
        explicit Order(std::string customer);
        ~Order();                                     // MetacodeFunctionKind.Destructor

        void add(Line line);
        Money total() const;
        int count() const;

        bool operator==(const Order& other) const;    // CXXOperatorCallExpr -> a binary ==
        const Line& operator[](std::size_t index) const; // -> an index

        virtual std::string describe() const;         // MetacodeModifiers.Virtual

        Status status() const { return status_; }
        void setStatus(Status status) { status_ = status; }

    private:
        std::string customer_;
        std::vector<Line> lines_;
        Status status_ = Status::Draft;
    };

    class PriorityOrder final : public Order      // BaseTypes + virtual inherited
    {
    public:
        PriorityOrder(std::string customer, Money fee);
        std::string describe() const override;

    private:
        Money fee_;
    };

    Money applyDiscount(Money amount, int percent);

    // Function templates carry template parameters in the model rather than becoming opaque.
    template <typename T>
    T clamp(T value, T low, T high)
    {
        if (value < low)
        {
            return low;
        }
        if (value > high)
        {
            return high;
        }
        return value;
    }

    template <typename T>
    class Box
    {
    public:
        explicit Box(T value) : value_(value) {}

        T get() const { return value_; }
        void set(T value) { value_ = value; }

    private:
        T value_;
    };
}
