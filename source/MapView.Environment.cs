using San14Tenkazu.EnvironmentRules;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WMI=System.Windows.Media.Imaging;
namespace San14Tenkazu;
internal sealed partial class MapView
{
    private EnvironmentOverlay? environment;
    internal void SetEnvironment(EnvironmentOverlay? value){var preview=environment?.PreviewCity;environment=value;surface.SetEnvironment(value);if(preview!=value?.PreviewCity)BuildLegend();}
    private void AddEnvironmentPreviewLegend()
    {
        if(environment is not {} value)return;
        if(value.PreviewCity>0)legend.Children.Add(new System.Windows.Controls.TextBlock{Text=L.DataUi.PreviewLegend,Foreground=CardSkin.Ink});
    }
    internal static Color EnvironmentColor(int kind)=>kind switch{1=>Color.FromArgb(92,176,232),2=>Color.FromArgb(222,242,255),3=>Color.FromArgb(65,150,245),4=>Color.FromArgb(247,135,64),6=>Color.FromArgb(113,210,240),7=>Color.FromArgb(243,91,80),_=>Color.FromArgb(224,187,69)};
    internal static WMI.BitmapSource RenderEnvironment(GameSnapshot data,EnvironmentOverlay value)
    {
        var shade=new byte[GameMemory.TileCount];var regions=new int[501];foreach(var county in data.Counties)if(county.Index is >0 and <=500)regions[county.Index]=county.HomeCity;
        for(int tile=0;tile<shade.Length;tile++){int county=data.TileCounties[tile];if(county is >0 and <=500)shade[tile]=(byte)value.Shade(regions[county]);}
        View view=FillView();using var bitmap=new Bitmap(view.Width,view.Height,PixelFormat.Format32bppPArgb);using Graphics g=Graphics.FromImage(bitmap);
        foreach(var pair in OwnerAreas(shade,view,data.Terrain??MapTerrain.Terrain)){using var brush=new SolidBrush(Color.FromArgb(45,EnvironmentColor(pair.Key)));g.FillPath(brush,pair.Value);pair.Value.Dispose();}
        return ToImage(bitmap);
    }
}
