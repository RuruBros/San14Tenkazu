namespace San14Tenkazu;

// Force tree #77: scripts 8 -> 11 -> 12 (#100/102/110), then title adjustment (#114..118).
// weightedFriendship is ForceInfo kind 5's result, not the raw diplomatic relationship byte.
// likedBias/hatedBias are the loop's variables: #79/80 initialize them once, #105/107 set 30 when matched.
// The caller must carry those variables between candidates; this method does not infer them from this candidate.
// Native answers: source/Tests/AiFriendVectors.json, reproduced by Reverse/ai_friend.py (read-only oracle).
internal static class AiFriendScore
{
    internal static int Evaluate(int ownPower, int otherPower, int weightedFriendship,
        int likedBias, int compatibility, int hatedBias, int titleRank = 0, int rulerFame = 0)
    {
        unchecked
        {
            // The native script multiply/subtract/add instructions wrap at 32 bits. idiv truncates toward zero.
            // The division operator returns 1 for a zero denominator (it does not evaluate the numerator then).
            int ratio = otherPower == 0 ? 1 : (100 * ownPower) / otherPower;
            int result = ratio - weightedFriendship - likedBias + compatibility / 3 + hatedBias;
            if (titleRank > 0 && rulerFame >= 10) result -= 10 - titleRank;
            return result;
        }
    }

    // CGetCompatibilityNode -> 0x2A5E20, valid officers' +0xBE bytes: circular distance on 0..149, capped at 75.
    internal static int Compatibility(byte first, byte second)
    {
        int distance = Math.Abs(first - second);
        return Math.Min(75, Math.Min(distance, 150 - distance));
    }
}
