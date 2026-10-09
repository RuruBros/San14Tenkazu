using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// The game's own map-label art (0002\ScreenLayout sprites, read-only): the shield behind a city's
// country mark and a county's name, the gate flag, and small icons. Sprites are white or light shapes;
// they are multiplied by a colour (the force's, or the outline's) when drawn. Until the sprites are
// loaded, or without the game, the map draws plain shapes instead.
internal static class MapArt
{
    // Every icon is drawn as the game draws it: the detailed white glyph with its dark lines (not the soft _base
    // silhouette) tinted in the force's colour, over its outline in the rim colour. The shield:
    internal const string Shield = "000_cmn_prt_flag_default", ShieldOutline = "000_cmn_prt_flag_default_outline";
    // A gate's flag: the rounded square (…_seki_outline) in the rim colour behind the dark-edged gate (…_seki)
    // in the force's colour.
    internal const string GateFlag = "000_cmn_prt_flag_seki", GateOutline = "000_cmn_prt_flag_seki_outline";
    internal const string Soldier = "000_cmn_icon_map_heishi_fuchi", Supplies = "000_cmn_icon_map_hyourou_fuchi";
    internal const string Port = "000_cmn_icon_chikei_10port";
    // The game's own map icons for a city (a gatehouse, white with a dark rim) and a gate (white).
    internal const string City = "000_cmn_icon_kyoten_fuchi", GateIcon = "000_cmn_icon_chikei_09gate";

    // The game's building types, as its own table in memory lists them (2026-10-01; the index is the type):
    // name, kind and full durability. The durabilities match the built facilities read from the game.
    internal enum FacilityKind { Structure, Wall, Trap }

    internal sealed record FacilityType(string Name, FacilityKind Kind, int Durability);

    private static readonly Dictionary<int, FacilityType> FacilityTypes = new()
    {
        [3] = new("진", FacilityKind.Structure, 400), [4] = new("요새", FacilityKind.Structure, 600), [5] = new("망루", FacilityKind.Structure, 300),
        [6] = new("투석대", FacilityKind.Structure, 400), [7] = new("군악대", FacilityKind.Structure, 300), [8] = new("석병팔진", FacilityKind.Structure, 300),
        [9] = new("토성", FacilityKind.Wall, 500), [10] = new("석벽", FacilityKind.Wall, 800), [11] = new("불함정", FacilityKind.Trap, 15),
        [12] = new("맹화함정", FacilityKind.Trap, 30), [13] = new("화선", FacilityKind.Trap, 20), [14] = new("구덩이", FacilityKind.Trap, 20),
        [15] = new("망대", FacilityKind.Structure, 200), [16] = new("철쇄", FacilityKind.Wall, 350), [17] = new("낙석", FacilityKind.Trap, 20),
    };

    internal static FacilityType? Type(int kind) => FacilityTypes.GetValueOrDefault(kind);

    // The game's icons by building type, drawn as the game draws them: the detailed glyph (white with dark
    // lines) tinted in the builder's colour over its outline in the rim colour. (The _base sprites are soft
    // silhouettes without the detail.) 망대 and 낙석 have no outline sprite: their soft silhouette, in the rim
    // colour, is the rim. Walls have no icon on the game's map, only their name.
    private static readonly Dictionary<int, (string Base, string? Outline)> Facilities = new()
    {
        [3] = ("000_cmn_icon_facility_00jin", "000_cmn_icon_facility_00jin_outline"),
        [4] = ("000_cmn_icon_facility_01toride", "000_cmn_icon_facility_01toride_outline"),
        [5] = ("000_cmn_icon_facility_02yumiyagura", "000_cmn_icon_facility_02yumiyagura_outline"),
        [6] = ("000_cmn_icon_facility_03touseki", "000_cmn_icon_facility_03touseki_outlinei"),
        [7] = ("000_cmn_icon_facility_04gungaku", "000_cmn_icon_facility_04gungaku_outline"),
        [8] = ("000_cmn_icon_facility_05sekihei", "000_cmn_icon_facility_05sekihei_outline"),
        [11] = ("000_cmn_icon_tpap_00hiwana", "000_cmn_icon_tpap_00hiwana_outline"),
        [12] = ("000_cmn_icon_tpap_01mouka", "000_cmn_icon_tpap_01mouka_outline"),
        [13] = ("000_cmn_icon_tpap_02kasen", "000_cmn_icon_tpap_02kasen_outline"),
        [14] = ("000_cmn_icon_trap_03ana", "000_cmn_icon_trap_03ana_outline"),
        [15] = ("000_cmn_icon_bt_12doyagura", "000_cmn_icon_trap_doyagura_base"),
        [17] = ("000_cmn_icon_bt_13rakuseki", "000_cmn_icon_trap_rakuseki_base"),
    };

    // A unit's 진형, as the game's formation icons (jin_fuchi_1~14, white with a dark rim). 1~13 follow the
    // formation table's order (어린 ▲, 봉시 ↑, 안행 V, 방원 ○, … 정란, 충차, 투석, 주가, 몽충, 누선); which of the Λ, U
    // and S icons (5~7) is 학익, 장사 or 추행 is not confirmed. 사이 is the helmet (jin_15), transports the cart
    // (jin_fuchi_14) on land and the small boat on water.
    internal static string? FormationIcon(int formation) => formation switch
    {
        >= 1 and <= 13 => $"000_cmn_icon_jin_fuchi_{formation}",
        14 => "000_cmn_icon_jin_15_fuchi",
        19 => "000_cmn_icon_jin_fuchi_14",
        17 or 18 or 20 => "000_cmn_icon_jin_fuchi_11",
        _ => null
    };

    // A facility still going up: the game's hammer, small, on its icon's corner.
    internal const string Build = "000_cmn_icon_facility_build_fuchi";

    // 명소 (scenic spots): the game's pavilion in the colour of the territory it stands in.
    internal const string Scenic = "000_cmn_icon_scenic", ScenicOutline = "000_cmn_icon_scenic_outline";

    // The game's order icons, shown where a unit is going: a boot for moving, crossed swords for an attack,
    // an arrow into a gate for going back to a base.
    internal const string MoveOrder = "000_cmn_icon_bt_kidou", AttackOrder = "000_cmn_icon_bt_battle", ReturnOrder = "000_cmn_icon_bt_nyujo";

    internal static (string Base, string? Outline)? Facility(int kind) => Facilities.TryGetValue(kind, out var names) ? names : null;

    internal static string FacilityName(int kind) => Type(kind)?.Name ?? L.Terms.Facility;

    private static readonly string[] Names =
        [Shield, ShieldOutline, GateFlag, GateOutline, Soldier, Supplies, Port, City, GateIcon, MoveOrder, AttackOrder, ReturnOrder, Scenic, ScenicOutline, Build,
         .. Enumerable.Range(1, 20).Select(FormationIcon).OfType<string>().Distinct(),
         .. Facilities.Values.SelectMany(f => new[] { f.Base, f.Outline }).OfType<string>()];

    private static volatile Dictionary<string, (byte[] Pixels, int Width, int Height)> sprites = [];
    private static readonly ConcurrentDictionary<(string Name, int Tint), WMI.BitmapSource> tinted = new();
    private static readonly ConcurrentDictionary<string, WMI.BitmapSource> originals = new();

    internal static event Action? Changed;

    // Called once the game's sprites are indexed; runs on a background thread.
    internal static void Load(FaceLibrary art)
    {
        var loaded = new Dictionary<string, (byte[], int, int)>();
        foreach (string name in Names)
        {
            using Bitmap? sprite = art.Sprite(name);
            if (sprite is not null) loaded[name] = (PixelsOf(sprite), sprite.Width, sprite.Height);
        }
        sprites = loaded;
        tinted.Clear();
        originals.Clear();
        Changed?.Invoke();
    }

    internal static bool Has(string name) => sprites.ContainsKey(name);

    // The sprite multiplied by a colour: white parts take the colour, dark edges stay dark. Null when missing.
    internal static WMI.BitmapSource? Get(string name, Color tint)
    {
        if (!sprites.TryGetValue(name, out var sprite)) return null;
        return tinted.GetOrAdd((name, tint.ToArgb()), _ => Tinted(sprite.Pixels, sprite.Width, sprite.Height, tint));
    }

    internal static bool Draw(IMapCanvas canvas, string name, Color tint, System.Windows.Rect area)
    {
        if (canvas is IGpuMapCanvas gpu)
        {
            if (!sprites.TryGetValue(name, out var sprite)) return false;
            WMI.BitmapSource original = originals.GetOrAdd(name, _ =>
            {
                var image = WMI.BitmapSource.Create(sprite.Width, sprite.Height, 96, 96, WM.PixelFormats.Bgra32, null, sprite.Pixels, sprite.Width * 4);
                image.Freeze();
                return image;
            });
            gpu.DrawTinted(original, tint, area);
            return true;
        }
        if (Get(name, tint) is not { } cpu) return false;
        canvas.DrawImage(cpu, area);
        return true;
    }

    // A bitmap's pixels as BGRA with a stride of width * 4.
    internal static byte[] PixelsOf(Bitmap bitmap)
    {
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return pixels;
    }

    // BGRA pixels multiplied by a colour, as a frozen WPF image (the pixels given are left as they are).
    internal static WMI.BitmapSource Tinted(byte[] source, int width, int height, Color tint)
    {
        byte[] pixels = (byte[])source.Clone();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * tint.B / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * tint.G / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * tint.R / 255);
        }
        var image = WMI.BitmapSource.Create(width, height, 96, 96, WM.PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
