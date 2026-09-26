using Canis.utils.ids;
using tuber.canis.archetypes.RootbotTraitArchetypes;
using tuber.canis.archetypes.VagabondArchetypes;
using tuber.canis.data.matchinitdata;

namespace RootEngineProbe;

internal static class NativeClockwork
{
    private static ArchetypeID[] Traits(int faction) => faction switch
    {
        10 => new[] { MechanicalMarquiseBlitz.archID, MechanicalMarquiseFortified.archID, MechanicalMarquiseHospitals.archID, MechanicalMarquiseIronWill.archID },
        11 => new[] { ElectricEyrieNobility.archID, ElectricEyrieRelentless.archID, ElectricEyrieSwoop.archID, ElectricEyrieWarTax.archID },
        12 => new[] { AutomatedAllianceInformants.archID, AutomatedAlliancePopularity.archID, AutomatedAllianceVeterans.archID, AutomatedAllianceWildfire.archID },
        13 => new[] { VagabotAdventurer.archID, VagabotBerserker.archID, VagabotHelper.archID, VagabotMarksman.archID },
        _ => Array.Empty<ArchetypeID>()
    };
    public static ArchetypeID Character(int value) => value switch
    {
        1 => VagabotTinkerArchetype.archID,
        2 => VagabotThiefArchetype.archID,
        3 => VagabotRangerArchetype.archID,
        _ => throw new InvalidDataException("Unsupported Vagabot character.")
    };
    public static void Apply(ClockworkConfig config, int faction, int[] traits, int character)
    {
        var options = Traits(faction);
        foreach (var trait in traits) config.traits.Add(options[trait]);
        if (faction == 13) config.character = Character(character);
    }
    public static int[] ReadTraits(TuberPlayerMatchInitData player)
    {
        var options = Traits((int)player.Faction);
        if (player.ClockworkConfig?.traits is not { } traits) return Array.Empty<int>();
        return Enumerable.Range(0, options.Length).Where(index => traits.ToArray().Any(trait => trait.ToString() == options[index].ToString())).ToArray();
    }
    public static int ReadCharacter(IReadOnlyList<TuberPlayerMatchInitData> players)
    {
        var player = players.FirstOrDefault(player => (int)player.Faction == 13);
        if (player?.ClockworkConfig?.character is not { } character) return 1;
        for (var index = 1; index <= 3; index++) if (Character(index).ToString() == character.ToString()) return index;
        throw new InvalidDataException("This saved match uses an unsupported Vagabot character.");
    }
}
