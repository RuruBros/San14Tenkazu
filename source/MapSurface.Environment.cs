using San14Tenkazu.EnvironmentRules;
using W=System.Windows;
using WC=System.Windows.Controls;
using WM=System.Windows.Media;
using WMI=System.Windows.Media.Imaging;
namespace San14Tenkazu;
internal sealed partial class MapSurface
{
    private readonly WC.Image environmentFill=Layer(WM.BitmapScalingMode.Linear);
    private readonly object environmentImageSlot=new();
    private EnvironmentOverlay? environment;
    private EnvironmentLayer environmentLayer=null!;
    private int environmentVersion;
    private EnvironmentOverlay? environmentShown;
    private GameSnapshot? environmentSnapshot;
    private readonly List<(W.Rect Area,int City,string Text)> environmentHits=[];
    private sealed class EnvironmentLayer(MapSurface map):LabelLayer(map){protected override void Draw(MapSurface map,IMapCanvas dc)=>map.DrawEnvironment(dc);}
    internal void SetEnvironment(EnvironmentOverlay? value)
    {
        environment=value;
        // Called on every control refresh: compare the overlay in place rather than formatting it into a key.
        bool sameOverlay=value is null?environmentShown is null:environmentShown is {} shown&&value.SameAs(shown);
        bool sameRegions=ReferenceEquals(snapshot,environmentSnapshot)||(snapshot is {} current&&environmentSnapshot is {} previous&&current.TileCounties.AsSpan().SequenceEqual(previous.TileCounties)&&current.Counties.Select(c=>(c.Index,c.HomeCity)).SequenceEqual(previous.Counties.Select(c=>(c.Index,c.HomeCity))));
        if(sameOverlay&&sameRegions)return;environmentShown=value;environmentSnapshot=snapshot;int version=++environmentVersion;environmentHits.Clear();
        if(value is null){environmentFill.Source=null;environmentLayer?.Redraw();RequestFrame();RefreshEnvironmentHover();return;}
        if(snapshot is not {} data){RefreshEnvironmentHover();return;}
        Task.Run(()=>MapView.RenderEnvironment(data,value)).ContinueWith(task=>Dispatcher.InvokeAsync(()=>
        {
            if(version!=environmentVersion)return;
            if(task.IsCompletedSuccessfully){environmentFill.Source=task.Result;environmentFill.Width=WorldWidth;environmentFill.Height=WorldHeight;}
            else System.Diagnostics.Trace.TraceError("Environment region image: {0}",task.Exception);
            environmentLayer.Redraw();RequestFrame();RefreshEnvironmentHover();
        }),TaskScheduler.Default);environmentLayer?.Redraw();RefreshEnvironmentHover();
    }
    private void DrawEnvironment(IMapCanvas dc)
    {
        environmentHits.Clear();if(environment is not {} value||snapshot is not {} data)return;
        double dpi=WM.VisualTreeHelper.GetDpi(this).PixelsPerDip;CheckTextDpi(dpi);
        foreach(int city in data.CityIds)
        {
            var marks=value.Marks(city).ToArray();if(marks.Length==0)continue;W.Point at=CityOnScreen(city);
            if(at.X<-100-LabelPad||at.Y<-100-LabelPad||at.X>ActualWidth+100+LabelPad||at.Y>ActualHeight+100+LabelPad)continue;
            double left=bannerRects.TryGetValue(city,out var banner)?banner.Right+5:at.X+16;
            double top=at.Y-18;
            for(int i=0;i<marks.Length;i++){
                var mark=marks[i];var color=MapView.EnvironmentColor(mark.Kind);var ink=Solid(color);var edge=Line(ink,1.3);
                var box=new W.Rect(left+i*34,top,30,36);dc.DrawRoundedRectangle(HudBack,edge,box,4,4);
                DrawEnvironmentSymbol(dc,mark.Kind,new W.Point(box.X+15,box.Y+12),ink);
                string tag=value.IsPreview(city,mark.Kind)?L.DataUi.PreviewBadge:mark.Kind<3?L.Environment.Current:EnvironmentProtocol.Date(mark.Date).Month+"/"+EnvironmentProtocol.Date(mark.Date).Day;
                var label=Mark(tag,9,HudInk,dpi);dc.DrawText(label,new W.Point(box.X+(30-label.Width)/2,box.Y+22));
                if(DescribeEnvironment(city,mark.Kind) is {} tip)environmentHits.Add((box,city,tip));
            }
        }
    }
    private int EnvironmentCityAtTile(int tile)=>snapshot is {} data&&tile>=0&&tile<data.TileCounties.Length
        ?data.County(data.TileCounties[tile])?.HomeCity??0:0;
    private string? DescribeEnvironment(int city,int kind=0,bool includeCity=true)
    {
        if(environment is not {} value||snapshot is not {} data||city is <1 or >EnvironmentProtocol.CitySlots)return null;
        if((value.UnavailableCities&(1UL<<(city-1)))!=0)return (includeCity?data.CityName(city)+"\n":"")+L.Environment.RegionUnavailable;
        var marks=value.Marks(city).Where(mark=>kind==0||mark.Kind==kind).ToArray();if(marks.Length==0)return null;
        var lines=new List<string>();
        if(includeCity)lines.Add(data.CityName(city));
        foreach(var mark in marks)
        {
            lines.Add(mark.Kind switch{1=>L.Environment.RainVisual,2=>L.Environment.SnowVisual,3=>L.Environment.Flood,4=>L.Environment.Wildfire,6=>L.Environment.Blizzard,7=>L.Environment.Heatwave,_=>L.Environment.Drought});
            var date=EnvironmentProtocol.Date(mark.Kind<3?value.Date:mark.Date);string dated=$"{date.Year}/{date.Month}/{date.Day}";
            lines.Add(value.IsPreview(city,mark.Kind)?L.DataUi.PreviewTooltip:mark.Kind<3?L.Environment.CurrentWeather(dated):L.Environment.RecentDamage(dated));
        }
        int movement=EnvironmentPolicy.MovementPercent(value.Weather[city-1],value.Flags,value.Values);
        lines.Add(L.Environment.OverlayValues(100-movement,(value.Flags&EnvironmentEffects.WeatherMorale)!=0&&value.Weather[city-1]!=0?value.Values.WeatherMoraleLoss:0));
        return string.Join("\n",lines);
    }
    // Region information is hover-only: ordinary hit tests still control clicks and double-click navigation.
    private MapHit HoverHitTest(W.Point location)
    {
        var hit=HitTest(location);if(!hit.IsEmpty)return hit;
        WM.Matrix transform=Gpu&&presentedView.Zoom>0?presentedTransform:worldTransform.Matrix;
        if(!transform.HasInverse)return default;transform.Invert();var world=transform.Transform(location);
        return MapTerrain.TileAt(world.X,world.Y) is {} tile&&DescribeEnvironment(EnvironmentCityAtTile(tile)) is {} text
            ?new MapHit(0,null,text):default;
    }
    private void RefreshEnvironmentHover()
    {
        if(!IsMouseOver||IsMoving){SetHover(default);return;}
        var point=System.Windows.Input.Mouse.GetPosition(this);UpdateHover(point);
        if(card.Visibility==W.Visibility.Visible){cardText.Text=HoverText();PlaceCard(point);}
    }
    private static void DrawEnvironmentSymbol(IMapCanvas dc,int kind,W.Point p,WM.Brush ink)
    {
        var pen=Line(ink,1.6);
        if(kind==1){dc.DrawEllipse(ink,null,new(p.X-4,p.Y-3),4,3);dc.DrawEllipse(ink,null,new(p.X+2,p.Y-4),5,4);dc.DrawLine(pen,new(p.X-5,p.Y+2),new(p.X-7,p.Y+6));dc.DrawLine(pen,new(p.X+1,p.Y+2),new(p.X-1,p.Y+6));}
        else if(kind==2){for(int i=0;i<3;i++){double a=i*Math.PI/3;var d=new W.Vector(Math.Cos(a)*7,Math.Sin(a)*7);dc.DrawLine(pen,p-d,p+d);}}
        else if(kind==3){for(int y=-4;y<=4;y+=4){dc.DrawLine(pen,new(p.X-8,p.Y+y),new(p.X-2,p.Y+y+2));dc.DrawLine(pen,new(p.X-2,p.Y+y+2),new(p.X+4,p.Y+y));dc.DrawLine(pen,new(p.X+4,p.Y+y),new(p.X+8,p.Y+y+2));}}
        else if(kind==4){dc.DrawGeometryAt(ink,null,EnvironmentFlame,p);}
        else if(kind==6){for(int i=0;i<3;i++){double a=i*Math.PI/3;var d=new W.Vector(Math.Cos(a)*6,Math.Sin(a)*6);dc.DrawLine(pen,p-d,p+d);}dc.DrawLine(pen,new(p.X-8,p.Y+8),new(p.X+8,p.Y+8));dc.DrawLine(pen,new(p.X-8,p.Y+5),new(p.X-3,p.Y+5));}
        else if(kind==7){dc.DrawEllipse(ink,null,new(p.X,p.Y+5),3,3);dc.DrawRoundedRectangle(null,pen,new(p.X-2,p.Y-8,4,13),2,2);dc.DrawLine(pen,new(p.X,p.Y-4),new(p.X,p.Y+5));dc.DrawLine(pen,new(p.X+5,p.Y-5),new(p.X+8,p.Y-5));dc.DrawLine(pen,new(p.X+5,p.Y-1),new(p.X+8,p.Y-1));}
        else{dc.DrawEllipse(null,pen,p,4,4);for(int i=0;i<8;i++){double a=i*Math.PI/4;var d=new W.Vector(Math.Cos(a),Math.Sin(a));dc.DrawLine(pen,p+d*6,p+d*8);}}
    }
    private static readonly WM.Geometry EnvironmentFlame=MakeEnvironmentFlame();
    private static WM.Geometry MakeEnvironmentFlame(){var shape=WM.Geometry.Parse("M 0,-9 C 9,-1 10,6 3,8 C -6,11 -10,2 -5,-4 C -5,1 -1,3 0,-9 Z");shape.Freeze();return shape;}
}
