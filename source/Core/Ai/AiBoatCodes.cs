namespace San14Tenkazu;

// Native 2C4D00 is a signed-int scalar comparison, not a policy or technology lookup.
// The parents choose the force/unit code. No object or enum validity is tested here.
internal static class AiBoatCodes
{
    internal static bool Allowed(int terrain, int code) => terrain != 24
        || unchecked((uint)(code - 12)) <= 1 || unchecked((uint)(code - 17)) <= 1;
}
