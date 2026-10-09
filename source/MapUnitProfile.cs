namespace San14Tenkazu;

// CPU preparation/recording costs, separate from GPU completion and WPF presentation.
internal readonly record struct MapUnitProfile(double Layout, double Tokens, double Shields, long Allocated,
    int TokensShown, bool LayoutReused);
