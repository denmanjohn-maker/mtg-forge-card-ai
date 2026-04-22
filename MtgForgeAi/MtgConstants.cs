namespace MtgForgeAi;

internal static class MtgConstants
{
    internal static readonly HashSet<string> BasicLandNames = new(
        ["Plains", "Island", "Swamp", "Mountain", "Forest", "Wastes"],
        StringComparer.OrdinalIgnoreCase);
}
