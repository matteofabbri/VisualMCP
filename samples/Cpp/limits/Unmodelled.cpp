#include <string>

// Constructs the pseudocode model deliberately has no node for. Each one is kept in the text as
// RAW[cpp] with a diagnostic instead of being dropped or guessed, which is what the third demo shows.
// Translate this file with metacode_translate and read the diagnostics block.

namespace unmodelled_demo
{
    using namespace std;                  // using-directive     -> MC0006 (no node)

    void walk(int value)
    {
        goto finish;                      // goto                -> MC0004 (no node)
    finish:
        (void)value;
    }

    int size_of_pointer(int* pointer)
    {
        return sizeof(pointer);           // sizeof              -> MC0003 (no node)
    }

    void release(int* pointer)
    {
        delete pointer;                   // delete              -> MC0003 (no node)
    }
}
