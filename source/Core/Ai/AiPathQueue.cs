namespace San14Tenkazu;

// 18AE60: remove a member of the backend's binary queue, keeping all other links.
// -1 represents null. Nonempty inputs require target to be a member of a valid tree.
internal static class AiPathQueue
{
    internal static int Remove(int root, int target, ReadOnlySpan<int> priorities, Span<int> left, Span<int> right)
    {
        if (root == -1) return root;
        int replacement = left[target];
        if (replacement != -1)
        {
            int last = replacement;
            while (right[last] != -1) last = right[last];
            right[last] = right[target];
        }
        else replacement = right[target];
        if (root == target) root = replacement;
        else
        {
            int parent = root;
            while (true)
            {
                Span<int> links = priorities[parent] > priorities[target] ? left : right;
                if (links[parent] == target)
                {
                    links[parent] = replacement;
                    break;
                }
                parent = links[parent];
            }
        }
        left[target] = right[target] = -1;
        return root;
    }
}
