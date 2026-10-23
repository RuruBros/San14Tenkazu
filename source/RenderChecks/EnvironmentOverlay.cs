using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using San14Tenkazu.EnvironmentRules;
using W=System.Windows;
using WC=System.Windows.Controls;
using WM=System.Windows.Media;
using WMI=System.Windows.Media.Imaging;
using WT=System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckEnvironmentOverlay(string output)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("New evidence directory required");Directory.CreateDirectory(output);
        using var log=new StreamWriter(Path.Combine(output,"checks.jsonl")){AutoFlush=true};
        void Check(string name,bool passed){log.WriteLine(JsonSerializer.Serialize(new{name,passed}));Require(passed,name);}
        static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,Hidden)!.GetValue(o)!;
        static object? Call(object o,string name,params object?[] args)=>(o.GetType().GetMethod(name,Hidden|System.Reflection.BindingFlags.DeclaredOnly)??o.GetType().GetMethod(name,Hidden))!.Invoke(o,args);
        static void Pump(Func<bool> done){var timer=Stopwatch.StartNew();do{var frame=new WT.DispatcherFrame();WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background,new Action(()=>frame.Continue=false));WT.Dispatcher.PushFrame(frame);if(timer.ElapsedMilliseconds>15000)throw new TimeoutException("Environment raster did not complete");Thread.Sleep(5);}while(!done());}
        var old=Environment.GetEnvironmentVariable("SAN14_NO_GPU");Environment.SetEnvironmentVariable("SAN14_NO_GPU","1");
        var map=new MapSurface();var host=new W.Window{Content=map,Width=1000,Height=750,ShowActivated=false,ShowInTaskbar=false};
        try{
            host.Show();var data=OriginalMapCastles(new TestGame().Force(1,1,1).Officer(1,"환경 검사 장수",1,1,4).Leading(1).Gate(52,0).Gate(53,0)).Build();data.Counties[0].Data[CountyFields.HomeCityOffset]=1;data.Counties[1].Data[CountyFields.HomeCityOffset]=2;
            for(int tile=0;tile<data.TileCounties.Length;tile++)data.TileCounties[tile]=(ushort)(tile%220<110?52:53);
            int unitTile=110*220+55;System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.Units[0].Data.AsSpan(UnitFields.TileOffset),(ushort)unitTile);
            uint date=192|(6u<<16)|(21u<<24);var weather=new byte[46];weather[0]=2;weather[1]=8;var recent=new uint[230];for(int k=0;k<5;k++)recent[k]=date;
            var overlay=new EnvironmentOverlay(date,EnvironmentEffects.All,EnvironmentValues.Default,weather,recent);
            map.SetEnvironment(overlay);map.SetSnapshot(data);Pump(()=>Field<WC.Image>(map,"environmentFill").Source is not null);
            var fill=(WMI.BitmapSource)Field<WC.Image>(map,"environmentFill").Source;byte[] pixels=new byte[fill.PixelWidth*fill.PixelHeight*4];fill.CopyPixels(pixels,fill.PixelWidth*4,0);
            Check("actual-region-wash-has-transparent-and-tinted-pixels",pixels.Where((_,i)=>i%4==3).Any(a=>a==45)&&pixels.Where((_,i)=>i%4==3).Any(a=>a==0));
            var layer=Field<W.FrameworkElement>(map,"environmentLayer");layer.Measure(new(1000,750));layer.Arrange(new(0,0,1000,750));
            Call(layer,"Redraw");var hits=Field<List<(W.Rect Area,int City,string Text)>>(map,"environmentHits").ToArray();
            Check("current-weather-and-five-recent-damage-types",hits.Length==7&&hits.Count(h=>h.Text.Contains(L.Environment.RecentDamage("192/6/21")))==5);
            Check("numeric-tooltip-and-current-weather-date",hits.Any(h=>h.Text.Contains(L.Environment.CurrentWeather("192/6/21"))&&h.Text.Contains("192/6/21")));
            Check("map-values-use-ten-day-unit",hits.All(h=>h.Text.Contains("/ 10일")&&!h.Text.Contains("/순")));
            foreach(var hit in hits)Check("fixed-size-marker-"+hit.City+"-"+hit.Area.X,hit.Area.Width==30&&hit.Area.Height==36);
            static object? Property(object value,string name)=>value.GetType().GetProperty(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic)!.GetValue(value);
            static bool Empty(object hit)=>(bool)Property(hit,"IsEmpty")!;
            W.Point EmptyPoint(int county)=>Enumerable.Range(0,data.TileCounties.Length).Where(tile=>data.TileCounties[tile]==county)
                .Select(tile=>(W.Point)Call(map,"TilePoint",tile)!).First(p=>p.X>20&&p.Y>80&&p.X<map.ActualWidth-20&&p.Y<map.ActualHeight-20&&Empty(Call(map,"HitTest",p)!));
            var regionPoint=EmptyPoint(52);var regionHit=Call(map,"HoverHitTest",regionPoint)!;
            Check("region-hover-shows-current-weather-and-five-recent-disasters",Property(regionHit,"Spot") is string region&&region.Contains(L.Environment.RainVisual)&&region.Contains(L.Environment.Flood)&&region.Contains(L.Environment.Heatwave)&&region.Contains(L.Environment.CurrentWeather("192/6/21")));
            Check("region-hover-does-not-create-city-selection",(int)Property(regionHit,"City")! ==0&&Empty(Call(map,"HitTest",regionPoint)!));
            bool emptyClicked=false,cityClicked=false;map.EmptyClicked+=()=>emptyClicked=true;map.CityClicked+=_=>cityClicked=true;Call(map,"ClickAt",regionPoint);
            Check("region-click-retains-empty-map-action",emptyClicked&&!cityClicked);
            Call(map,"SetHover",regionHit);Call(map,"PlaceCard",regionPoint);map.UpdateLayout();
            var card=Field<WC.Border>(map,"card");var cardText=Field<WC.TextBlock>(map,"cardText");
            var cardBounds=card.TransformToAncestor(map).TransformBounds(new W.Rect(new W.Point(),card.RenderSize));
            Check("region-hover-reuses-existing-map-card",card.Visibility==W.Visibility.Visible&&!card.IsHitTestVisible&&cardText.TextWrapping==W.TextWrapping.Wrap&&cardBounds.Left>=4&&cardBounds.Right<=map.ActualWidth-4+.01&&cardBounds.Bottom<=map.ActualHeight-4+.01);
            var unitPoint=(W.Point)Call(map,"TilePoint",unitTile)!;var unitHit=Call(map,"HitTest",unitPoint)!;Call(map,"SetHover",unitHit);
            Check("unit-hover-keeps-unit-details-and-adds-local-weather",Property(unitHit,"Unit") is not null&&cardText.Text.Contains("환경 검사 장수")&&cardText.Text.Contains(L.Environment.RainVisual));
            Call(map,"SetHover",regionHit);Call(map,"PlaceCard",regionPoint);map.UpdateLayout();
            var bitmap=new WMI.RenderTargetBitmap(1000,750,96,96,WM.PixelFormats.Pbgra32);bitmap.Render(map);var encoder=new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,"overview.png")))encoder.Save(stream);
            var next=overlay with{Date=192|(7u<<16)|(1u<<24)};map.SetEnvironment(next);
            string nextText=(string)Property(Call(map,"HoverHitTest",regionPoint)!,"Spot")!;
            Check("region-hover-refreshes-date-and-expires-ten-day-damage",nextText.Contains(L.Environment.CurrentWeather("192/7/1"))&&!nextText.Contains(L.Environment.RecentDamage("192/6/21")));
            map.SetEnvironment(overlay with{Weather=new byte[46],RecentDates=new uint[230]});
            Check("clear-region-has-no-weather-card",Empty(Call(map,"HoverHitTest",regionPoint)!));
            map.SetEnvironment(overlay);Pump(()=>Field<WC.Image>(map,"environmentFill").Source is not null);Call(layer,"Redraw");
            var view=new MapView();typeof(MapView).GetField("snapshot",Hidden)!.SetValue(view,data);view.SetEnvironment(overlay);Call(view,"BuildLegend");
            var legend=Field<WC.WrapPanel>(view,"legend");
            Check("force-legend-keeps-chips-without-weather-paragraphs",legend.Children.Count>0&&legend.Children.Cast<W.UIElement>().All(child=>child is WC.Border));
            view.SetEnvironment(overlay with{PreviewCity=1,PreviewKind=3});
            Check("preview-only-warning-remains-visible",legend.Children.OfType<WC.TextBlock>().Any(text=>text.Text.Contains("표시 전용 테스트")));
            view.SetEnvironment(null);
            typeof(MapSurface).GetField("zoom",Hidden)!.SetValue(map,8d);var world=typeof(MapSurface).GetMethod("TileWorld",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,[MapTerrain.CityTiles[1]])!;
            typeof(MapSurface).GetField("center",Hidden)!.SetValue(map,world);Call(map,"ApplyView");Call(layer,"Redraw");
            Check("zoom-eight-keeps-marker-size",Field<List<(W.Rect Area,int City,string Text)>>(map,"environmentHits").Where(h=>h.City==1).All(h=>h.Area.Width==30&&h.Area.Height==36));
            map.SetEnvironment(null);Pump(()=>Field<WC.Image>(map,"environmentFill").Source is null);Call(layer,"Redraw");
            Check("off-clears-range-and-hover",Field<List<(W.Rect Area,int City,string Text)>>(map,"environmentHits").Count==0);
            Check("off-removes-region-tooltip",Call(map,"DescribeEnvironment",1,0,true) is null);
        }finally{host.Close();Environment.SetEnvironmentVariable("SAN14_NO_GPU",old);}
        Console.WriteLine("PASS WPF environment regions, current/recent dates, numeric hover, fixed-size overview/zoom, OFF");
    }
}
